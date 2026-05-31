using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// 経験値バランスの固定値。1 回のゲームは 7 stage、6 stage clear で Lv90 付近を調整目標にする。
/// </summary>
public static class ExperienceConstants
{
    public const int MaxLevel = 99;
    public const int ExpectedStageCount = 7;
    public const int TargetLevelAfterStageSix = 90;
    public const int BaseRequiredExperience = 50;
    public const float DefaultRequiredExperienceMultiplierPerLevel = 2f;
    public const float DefaultSkillDamageRatePerLevel = 0.02f;
}

/// <summary>
/// 経験値と level up の純粋計算。
/// </summary>
public static class ExperienceMath
{
    public static ExperienceComponent CreateInitialExperience()
    {
        return CreateInitialExperience(CreateDefaultLevelConfig());
    }

    public static ExperienceComponent CreateInitialExperience(ExperienceLevelConfig config)
    {
        return new ExperienceComponent
        {
            CurrentExperience = 0,
            RequiredExperience = CalculateRequiredExperienceForNextLevel(1, config),
            Level = 1
        };
    }

    public static ExperienceLevelConfig CreateDefaultLevelConfig()
    {
        return new ExperienceLevelConfig
        {
            BaseRequiredExperience = ExperienceConstants.BaseRequiredExperience,
            RequiredExperienceMultiplierPerLevel = ExperienceConstants.DefaultRequiredExperienceMultiplierPerLevel
        };
    }

    public static int CalculateRequiredExperienceForNextLevel(int level)
    {
        return CalculateRequiredExperienceForNextLevel(level, CreateDefaultLevelConfig());
    }

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

    public static ExperienceComponent AddExperience(ExperienceComponent experience, int amount)
    {
        return AddExperience(experience, amount, CreateDefaultLevelConfig());
    }

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

    public static ExperienceComponent ProcessLevelUps(ExperienceComponent experience)
    {
        return ProcessLevelUps(experience, CreateDefaultLevelConfig());
    }

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

    public static int NormalizeReward(int reward)
    {
        return math.max(0, reward);
    }

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

    public static int CalculateMaxHpForLevel(PlayerLevelStats stats, int level)
    {
        var safeLevel = math.clamp(level, 1, ExperienceConstants.MaxLevel);

        return math.max(
            1,
            math.max(1, stats.BaseMaxHp) +
            math.max(0, stats.MaxHpPerLevel) * (safeLevel - 1));
    }

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
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(MonsterDestroySystem))]
public partial struct LevelUpSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<ExperienceComponent>();
    }

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
