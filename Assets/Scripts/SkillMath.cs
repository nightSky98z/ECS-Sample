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

    public static BuffAccumulator ApplyBuff(BuffAccumulator accumulator, BuffSkillComponent buff)
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
        var safeLevel = math.max(1, level);

        return 1f + (safeLevel - 1) * PlayerCombatConstants.DamageRatePerLevel;
    }

    public static float CalculateEffectiveDamage(
        AttackSkillComponent skill,
        BuffAccumulator buffs)
    {
        return math.max(0f, skill.BaseDamage) *
               CalculateLevelRate(skill.Level) *
               buffs.DamageMultiplier;
    }

    public static float CalculateEffectiveCooltime(
        AttackSkillComponent skill,
        BuffAccumulator buffs)
    {
        return math.max(0f, skill.Cooltime) * buffs.CooltimeMultiplier;
    }

    public static float CalculateEffectiveTargetRange(
        AttackSkillComponent skill,
        BuffAccumulator buffs)
    {
        return math.max(0f, skill.BaseTargetRange) * buffs.TargetRangeMultiplier;
    }

    public static float CalculateEffectiveAttackRange(
        AttackSkillComponent skill,
        BuffAccumulator buffs)
    {
        return math.max(0f, skill.BaseAttackRange) * buffs.AttackRangeMultiplier;
    }

    public static int CalculateDamageToHp(float damage)
    {
        return math.max(0, (int)math.ceil(damage));
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
