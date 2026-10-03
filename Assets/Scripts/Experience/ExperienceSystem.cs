using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// 経験値バランスの定数。
/// 1 回のゲームは 7 ステージ構成で、6 ステージをクリアした時点で Lv90 前後になるように調整している。
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
    /// レベルが 1 上がるごとに、必要経験値にかける倍率（既定値）。
    /// </summary>
    public const float DefaultRequiredExperienceMultiplierPerLevel = 2f;

    /// <summary>
    /// プレイヤーのレベルが 1 上がるごとに増えるスキルダメージ倍率（既定値）。
    /// </summary>
    public const float DefaultSkillDamageRatePerLevel = 0.02f;
}

/// <summary>
/// 経験値とレベルアップの計算。
/// 計算を System から切り離した static 関数にまとめ、System は結果を Component に書き戻すだけにしている。
/// そのため、経験値曲線の調整や境界値（最大レベル・オーバーフロー）のテストをこのクラスだけで行える。
/// </summary>
public static class ExperienceMath
{
    /// <summary>
    /// 既定の成長曲線で、Lv1・経験値 0 の状態を作る。
    /// </summary>
    public static ExperienceComponent CreateInitialExperience()
    {
        return CreateInitialExperience(CreateDefaultLevelConfig());
    }

    /// <summary>
    /// 指定した成長曲線で、Lv1・経験値 0 の状態を作る。
    /// </summary>
    /// <param name="config">成長曲線。</param>
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
    /// 既定の成長曲線を返す。
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
    /// 既定の成長曲線で、指定したレベルから次のレベルに上がるための必要経験値を返す。
    /// </summary>
    public static int CalculateRequiredExperienceForNextLevel(int level)
    {
        return CalculateRequiredExperienceForNextLevel(level, CreateDefaultLevelConfig());
    }

    /// <summary>
    /// 指定したレベルから次のレベルに上がるための必要経験値を返す。
    /// 必要経験値は「基本値 × 倍率^(レベル-1)」で増えていく。
    /// </summary>
    /// <param name="level">現在のレベル。範囲外の値は 1〜MaxLevel に収める。</param>
    /// <param name="config">成長曲線。</param>
    /// <returns>最大レベルなら 0、それ以外は 1 以上。</returns>
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
            // 指数的に増えるため、int の上限を超える前に打ち切る。
            if (requiredExperience > int.MaxValue / multiplier)
            {
                return int.MaxValue;
            }

            requiredExperience *= multiplier;
        }

        return math.max(1, (int)math.ceil(requiredExperience));
    }

    /// <summary>
    /// 既定の成長曲線で経験値を加算する（レベルアップ処理は行わない）。
    /// </summary>
    public static ExperienceComponent AddExperience(ExperienceComponent experience, int amount)
    {
        return AddExperience(experience, amount, CreateDefaultLevelConfig());
    }

    /// <summary>
    /// 経験値を加算する。レベルアップ処理は LevelUpSystem が別に行う。
    /// </summary>
    /// <param name="experience">加算前の状態。</param>
    /// <param name="amount">加算する量。0 以下は無視する。</param>
    /// <param name="config">成長曲線。</param>
    /// <returns>加算後の状態。</returns>
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

        // long で計算し、int の上限を超えないようにする。
        var nextExperience = (long)result.CurrentExperience + amount;

        result.CurrentExperience = nextExperience > int.MaxValue
            ? int.MaxValue
            : (int)nextExperience;

        return result;
    }

    /// <summary>
    /// 既定の成長曲線で、貯まっている経験値の分だけレベルアップさせる。
    /// </summary>
    public static ExperienceComponent ProcessLevelUps(ExperienceComponent experience)
    {
        return ProcessLevelUps(experience, CreateDefaultLevelConfig());
    }

    /// <summary>
    /// 貯まっている経験値の分だけレベルアップさせる。一度に大量の経験値を得た場合は、複数レベル上がる。
    /// </summary>
    /// <param name="experience">処理前の状態。</param>
    /// <param name="config">成長曲線。</param>
    /// <returns>レベルアップ後の状態。</returns>
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
    /// モンスターの経験値報酬を 0 以上に収める。
    /// </summary>
    public static int NormalizeReward(int reward)
    {
        return math.max(0, reward);
    }

    /// <summary>
    /// プレイヤーのレベルに合わせて最大 HP を再計算し、最大 HP が増えた分だけ現在の HP も回復させる。
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
    /// プレイヤーのレベルから最大 HP を計算する。
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
    /// プレイヤーのレベルからスキルダメージ倍率を計算する（Lv1 で 1 倍）。
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

    /// <summary>
    /// レベルを 1〜MaxLevel に収め、必要経験値を成長曲線から計算し直す。
    /// </summary>
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
/// 貯まった経験値が必要量を超えていれば、レベルアップさせる。
/// 役割分担：MonsterDestroySystem は経験値を加算するだけで、
/// レベルアップの判定と、それに伴うステータス（最大 HP など）の更新はこの System が担当する。
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

    /// <summary>
    /// 新しいレベルに合わせて、プレイヤーの最大 HP を更新する。
    /// </summary>
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
