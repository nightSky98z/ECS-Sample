using Unity.Mathematics;

/// <summary>
/// スキルの計算処理（ダメージ・クールタイム・範囲・タイミングなど）。
/// EntityQuery や World の状態を読まず、引数の値だけから結果を計算するため、単体テストしやすい。
/// </summary>
public static class SkillMath
{
    /// <summary>
    /// バフがない状態（すべての倍率が 1）の合算値を作る。
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
    /// バフスキル 1 つ分の倍率を合算値に掛け合わせる。
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
    /// スキルレベルからダメージ倍率を計算する（Lv1 で 1 倍、1 上がるごとに DamageRatePerLevel だけ増える）。
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
    /// スキルの基礎ダメージ・スキルレベル・バフから、最終的なダメージを計算する。
    /// </summary>
    public static float CalculateEffectiveDamage(
        AttackSkillConfig config,
        AttackSkillState state,
        BuffAccumulator buffs)
    {
        return CalculateEffectiveDamage(config, state, buffs, 1f);
    }

    /// <summary>
    /// プレイヤーのレベルによる倍率も含めて、最終的なダメージを計算する。
    /// 最終ダメージ = 基礎ダメージ × スキルレベル倍率 × プレイヤーレベル倍率 × バフ倍率
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
    /// バフを反映したクールタイム（秒）を計算する。
    /// </summary>
    public static float CalculateEffectiveCooltime(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.Cooltime) * buffs.CooltimeMultiplier;
    }

    /// <summary>
    /// バフを反映した、ターゲットを探す範囲の半径を計算する。
    /// </summary>
    public static float CalculateEffectiveTargetRange(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.BaseTargetRange) * buffs.TargetRangeMultiplier;
    }

    /// <summary>
    /// バフを反映した、攻撃範囲の半径を計算する。
    /// </summary>
    public static float CalculateEffectiveAttackRange(
        AttackSkillConfig config,
        BuffAccumulator buffs)
    {
        return math.max(0f, config.BaseAttackRange) * buffs.AttackRangeMultiplier;
    }

    /// <summary>
    /// 小数のダメージを、HP に適用する整数に変換する（切り上げ。小さなダメージが 0 にならないようにする）。
    /// </summary>
    public static int CalculateDamageToHp(float damage)
    {
        return math.max(0, (int)math.ceil(damage));
    }

    /// <summary>
    /// スキルレベルから、1 回の発動で置く攻撃範囲（円）の数を計算する。
    /// 数 = 基本数 + (レベル - 1) × 係数（小数は roundMode で丸める）。上限を超えないようにする。
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
    /// 発動からの経過時間が、指定した待ち時間に達したかを判定する。
    /// </summary>
    public static bool IsDelayReached(float elapsedTime, float delay)
    {
        return math.max(0f, elapsedTime) >= math.max(0f, delay);
    }

    /// <summary>
    /// 発動中のスキルの経過時間を進める。
    /// </summary>
    /// <remarks>
    /// 発動を開始したフレームでは進めない。
    /// 開始したフレームでターゲットを確定し、次のフレームからダメージや演出のタイミングを判定するため。
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
    /// 次のダメージを与える予定の時刻（発動からの経過時間）を返す。
    /// 連続攻撃の場合、2 回目以降は RepeatInterval ずつ後ろにずれる。
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
    /// このフレームでダメージを与えるべきかを判定する。
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
    /// 必要な演出（SFX・VFX）がすべて終わったかを判定する。
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
    /// VFX の大きさの倍率を計算する（Prefab の元の大きさに対して、表示したい大きさが何倍か）。
    /// これにより、攻撃範囲が変わっても VFX の見た目が範囲と一致する。
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
    /// 方向ベクトル（XZ）が、周囲 360° を bucketCount 個に分けたうちの何番目のグループに入るかを返す。
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
    /// ターゲットを選ぶときの重みを計算する。近い敵ほど重みが大きく、選ばれやすくなる。
    /// 重み = 1 + distanceWeight × (1 - 距離 / 範囲)
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
