using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Monster spawn tuning が読む共通ステージ進行度。
/// </summary>
public struct StageSpawnProgress : IComponentData
{
    public float Value;
}

/// <summary>
/// ステージのクリア状態。
/// </summary>
public struct StageClearState : IComponentData
{
    public byte IsCleared;
}

/// <summary>
/// 時間上限でクリアする生存ステージの状態。
/// </summary>
public struct TimedSurvivalStageProgress : IComponentData
{
    public float TimeLimitSeconds;
    public float ElapsedSeconds;
}

/// <summary>
/// Monster 討伐数でクリアするステージの状態。
/// </summary>
public struct KillCountStageProgress : IComponentData
{
    public int TargetKillCount;
    public int CurrentKillCount;
}

/// <summary>
/// Boss 残り HP でクリアするステージの状態。
/// </summary>
public struct BossHealthStageProgress : IComponentData
{
    public Entity BossEntity;
    public int MaxHp;
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
/// </summary>
[UpdateBefore(typeof(MonsterSpawnDirectorSystem))]
public partial struct TimedSurvivalStageSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
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
/// 討伐数ステージの共通進行度とクリア状態を更新する。
/// </summary>
[UpdateAfter(typeof(MonsterDestroySystem))]
[UpdateBefore(typeof(MonsterSpawnDirectorSystem))]
public partial struct KillCountStageSystem : ISystem
{
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
    public void OnUpdate(ref SystemState state)
    {
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
                state.EntityManager.Exists(stage.ValueRO.BossEntity) &&
                state.EntityManager.HasComponent<HealthComponent>(stage.ValueRO.BossEntity))
            {
                var health = state.EntityManager.GetComponentData<HealthComponent>(stage.ValueRO.BossEntity);

                currentHp = health.CurrentHp;
                maxHp = math.max(health.MaxHp, maxHp);
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
