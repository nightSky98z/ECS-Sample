using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// ステージの進み具合（0〜1）。クリア条件の種類に関係なく共通の値。
/// クリア条件（時間・討伐数・ボス）ごとに状態の Component は異なるが、
/// モンスターの出現処理はこの値だけを読めばよいので、クリア条件の種類を知らなくて済む。
/// </summary>
public struct StageSpawnProgress : IComponentData
{
    /// <summary>
    /// 0 = ステージ開始直後、1 = クリア直前。
    /// </summary>
    public float Value;
}

/// <summary>
/// ステージのクリア状態。
/// </summary>
public struct StageClearState : IComponentData
{
    /// <summary>
    /// 1 ならクリア済み。一度クリアしたら 0 には戻さない。
    /// </summary>
    public byte IsCleared;
}

/// <summary>
/// 「制限時間まで生き残るとクリア」のステージの状態。
/// </summary>
public struct TimedSurvivalStageProgress : IComponentData
{
    /// <summary>
    /// クリアまでの制限時間（秒）。
    /// </summary>
    public float TimeLimitSeconds;

    /// <summary>
    /// 経過時間（秒）。クリア後は増やさない。
    /// </summary>
    public float ElapsedSeconds;
}

/// <summary>
/// 「規定数のモンスターを倒すとクリア」のステージの状態。
/// </summary>
public struct KillCountStageProgress : IComponentData
{
    /// <summary>
    /// クリアに必要な討伐数。
    /// </summary>
    public int TargetKillCount;

    /// <summary>
    /// 現在の討伐数。モンスターが倒されたときに MonsterDestroySystem が加算する。
    /// </summary>
    public int CurrentKillCount;
}

/// <summary>
/// 「ボスを倒すとクリア」のステージの状態。
/// </summary>
public struct BossHealthStageProgress : IComponentData
{
    /// <summary>
    /// ボスの Entity。設定されていなければ、下の CurrentHp / MaxHp の値をそのまま使う。
    /// </summary>
    public Entity BossEntity;

    /// <summary>
    /// ボスの最大 HP（HP バーの表示にも使う）。
    /// </summary>
    public int MaxHp;

    /// <summary>
    /// ボスの現在の HP。ボスの Entity が設定されていれば、毎フレームその HP をコピーする。
    /// </summary>
    public int CurrentHp;
}

/// <summary>
/// ステージの進み具合とクリア判定の計算。状態を持たない関数だけなので、単体テストしやすい。
/// </summary>
public static class StageProgressMath
{
    /// <summary>
    /// 生き残りステージの進み具合（経過時間 / 制限時間）を返す。
    /// </summary>
    public static float CalculateTimedSurvivalProgress(float elapsedSeconds, float timeLimitSeconds)
    {
        if (timeLimitSeconds <= 0f)
        {
            return 0f;
        }

        return math.saturate(math.max(0f, elapsedSeconds) / timeLimitSeconds);
    }

    /// <summary>
    /// 討伐ステージの進み具合（討伐数 / 目標数）を返す。
    /// </summary>
    public static float CalculateKillCountProgress(int currentKillCount, int targetKillCount)
    {
        if (targetKillCount <= 0)
        {
            return 0f;
        }

        return math.saturate((float)math.max(0, currentKillCount) / targetKillCount);
    }

    /// <summary>
    /// 討伐数を加算した値を返す（負の値は 0 として扱う）。
    /// </summary>
    public static int AddKillCount(int currentKillCount, int deltaKillCount)
    {
        return math.max(0, currentKillCount) + math.max(0, deltaKillCount);
    }

    /// <summary>
    /// ボスの HP がどれだけ減ったか（0〜1）を、進み具合として返す。
    /// </summary>
    public static float CalculateBossHealthProgress(int currentHp, int maxHp)
    {
        if (maxHp <= 0)
        {
            return 0f;
        }

        var currentHpRate = math.clamp((float)currentHp / maxHp, 0f, 1f);

        return 1f - currentHpRate;
    }

    /// <summary>
    /// 生き残りステージをクリアしたかを返す。
    /// </summary>
    public static bool IsTimedSurvivalStageCleared(float elapsedSeconds, float timeLimitSeconds)
    {
        return timeLimitSeconds > 0f && elapsedSeconds >= timeLimitSeconds;
    }

    /// <summary>
    /// 討伐ステージをクリアしたかを返す。
    /// </summary>
    public static bool IsKillCountStageCleared(int currentKillCount, int targetKillCount)
    {
        return targetKillCount > 0 && currentKillCount >= targetKillCount;
    }

    /// <summary>
    /// ボスステージをクリアしたかを返す。
    /// </summary>
    public static bool IsBossStageCleared(int currentHp)
    {
        return currentHp <= 0;
    }

    /// <summary>
    /// クリア状態を更新する。一度クリアしたら、その後条件を満たさなくなってもクリアのままにする。
    /// </summary>
    public static byte LatchClearState(byte currentClearState, bool shouldClear)
    {
        return currentClearState != 0 || shouldClear ? (byte)1 : (byte)0;
    }
}

/// <summary>
/// 生き残りステージの経過時間を進め、進み具合とクリア状態を更新する。
/// 準備中（Warmup）や Time.timeScale が 0 の間は、経過時間も進まない。
/// 設計方針：カード選択 phase ではステージ時間とモンスター更新を両方止める。
/// </summary>
[UpdateBefore(typeof(MonsterSpawnDirectorSystem))]
public partial struct TimedSurvivalStageSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (!StageRuntimeUtility.IsGameplayPhase(runtimeState.ValueRO.Phase))
            {
                return;
            }
        }

        var deltaTime = SystemAPI.Time.DeltaTime;

        foreach (var (stage, spawnProgress, clearState) in
                 SystemAPI.Query<
                     RefRW<TimedSurvivalStageProgress>,
                     RefRW<StageSpawnProgress>,
                     RefRW<StageClearState>>())
        {
            var elapsedSeconds = stage.ValueRO.ElapsedSeconds;

            if (clearState.ValueRO.IsCleared == 0)
            {
                elapsedSeconds = math.max(
                    0f,
                    elapsedSeconds + deltaTime);
                stage.ValueRW.ElapsedSeconds = elapsedSeconds;
            }

            spawnProgress.ValueRW.Value = StageProgressMath.CalculateTimedSurvivalProgress(
                elapsedSeconds,
                stage.ValueRO.TimeLimitSeconds);
            clearState.ValueRW.IsCleared = StageProgressMath.LatchClearState(
                clearState.ValueRO.IsCleared,
                StageProgressMath.IsTimedSurvivalStageCleared(
                    elapsedSeconds,
                    stage.ValueRO.TimeLimitSeconds));
        }
    }
}

/// <summary>
/// すべての種類のステージについて、クリア条件を満たしたかを最終的に確定する。
/// 一度クリアしたステージは、同じプレイ中に未クリアへ戻らない。
/// </summary>
[UpdateAfter(typeof(TimedSurvivalStageSystem))]
[UpdateAfter(typeof(KillCountStageSystem))]
[UpdateAfter(typeof(BossHealthStageSystem))]
[UpdateBefore(typeof(MonsterSpawnDirectorSystem))]
public partial struct StageClearSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<StageClearState>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (stage, clearState) in
                 SystemAPI.Query<RefRO<TimedSurvivalStageProgress>, RefRW<StageClearState>>())
        {
            clearState.ValueRW.IsCleared = StageProgressMath.LatchClearState(
                clearState.ValueRO.IsCleared,
                StageProgressMath.IsTimedSurvivalStageCleared(
                    stage.ValueRO.ElapsedSeconds,
                    stage.ValueRO.TimeLimitSeconds));
        }

        foreach (var (stage, clearState) in
                 SystemAPI.Query<RefRO<KillCountStageProgress>, RefRW<StageClearState>>())
        {
            clearState.ValueRW.IsCleared = StageProgressMath.LatchClearState(
                clearState.ValueRO.IsCleared,
                StageProgressMath.IsKillCountStageCleared(
                    stage.ValueRO.CurrentKillCount,
                    stage.ValueRO.TargetKillCount));
        }

        foreach (var (stage, clearState) in
                 SystemAPI.Query<RefRO<BossHealthStageProgress>, RefRW<StageClearState>>())
        {
            clearState.ValueRW.IsCleared = StageProgressMath.LatchClearState(
                clearState.ValueRO.IsCleared,
                StageProgressMath.IsBossStageCleared(stage.ValueRO.CurrentHp));
        }
    }
}

/// <summary>
/// 討伐ステージの進み具合とクリア状態を更新する。
/// </summary>
[UpdateAfter(typeof(MonsterDestroySystem))]
[UpdateBefore(typeof(MonsterSpawnDirectorSystem))]
public partial struct KillCountStageSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (stage, spawnProgress, clearState) in
                 SystemAPI.Query<
                     RefRO<KillCountStageProgress>,
                     RefRW<StageSpawnProgress>,
                     RefRW<StageClearState>>())
        {
            spawnProgress.ValueRW.Value = StageProgressMath.CalculateKillCountProgress(
                stage.ValueRO.CurrentKillCount,
                stage.ValueRO.TargetKillCount);
            clearState.ValueRW.IsCleared = StageProgressMath.LatchClearState(
                clearState.ValueRO.IsCleared,
                StageProgressMath.IsKillCountStageCleared(
                    stage.ValueRO.CurrentKillCount,
                    stage.ValueRO.TargetKillCount));
        }
    }
}

/// <summary>
/// ボスの残り HP から、ボスステージの進み具合とクリア状態を更新する。
/// </summary>
[UpdateBefore(typeof(MonsterSpawnDirectorSystem))]
public partial struct BossHealthStageSystem : ISystem
{
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var healthLookup = SystemAPI.GetComponentLookup<HealthComponent>(true);

        foreach (var (stage, spawnProgress, clearState) in
                 SystemAPI.Query<
                     RefRW<BossHealthStageProgress>,
                     RefRW<StageSpawnProgress>,
                     RefRW<StageClearState>>())
        {
            if (clearState.ValueRO.IsCleared != 0)
            {
                spawnProgress.ValueRW.Value = 1f;
                continue;
            }

            var currentHp = stage.ValueRO.CurrentHp;
            var maxHp = stage.ValueRO.MaxHp;

            // ボスの Entity が設定されていれば、その HP を読み取ってコピーする。
            if (stage.ValueRO.BossEntity != Entity.Null &&
                healthLookup.HasComponent(stage.ValueRO.BossEntity))
            {
                var health = healthLookup[stage.ValueRO.BossEntity];

                currentHp = health.CurrentHp;
                maxHp = math.max(1, health.MaxHp);
                stage.ValueRW.CurrentHp = currentHp;
                stage.ValueRW.MaxHp = maxHp;
            }

            spawnProgress.ValueRW.Value = StageProgressMath.CalculateBossHealthProgress(
                currentHp,
                maxHp);
            clearState.ValueRW.IsCleared = StageProgressMath.LatchClearState(
                clearState.ValueRO.IsCleared,
                StageProgressMath.IsBossStageCleared(currentHp));
        }
    }
}
