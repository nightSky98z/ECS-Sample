using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// 経験値バランスの固定値。1 回のゲームは 7 stage、6 stage clear で Lv90 付近を調整目標にする。
/// </summary>
public static class ExperienceConstants
{
    /// <summary>
    /// 1 回のゲームで到達できる最大レベル。
    /// </summary>
    public const int MaxLevel = 99;

    /// <summary>
    /// バランス想定上の総ステージ数。
    /// </summary>
    public const int ExpectedStageCount = 7;

    /// <summary>
    /// 6 ステージクリア時点の調整目標レベル。
    /// </summary>
    public const int TargetLevelAfterStageSix = 90;

    /// <summary>
    /// Lv1 から Lv2 に必要な経験値。
    /// </summary>
    public const int BaseRequiredExperience = 50;

    /// <summary>
    /// 各レベルで必要経験値へ掛ける既定倍率。
    /// </summary>
    public const float DefaultRequiredExperienceMultiplierPerLevel = 2f;

    /// <summary>
    /// Player level 1 つごとの既定スキルダメージ増加率。
    /// </summary>
    public const float DefaultSkillDamageRatePerLevel = 0.02f;
}

/// <summary>
/// 経験値と level up の純粋計算。
///
/// System はこの helper の結果を component に書き戻すだけにし、経験値曲線の境界をここへ集約する。
/// </summary>
public static class ExperienceMath
{
    /// <summary>
    /// 既定の必要経験値曲線で Lv1 の経験値状態を作る。
    /// </summary>
    public static ExperienceComponent CreateInitialExperience()
    {
        return CreateInitialExperience(CreateDefaultLevelConfig());
    }

    /// <summary>
    /// 指定 config で Lv1 の経験値状態を作る。
    /// </summary>
    /// <param name="config">必要経験値曲線。</param>
    /// <returns>経験値 0、Lv1 の状態。</returns>
    public static ExperienceComponent CreateInitialExperience(ExperienceLevelConfig config)
    {
        return new ExperienceComponent
        {
            CurrentExperience = 0,
            RequiredExperience = CalculateRequiredExperienceForNextLevel(1, config),
            Level = 1
        };
    }

    /// <summary>
    /// プロジェクト既定の必要経験値曲線を返す。
    /// </summary>
    public static ExperienceLevelConfig CreateDefaultLevelConfig()
    {
        return new ExperienceLevelConfig
        {
            BaseRequiredExperience = ExperienceConstants.BaseRequiredExperience,
            RequiredExperienceMultiplierPerLevel = ExperienceConstants.DefaultRequiredExperienceMultiplierPerLevel
        };
    }

    /// <summary>
    /// 既定 config で、指定 level から次 level へ進むための必要経験値を返す。
    /// </summary>
    public static int CalculateRequiredExperienceForNextLevel(int level)
    {
        return CalculateRequiredExperienceForNextLevel(level, CreateDefaultLevelConfig());
    }

    /// <summary>
    /// 指定 level から次 level へ進むための必要経験値を返す。
    /// </summary>
    /// <param name="level">現在 level。範囲外は 1..MaxLevel に丸める。</param>
    /// <param name="config">必要経験値曲線。</param>
    /// <returns>MaxLevel 到達後は 0、それ以外は 1 以上。</returns>
    public static int CalculateRequiredExperienceForNextLevel(
        int level,
        ExperienceLevelConfig config)
    {
        var safeLevel = math.clamp(level, 1, ExperienceConstants.MaxLevel);

        if (safeLevel >= ExperienceConstants.MaxLevel)
        {
            return 0;
        }

        var requiredExperience = (double)math.max(1, config.BaseRequiredExperience);
        var multiplier = math.max(1f, config.RequiredExperienceMultiplierPerLevel);

        for (var levelIndex = 1; levelIndex < safeLevel; levelIndex++)
        {
            if (requiredExperience > int.MaxValue / multiplier)
            {
                return int.MaxValue;
            }

            requiredExperience *= multiplier;
        }

        return math.max(1, (int)math.ceil(requiredExperience));
    }

    /// <summary>
    /// 既定 config で経験値を加算する。level up の消費処理は行わない。
    /// </summary>
    public static ExperienceComponent AddExperience(ExperienceComponent experience, int amount)
    {
        return AddExperience(experience, amount, CreateDefaultLevelConfig());
    }

    /// <summary>
    /// 経験値を加算する。level up の消費処理は LevelUpSystem 側で別に行う。
    /// </summary>
    /// <param name="experience">加算前の経験値状態。</param>
    /// <param name="amount">加算量。0 以下は無視する。</param>
    /// <param name="config">必要経験値曲線。</param>
    /// <returns>加算後の経験値状態。</returns>
    public static ExperienceComponent AddExperience(
        ExperienceComponent experience,
        int amount,
        ExperienceLevelConfig config)
    {
        var result = NormalizeExperience(experience, config);

        if (result.Level >= ExperienceConstants.MaxLevel || amount <= 0)
        {
            return result;
        }

        var nextExperience = (long)result.CurrentExperience + amount;

        result.CurrentExperience = nextExperience > int.MaxValue
            ? int.MaxValue
            : (int)nextExperience;

        return result;
    }

    /// <summary>
    /// 既定 config で、現在経験値から可能な限り level up を進める。
    /// </summary>
    public static ExperienceComponent ProcessLevelUps(ExperienceComponent experience)
    {
        return ProcessLevelUps(experience, CreateDefaultLevelConfig());
    }

    /// <summary>
    /// 現在経験値から可能な限り level up を進める。
    /// </summary>
    /// <param name="experience">処理前の経験値状態。</param>
    /// <param name="config">必要経験値曲線。</param>
    /// <returns>level up 後の経験値状態。</returns>
    public static ExperienceComponent ProcessLevelUps(
        ExperienceComponent experience,
        ExperienceLevelConfig config)
    {
        var result = NormalizeExperience(experience, config);

        while (result.Level < ExperienceConstants.MaxLevel &&
               result.RequiredExperience > 0 &&
               result.CurrentExperience >= result.RequiredExperience)
        {
            result.CurrentExperience -= result.RequiredExperience;
            result.Level++;
            result.RequiredExperience = CalculateRequiredExperienceForNextLevel(result.Level, config);
        }

        if (result.Level >= ExperienceConstants.MaxLevel)
        {
            result.Level = ExperienceConstants.MaxLevel;
            result.CurrentExperience = 0;
            result.RequiredExperience = 0;
        }

        return result;
    }

    /// <summary>
    /// Monster の経験値報酬を非負値へ正規化する。
    /// </summary>
    public static int NormalizeReward(int reward)
    {
        return math.max(0, reward);
    }

    /// <summary>
    /// Player level に合わせて最大 HP を再計算し、増えた最大 HP 分だけ現在 HP も増やす。
    /// </summary>
    public static HealthComponent ApplyPlayerLevelToHealth(
        HealthComponent health,
        PlayerLevelStats stats,
        int level)
    {
        var nextMaxHp = CalculateMaxHpForLevel(stats, level);
        var currentMaxHp = math.max(1, health.MaxHp);
        var currentHp = math.clamp(health.CurrentHp, 0, currentMaxHp);
        var maxHpDelta = nextMaxHp - currentMaxHp;

        return HealthMath.CreateHealth(
            currentHp + math.max(0, maxHpDelta),
            nextMaxHp);
    }

    /// <summary>
    /// Player level から最大 HP を計算する。
    /// </summary>
    public static int CalculateMaxHpForLevel(PlayerLevelStats stats, int level)
    {
        var safeLevel = math.clamp(level, 1, ExperienceConstants.MaxLevel);

        return math.max(
            1,
            math.max(1, stats.BaseMaxHp) +
            math.max(0, stats.MaxHpPerLevel) * (safeLevel - 1));
    }

    /// <summary>
    /// Player level からスキルダメージ倍率を計算する。
    /// </summary>
    public static float CalculatePlayerSkillDamageRate(PlayerLevelStats stats, int level)
    {
        var safeLevel = math.clamp(level, 1, ExperienceConstants.MaxLevel);
        var ratePerLevel = math.max(0f, stats.SkillDamageRatePerLevel);

        return 1f + (safeLevel - 1) * ratePerLevel;
    }

    private static ExperienceComponent NormalizeExperience(ExperienceComponent experience)
    {
        return NormalizeExperience(experience, CreateDefaultLevelConfig());
    }

    private static ExperienceComponent NormalizeExperience(
        ExperienceComponent experience,
        ExperienceLevelConfig config)
    {
        var safeLevel = math.clamp(experience.Level, 1, ExperienceConstants.MaxLevel);

        if (safeLevel >= ExperienceConstants.MaxLevel)
        {
            return new ExperienceComponent
            {
                CurrentExperience = 0,
                RequiredExperience = 0,
                Level = ExperienceConstants.MaxLevel
            };
        }

        return new ExperienceComponent
        {
            CurrentExperience = math.max(0, experience.CurrentExperience),
            RequiredExperience = CalculateRequiredExperienceForNextLevel(safeLevel, config),
            Level = safeLevel
        };
    }
}

/// <summary>
/// 加算済み経験値から、必要量を超えた分だけ level up する。
///
/// MonsterDestroySystem は経験値加算だけを行い、この System が経験値消費と level up に伴う status 更新を担当する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(MonsterDestroySystem))]
public partial struct LevelUpSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ExperienceComponent>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var healthLookup = SystemAPI.GetComponentLookup<HealthComponent>(false);
        var levelConfigLookup = SystemAPI.GetComponentLookup<ExperienceLevelConfig>(true);
        var levelStatsLookup = SystemAPI.GetComponentLookup<PlayerLevelStats>(true);

        foreach (var (experience, entity) in
                 SystemAPI.Query<RefRW<ExperienceComponent>>()
                     .WithEntityAccess())
        {
            var oldLevel = experience.ValueRO.Level;
            var levelConfig = levelConfigLookup.HasComponent(entity)
                ? levelConfigLookup[entity]
                : ExperienceMath.CreateDefaultLevelConfig();
            var nextExperience = ExperienceMath.ProcessLevelUps(
                experience.ValueRO,
                levelConfig);

            experience.ValueRW.CurrentExperience = nextExperience.CurrentExperience;
            experience.ValueRW.RequiredExperience = nextExperience.RequiredExperience;
            experience.ValueRW.Level = nextExperience.Level;

            if (nextExperience.Level != oldLevel)
            {
                ApplyPlayerLevelStats(
                    entity,
                    nextExperience.Level,
                    healthLookup,
                    levelStatsLookup);
            }
        }
    }

    private static void ApplyPlayerLevelStats(
        Entity entity,
        int level,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<PlayerLevelStats> levelStatsLookup)
    {
        if (!levelStatsLookup.HasComponent(entity))
        {
            return;
        }

        var stats = levelStatsLookup[entity];

        if (healthLookup.HasComponent(entity))
        {
            healthLookup[entity] = ExperienceMath.ApplyPlayerLevelToHealth(
                healthLookup[entity],
                stats,
                level);
        }
    }
}
