using Unity.Mathematics;

/// <summary>
/// Skill system から独立して検査できる攻撃計算。
/// EntityQuery や World 状態を読まず、値だけを入力にする。
/// </summary>
public static class SkillMath
{
    /// <summary>
    /// Buff が何もない状態の倍率 accumulator を作る。
    /// </summary>
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

    /// <summary>
    /// 1 つの buff skill を accumulator へ反映する。
    /// </summary>
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

    /// <summary>
    /// Skill level から skill 固有のダメージ倍率を計算する。
    /// </summary>
    public static float CalculateLevelRate(int level)
    {
        var safeLevel = math.clamp(
            level,
            PlayerCombatConstants.MinSkillLevel,
            PlayerCombatConstants.MaxSkillLevel);

        return 1f + (safeLevel - 1) * PlayerCombatConstants.DamageRatePerLevel;
    }

    /// <summary>
    /// Skill 定義、skill level、buff から最終ダメージを計算する。
    /// </summary>
    public static float CalculateEffectiveDamage(
        AttackSkillConfig config,
        AttackSkillState state,
        BuffAccumulator buffs)
    {
        return CalculateEffectiveDamage(config, state, buffs, 1f);
    }

    /// <summary>
    /// Player level 倍率も含めて最終ダメージを計算する。
    /// </summary>
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

    /// <summary>
    /// Buff 適用後の cooldown 秒を計算する。
    /// </summary>
    public static float CalculateEffectiveCooltime(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.Cooltime) * buffs.CooltimeMultiplier;
    }

    /// <summary>
    /// Buff 適用後の対象探索半径を計算する。
    /// </summary>
    public static float CalculateEffectiveTargetRange(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.BaseTargetRange) * buffs.TargetRangeMultiplier;
    }

    /// <summary>
    /// Buff 適用後の攻撃判定半径を計算する。
    /// </summary>
    public static float CalculateEffectiveAttackRange(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.BaseAttackRange) * buffs.AttackRangeMultiplier;
    }

    /// <summary>
    /// float ダメージを HP component 用の整数値へ変換する。
    /// </summary>
    public static int CalculateDamageToHp(float damage)
    {
        return math.max(0, (int)math.ceil(damage));
    }

    /// <summary>
    /// Skill level から 1 回の発動で作る攻撃円数を計算する。
    /// </summary>
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

    /// <summary>
    /// 発動経過時間が指定 delay に到達したか判定する。
    /// </summary>
    public static bool IsDelayReached(float elapsedTime, float delay)
    {
        return math.max(0f, elapsedTime) >= math.max(0f, delay);
    }

    /// <summary>
    /// Casting 中の skill state に共通経過時間を進める。
    /// </summary>
    /// <remarks>
    /// 発動開始フレームは進めない。Logic system が同フレームで target を確定し、
    /// 次フレームから damage / presentation delay を評価するため。
    /// </remarks>
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

    /// <summary>
    /// 現在の DamageApplyCount に対応する damage 判定予定時刻を返す。
    /// </summary>
    public static float CalculateDamageApplyDelay(
        AttackSkillTimingConfig timing,
        SkillCastTarget castTarget,
        AttackSkillState state)
    {
        return math.max(0f, timing.DamageDelay) +
               math.max(0, state.DamageApplyCount) * math.max(0f, castTarget.RepeatInterval);
    }

    /// <summary>
    /// 現在フレームで damage 判定を実行するべきか判定する。
    /// </summary>
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

    /// <summary>
    /// SFX / VFX の必要な presentation がすべて完了したか判定する。
    /// </summary>
    public static bool IsPresentationComplete(
        AttackSkillState state,
        AttackSkillTimingConfig timing)
    {
        var sfxComplete = timing.HasSfx == 0 || state.SfxPlayed != 0;
        var vfxComplete = timing.HasVfx == 0 || state.VfxSpawned != 0;

        return sfxComplete && vfxComplete;
    }

    /// <summary>
    /// VFX prefab の元半径と表示したい半径から localScale 倍率を計算する。
    /// </summary>
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

    /// <summary>
    /// XZ 方向ベクトルを方向 bucket index に変換する。
    /// </summary>
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

    /// <summary>
    /// 対象選択時の重みを計算する。近い対象ほど少し重くなる。
    /// </summary>
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
