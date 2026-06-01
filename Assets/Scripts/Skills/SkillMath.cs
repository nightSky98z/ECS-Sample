using Unity.Mathematics;

/// <summary>
/// Skill system から独立して検査できる攻撃計算。
/// </summary>
public static class SkillMath
{
    public static BuffAccumulator CreateBuffAccumulator()
    {
        return new BuffAccumulator
        {
            DamageMultiplier = 1f,
            DefenseMultiplier = 1f,
            CooltimeMultiplier = 1f,
            AttackRangeMultiplier = 1f,
            TargetRangeMultiplier = 1f
        };
    }

    public static BuffAccumulator ApplyBuff(BuffAccumulator accumulator, BuffSkillConfig buff)
    {
        var multiplier = math.max(0f, buff.Multiplier);

        switch (buff.Target)
        {
            case BuffTargetStatus.Damage:
                accumulator.DamageMultiplier *= multiplier;
                break;
            case BuffTargetStatus.Defense:
                accumulator.DefenseMultiplier *= multiplier;
                break;
            case BuffTargetStatus.Cooltime:
                accumulator.CooltimeMultiplier *= multiplier;
                break;
            case BuffTargetStatus.AttackRange:
                accumulator.AttackRangeMultiplier *= multiplier;
                break;
            case BuffTargetStatus.TargetRange:
                accumulator.TargetRangeMultiplier *= multiplier;
                break;
        }

        return accumulator;
    }

    public static float CalculateLevelRate(int level)
    {
        var safeLevel = math.clamp(
            level,
            PlayerCombatConstants.MinSkillLevel,
            PlayerCombatConstants.MaxSkillLevel);

        return 1f + (safeLevel - 1) * PlayerCombatConstants.DamageRatePerLevel;
    }

    public static float CalculateEffectiveDamage(
        AttackSkillConfig config,
        AttackSkillState state,
        BuffAccumulator buffs)
    {
        return CalculateEffectiveDamage(config, state, buffs, 1f);
    }

    public static float CalculateEffectiveDamage(
        AttackSkillConfig config,
        AttackSkillState state,
        BuffAccumulator buffs,
        float playerLevelDamageRate)
    {
        return math.max(0f, config.BaseDamage) *
               CalculateLevelRate(state.Level) *
               math.max(0f, playerLevelDamageRate) *
               buffs.DamageMultiplier;
    }

    public static float CalculateEffectiveCooltime(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.Cooltime) * buffs.CooltimeMultiplier;
    }

    public static float CalculateEffectiveTargetRange(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.BaseTargetRange) * buffs.TargetRangeMultiplier;
    }

    public static float CalculateEffectiveAttackRange(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.BaseAttackRange) * buffs.AttackRangeMultiplier;
    }

    public static int CalculateDamageToHp(float damage)
    {
        return math.max(0, (int)math.ceil(damage));
    }

    public static int CalculateAttackCircleCount(
        int level,
        int baseTargetCount,
        float targetCountLevelWeight,
        int maxTargetCount,
        SkillCountRoundMode roundMode)
    {
        var safeLevel = math.clamp(
            level,
            PlayerCombatConstants.MinSkillLevel,
            PlayerCombatConstants.MaxSkillLevel);
        var safeBaseCount = math.max(1, baseTargetCount);
        var safeWeight = math.max(0f, targetCountLevelWeight);
        var safeMaxCount = math.clamp(
            math.max(safeBaseCount, maxTargetCount),
            1,
            PlayerCombatConstants.MaxAttackCircleCount);
        var rawAdditionalCount = (safeLevel - 1) * safeWeight;
        var additionalCount = roundMode switch
        {
            SkillCountRoundMode.Round => (int)math.floor(rawAdditionalCount + 0.5f),
            SkillCountRoundMode.Ceil => (int)math.ceil(rawAdditionalCount),
            _ => (int)math.floor(rawAdditionalCount)
        };

        return math.clamp(safeBaseCount + additionalCount, 1, safeMaxCount);
    }

    public static bool IsDelayReached(float elapsedTime, float delay)
    {
        return math.max(0f, elapsedTime) >= math.max(0f, delay);
    }

    public static AttackSkillState AdvanceCastElapsedTime(
        AttackSkillState state,
        float deltaTime)
    {
        if (state.IsCasting == 0 ||
            state.StartedThisFrame != 0)
        {
            return state;
        }

        state.CastElapsedTime += math.max(0f, deltaTime);
        return state;
    }

    public static float CalculateDamageApplyDelay(
        AttackSkillTimingConfig timing,
        SkillCastTarget castTarget,
        AttackSkillState state)
    {
        return math.max(0f, timing.DamageDelay) +
               math.max(0, state.DamageApplyCount) * math.max(0f, castTarget.RepeatInterval);
    }

    public static bool ShouldApplySkillDamage(
        AttackSkillState state,
        AttackSkillTimingConfig timing,
        SkillCastTarget castTarget)
    {
        if (state.IsCasting == 0)
        {
            return false;
        }

        var damageApplyTotalCount = math.max(1, castTarget.RepeatCount);

        return state.DamageApplyCount < damageApplyTotalCount &&
               IsDelayReached(
                   state.CastElapsedTime,
                   CalculateDamageApplyDelay(timing, castTarget, state));
    }

    public static bool IsPresentationComplete(
        AttackSkillState state,
        AttackSkillTimingConfig timing)
    {
        var sfxComplete = timing.HasSfx == 0 || state.SfxPlayed != 0;
        var vfxComplete = timing.HasVfx == 0 || state.VfxSpawned != 0;

        return sfxComplete && vfxComplete;
    }

    public static float CalculateVfxScale(
        float attackRange,
        AttackSkillTimingConfig timing)
    {
        var safePrefabRadius = math.max(0.0001f, timing.VfxPrefabRadius);
        var targetRadius = timing.VfxDisplayRadius > 0f
            ? timing.VfxDisplayRadius
            : math.max(0f, attackRange);

        return targetRadius / safePrefabRadius;
    }

    public static int CalculateDirectionBucketIndex(float2 direction, int bucketCount)
    {
        var safeBucketCount = math.max(1, bucketCount);

        if (math.lengthsq(direction) <= 0f)
        {
            return 0;
        }

        var angle = math.atan2(direction.y, direction.x);

        if (angle < 0f)
        {
            angle += math.PI * 2f;
        }

        var bucketIndex = (int)math.floor(angle / (math.PI * 2f) * safeBucketCount);

        return math.clamp(bucketIndex, 0, safeBucketCount - 1);
    }

    public static float CalculateTargetSelectionWeight(
        float distanceSq,
        float targetRangeSq,
        float distanceWeight)
    {
        var safeDistanceWeight = math.max(0f, distanceWeight);

        if (targetRangeSq <= 0f)
        {
            return 1f;
        }

        var distanceRate = math.saturate(math.sqrt(math.max(0f, distanceSq)) / math.sqrt(targetRangeSq));

        return 1f + safeDistanceWeight * (1f - distanceRate);
    }
}
