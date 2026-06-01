using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Monster spawn tuning が読む共通ステージ進行度。
///
/// 各クリア条件は異なる component を持つが、MonsterSpawnDirectorSystem はこの 0..1 の値だけを読む。
/// </summary>
public struct StageSpawnProgress : IComponentData
{
    /// <summary>
    /// 0 = stage 開始付近、1 = clear 付近。範囲外値は計算側で丸める。
    /// </summary>
    public float Value;
}

/// <summary>
/// ステージのクリア状態。
/// </summary>
public struct StageClearState : IComponentData
{
    /// <summary>
    /// 0 = not cleared, 1 = cleared。clear 後は latch して戻さない。
    /// </summary>
    public byte IsCleared;
}

/// <summary>
/// 時間上限でクリアする生存ステージの状態。
/// </summary>
public struct TimedSurvivalStageProgress : IComponentData
{
    /// <summary>
    /// クリアまでの制限時間秒。
    /// </summary>
    public float TimeLimitSeconds;

    /// <summary>
    /// 進行済み秒数。clear 後は増やさない。
    /// </summary>
    public float ElapsedSeconds;
}

/// <summary>
/// Monster 討伐数でクリアするステージの状態。
/// </summary>
public struct KillCountStageProgress : IComponentData
{
    /// <summary>
    /// クリアに必要な討伐数。
    /// </summary>
    public int TargetKillCount;

    /// <summary>
    /// 現在討伐数。MonsterDestroySystem が死亡確定時に加算する。
    /// </summary>
    public int CurrentKillCount;
}

/// <summary>
/// Boss 残り HP でクリアするステージの状態。
/// </summary>
public struct BossHealthStageProgress : IComponentData
{
    /// <summary>
    /// 監視する boss entity。Entity.Null の場合は CurrentHp / MaxHp を直接使う。
    /// </summary>
    public Entity BossEntity;

    /// <summary>
    /// Boss HP bar 表示用の最大 HP。
    /// </summary>
    public int MaxHp;

    /// <summary>
    /// Boss HP bar 表示用の現在 HP。BossEntity が有効なら毎 frame 同期される。
    /// </summary>
    public int CurrentHp;
}

/// <summary>
/// ステージ進行度の純粋計算。
/// </summary>
public static class StageProgressMath
{
    /// <summary>
    /// 生存ステージの補充用進行度を返す。
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
    /// 討伐ステージの補充用進行度を返す。
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
    /// 討伐数を非負差分で加算した次の値を返す。
    /// </summary>
    public static int AddKillCount(int currentKillCount, int deltaKillCount)
    {
        return math.max(0, currentKillCount) + math.max(0, deltaKillCount);
    }

    /// <summary>
    /// Boss 残り HP から、0..1 の HP 減少率を返す。
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
    /// 生存ステージがクリア済みかどうかを返す。
    /// </summary>
    public static bool IsTimedSurvivalStageCleared(float elapsedSeconds, float timeLimitSeconds)
    {
        return timeLimitSeconds > 0f && elapsedSeconds >= timeLimitSeconds;
    }

    /// <summary>
    /// 討伐ステージがクリア済みかどうかを返す。
    /// </summary>
    public static bool IsKillCountStageCleared(int currentKillCount, int targetKillCount)
    {
        return targetKillCount > 0 && currentKillCount >= targetKillCount;
    }

    /// <summary>
    /// Boss ステージがクリア済みかどうかを返す。
    /// </summary>
    public static bool IsBossStageCleared(int currentHp)
    {
        return currentHp <= 0;
    }

    /// <summary>
    /// 一度 clear した stage を未 clear に戻さない。
    /// </summary>
    public static byte LatchClearState(byte currentClearState, bool shouldClear)
    {
        return currentClearState != 0 || shouldClear ? (byte)1 : (byte)0;
    }
}

/// <summary>
/// 生存ステージの経過時間を更新する。
///
/// timeScale 停止中や Warmup 中は、この値も進まない。
/// カード選択 phase ではステージ時間とモンスター更新を両方止める。
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
/// StageProgress が置かれた scene だけで、各ステージ条件から clear 状態を確定する。
///
/// clear 状態は latch され、一度 clear になった stage は同じ play session 中に未 clear へ戻らない。
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
/// 討伐数ステージの共通進行度とクリア状態を更新する。
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
/// Boss 残り HP から共通進行度とクリア状態を更新する。
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
