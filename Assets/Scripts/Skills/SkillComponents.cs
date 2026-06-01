using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// プレイヤーの戦闘関連で共有する固定容量と固定係数。
/// 配列長や FixedList 容量と一致させる値なので、実行中に変更しない。
/// </summary>
public static class PlayerCombatConstants
{
    /// <summary>
    /// プレイヤーが同時に装備できる攻撃 skill slot 数。
    /// </summary>
    public const int MaxAttackSkillCount = 5;

    /// <summary>
    /// プレイヤーが同時に装備できる buff skill slot 数。
    /// </summary>
    public const int MaxBuffSkillCount = 5;

    /// <summary>
    /// 攻撃 slot と buff slot を合わせた総 slot 数。
    /// </summary>
    public const int MaxSkillCount = MaxAttackSkillCount + MaxBuffSkillCount;

    /// <summary>
    /// Skill level の下限。Inspector / debug 操作 / runtime 計算で共通に使う。
    /// </summary>
    public const int MinSkillLevel = 1;

    /// <summary>
    /// Skill level の上限。現在のゲーム仕様上の最大値。
    /// </summary>
    public const int MaxSkillLevel = 6;

    /// <summary>
    /// Skill level 1 上昇ごとの skill 固有ダメージ倍率加算値。
    /// </summary>
    public const float DamageRatePerLevel = 0.1f;

    /// <summary>
    /// 攻撃対象を方向密度で選ぶときの方位分割数。
    /// </summary>
    public const int TargetSelectionDirectionBucketCount = 16;

    /// <summary>
    /// 対象選択で近い敵を少し優先する重み。
    /// </summary>
    public const float TargetSelectionDistanceWeight = 0.6f;

    /// <summary>
    /// 1 回の skill 発動で保持できる攻撃円数の上限。
    /// </summary>
    public const int MaxAttackCircleCount = 32;
}

/// <summary>
/// buff skill が倍率をかける攻撃値。
/// </summary>
public enum BuffTargetStatus
{
    Damage,
    Defense,
    Cooltime,
    AttackRange,
    TargetRange
}

/// <summary>
/// レベルから攻撃円数を計算するときの丸め方。
/// </summary>
public enum SkillCountRoundMode
{
    Floor,
    Round,
    Ceil
}

/// <summary>
/// Inspector で選ぶ attack skill の攻撃形状。
/// </summary>
public enum AttackSkillLogicKind
{
    [InspectorName("0: 対象中心の円形範囲攻撃")]
    TargetCenteredCircle = 0,

    [InspectorName("1: 前方扇形攻撃")]
    ForwardSector = 1,

    [InspectorName("2: 直線貫通攻撃")]
    PiercingLine = 2,

    [InspectorName("3: 自分中心 AoE")]
    SelfCenteredArea = 3,

    [InspectorName("4: 対象周囲に拡散爆発")]
    SpreadAroundTarget = 4,

    [InspectorName("5: 雷のように周囲へ連鎖")]
    ChainToNearbyTargets = 5,

    [InspectorName("6: 自分中心にランダム落下")]
    RandomGroundAroundOwner = 6,

    [InspectorName("7: 同じ場所で繰り返し攻撃")]
    RepeatingTargetCircle = 7
}

/// <summary>
/// 発動時に確定した damage 判定形状。
/// </summary>
public enum AttackSkillCastShape
{
    CircleList,
    ForwardSector,
    PiercingLine
}

/// <summary>
/// Debuff system が処理する効果カテゴリ。
/// </summary>
public enum DebuffKind
{
    MoveSpeedDown,
    DamageOverTime,
    AttackDown,
    DefenseDown,
    EvasionDown,
    Freeze,
    Paralyze
}

/// <summary>
/// 同じ DebuffId が既にある場合の更新規則。
/// </summary>
public enum DebuffStackPolicy
{
    RefreshDuration,
    ReplaceIfStronger,
    StackIndependent
}

/// <summary>
/// SkillEntity が持つ命中時 debuff 定義。
/// 発生率や継続時間は authoring / bake 時に正規化され、命中時に DebuffRuntimeState へコピーされる。
/// </summary>
public struct SkillDebuffSpec : IBufferElementData
{
    /// <summary>
    /// 同一 debuff を識別する ID。StackPolicy の比較単位。
    /// </summary>
    public int DebuffId;

    /// <summary>
    /// Runtime が実行する debuff 処理カテゴリ。
    /// </summary>
    public DebuffKind Kind;

    /// <summary>
    /// 同じ DebuffId が既に対象にある場合の更新規則。
    /// </summary>
    public DebuffStackPolicy StackPolicy;

    /// <summary>
    /// 命中時に適用される確率。0 は発生なし、1 は確定。
    /// </summary>
    public float Chance;

    /// <summary>
    /// 適用後の継続時間秒。0 以下は無効扱い。
    /// </summary>
    public float Duration;

    /// <summary>
    /// 主効果値。
    /// 倍率系は 1 を基準、DoT は tick ダメージ、Paralyze は速度 0 を維持する時間割合。
    /// </summary>
    public float Value0;

    /// <summary>
    /// 追加効果値。
    /// Paralyze では回復カーブ指数。1 は普通、2 以上は遅く始まり後半で戻る。
    /// </summary>
    public float Value1;

    /// <summary>
    /// DoT の tick 間隔秒。DoT 以外では現在未使用。
    /// </summary>
    public float TickInterval;
}

/// <summary>
/// Inspector で編集する命中時 debuff 定義。
/// </summary>
[Serializable]
public struct SkillDebuffAuthoring
{
    [Tooltip("デバフ ID。VFX/SFX/UI/個別ルールの識別に使う。")]
    public int DebuffId;

    [Tooltip("デバフの処理カテゴリ。")]
    public DebuffKind Kind;

    [Tooltip("同じ DebuffId が既にある場合の更新規則。")]
    public DebuffStackPolicy StackPolicy;

    [Tooltip("命中時にこのデバフが発生する確率。0 は発生なし、1 は必ず発生。")]
    public float Chance;

    [Tooltip("デバフの継続時間秒。0 以下なら適用しない。")]
    public float Duration;

    [Tooltip("効果値 0。速度低下なら移動速度倍率、DoT なら tick ダメージ、麻痺なら速度 0 を維持する時間割合。")]
    public float Value0;

    [Tooltip("効果値 1。麻痺なら回復カーブ指数。1 は普通、2 以上は遅く始まり後半で戻る。")]
    public float Value1;

    [Tooltip("DoT の tick 間隔秒。0 以下なら 1 秒として扱う。")]
    public float TickInterval;
}

/// <summary>
/// Monster が保持する実行中 debuff。
/// FixedList 内に値として保存されるため、Entity 参照や managed 参照を持たない。
/// </summary>
public struct ActiveDebuff
{
    /// <summary>
    /// 同一 debuff の置換判定に使う ID。
    /// </summary>
    public int DebuffId;

    /// <summary>
    /// この debuff の処理カテゴリ。
    /// </summary>
    public DebuffKind Kind;

    /// <summary>
    /// 再適用時の更新規則。
    /// </summary>
    public DebuffStackPolicy StackPolicy;

    /// <summary>
    /// 残り継続時間秒。0 以下になった tick で削除される。
    /// </summary>
    public float RemainingTime;

    /// <summary>
    /// 適用開始時の継続時間秒。Paralyze など、経過率で効果量を計算する debuff が使う。
    /// </summary>
    public float Duration;

    /// <summary>
    /// 主効果値。Kind により意味が変わる。
    /// </summary>
    public float Value0;

    /// <summary>
    /// 追加効果値。将来の複合効果用。
    /// </summary>
    public float Value1;

    /// <summary>
    /// DoT tick 間隔秒。
    /// </summary>
    public float TickInterval;

    /// <summary>
    /// 次の DoT tick までに蓄積された経過時間。
    /// </summary>
    public float TickTimer;
}

/// <summary>
/// Monster に固定で持たせる debuff runtime 状態。
/// Debuff の付与/消滅で component を追加削除せず、固定容量リストだけを書き換える。
/// </summary>
public struct DebuffRuntimeState : IComponentData
{
    public FixedList512Bytes<ActiveDebuff> ActiveDebuffs;
}

/// <summary>
/// 各 system が読む集約済み debuff 値。
/// Movement / AI などは ActiveDebuff の配列を直接見ず、この集約値だけを見る。
/// </summary>
public struct DebuffAggregate : IComponentData
{
    /// <summary>
    /// 移動速度倍率。1 が通常、0 は移動停止。
    /// </summary>
    public float MoveSpeedMultiplier;

    /// <summary>
    /// 攻撃力倍率。1 が通常。
    /// </summary>
    public float AttackMultiplier;

    /// <summary>
    /// 防御倍率。1 が通常。
    /// </summary>
    public float DefenseMultiplier;

    /// <summary>
    /// 回避率倍率。1 が通常。
    /// </summary>
    public float EvasionMultiplier;
}

/// <summary>
/// 氷結中の Entity に付く分類タグ。
/// Velocity を外して Movement / AI / PathFollow の query から外すため、移動停止を倍率では表現しない。
/// </summary>
public struct FreezeTag : IComponentData
{

}

/// <summary>
/// フリーズ状態で処理すべき runtime データ。
/// VFX / SFX の再生済み状態も、将来ここへ追加する。
/// </summary>
public struct FreezeComponent : IComponentData
{
    /// <summary>
    /// 氷結を維持する秒数。
    /// </summary>
    public float ShouldFreezeTime;

    /// <summary>
    /// 氷結開始からの経過秒数。
    /// </summary>
    public float Timer;

    /// <summary>
    /// 氷結解除時に戻す Velocity。解除直後の AI System が次 frame の水平速度を上書きする。
    /// </summary>
    public Velocity RestoreVelocity;
}

/// <summary>
/// Debuff 処理で共有する固定値。
/// </summary>
public static class DebuffConstants
{
    public const int MaxActiveDebuffCount = 12;
    public const float DefaultDotTickInterval = 1f;
    public const float DefaultParalyzeRecoveryPower = 2f;
}

/// <summary>
/// Debuff system から独立して検査できる計算。
/// </summary>
public static class DebuffMath
{
    /// <summary>
    /// Authoring / buffer 由来の debuff 定義を runtime が扱える範囲に丸める。
    /// </summary>
    public static SkillDebuffSpec NormalizeSpec(SkillDebuffSpec spec)
    {
        spec.Chance = math.saturate(spec.Chance);
        spec.Duration = math.max(0f, spec.Duration);
        spec.TickInterval = spec.TickInterval > 0f
            ? spec.TickInterval
            : DebuffConstants.DefaultDotTickInterval;

        if (spec.Kind == DebuffKind.Paralyze)
        {
            spec.Value0 = math.saturate(spec.Value0);
            spec.Value1 = spec.Value1 > 0f
                ? spec.Value1
                : DebuffConstants.DefaultParalyzeRecoveryPower;
        }

        return spec;
    }

    /// <summary>
    /// SkillDebuffSpec から対象に保存する ActiveDebuff を作る。
    /// </summary>
    public static ActiveDebuff CreateActiveDebuff(SkillDebuffSpec spec)
    {
        var normalizedSpec = NormalizeSpec(spec);

        return new ActiveDebuff
        {
            DebuffId = normalizedSpec.DebuffId,
            Kind = normalizedSpec.Kind,
            StackPolicy = normalizedSpec.StackPolicy,
            RemainingTime = normalizedSpec.Duration,
            Duration = normalizedSpec.Duration,
            Value0 = normalizedSpec.Value0,
            Value1 = normalizedSpec.Value1,
            TickInterval = normalizedSpec.TickInterval,
            TickTimer = 0f
        };
    }

    /// <summary>
    /// Debuff がない状態の集約値を作る。
    /// </summary>
    public static DebuffAggregate CreateNeutralAggregate()
    {
        return new DebuffAggregate
        {
            MoveSpeedMultiplier = 1f,
            AttackMultiplier = 1f,
            DefenseMultiplier = 1f,
            EvasionMultiplier = 1f
        };
    }

    /// <summary>
    /// 1 つの active debuff を集約値へ反映する。
    /// </summary>
    public static DebuffAggregate ApplyToAggregate(DebuffAggregate aggregate, ActiveDebuff debuff)
    {
        switch (debuff.Kind)
        {
            case DebuffKind.MoveSpeedDown:
                aggregate.MoveSpeedMultiplier *= math.max(0f, debuff.Value0);
                break;
            case DebuffKind.AttackDown:
                aggregate.AttackMultiplier *= math.max(0f, debuff.Value0);
                break;
            case DebuffKind.DefenseDown:
                aggregate.DefenseMultiplier *= math.max(0f, debuff.Value0);
                break;
            case DebuffKind.EvasionDown:
                aggregate.EvasionMultiplier *= math.max(0f, debuff.Value0);
                break;
        }

        aggregate.MoveSpeedMultiplier = math.max(0f, aggregate.MoveSpeedMultiplier);
        aggregate.AttackMultiplier = math.max(0f, aggregate.AttackMultiplier);
        aggregate.DefenseMultiplier = math.max(0f, aggregate.DefenseMultiplier);
        aggregate.EvasionMultiplier = math.max(0f, aggregate.EvasionMultiplier);
        return aggregate;
    }

    /// <summary>
    /// DoT の経過時間を進め、今回発生する HP ダメージ量を返す。
    /// </summary>
    /// <param name="debuff">DoT として評価する debuff。</param>
    /// <param name="deltaTime">今回進める秒数。</param>
    /// <param name="nextTickTimer">次フレームへ持ち越す tick 蓄積時間。</param>
    /// <returns>今回発生した HP ダメージ量。</returns>
    public static int CalculateDotDamage(ActiveDebuff debuff, float deltaTime, out float nextTickTimer)
    {
        var tickInterval = debuff.TickInterval > 0f
            ? debuff.TickInterval
            : DebuffConstants.DefaultDotTickInterval;
        var tickTimer = debuff.TickTimer + math.max(0f, deltaTime);
        var tickCount = (int)math.floor(tickTimer / tickInterval);

        nextTickTimer = tickTimer - tickCount * tickInterval;

        if (debuff.Kind != DebuffKind.DamageOverTime || tickCount <= 0)
        {
            return 0;
        }

        return tickCount * math.max(0, (int)math.ceil(debuff.Value0));
    }

    /// <summary>
    /// Runtime 全体から Paralyze による移動速度倍率を計算する。
    /// </summary>
    public static float CalculateParalyzeMoveSpeedMultiplier(DebuffRuntimeState runtime)
    {
        var multiplier = 1f;

        for (var debuffIndex = 0; debuffIndex < runtime.ActiveDebuffs.Length; debuffIndex++)
        {
            var debuff = runtime.ActiveDebuffs[debuffIndex];

            if (debuff.Kind != DebuffKind.Paralyze)
            {
                continue;
            }

            multiplier = math.min(multiplier, CalculateParalyzeMoveSpeedMultiplier(debuff));
        }

        return multiplier;
    }

    /// <summary>
    /// Paralyze の経過率から移動速度倍率を計算する。
    /// </summary>
    /// <remarks>
    /// Value0 は速度 0 の維持割合、Value1 は回復カーブ指数。
    /// 速度は 0 から始まり、hold 後に smoothstep^power で 1 へ戻る。
    /// </remarks>
    public static float CalculateParalyzeMoveSpeedMultiplier(ActiveDebuff debuff)
    {
        if (debuff.Kind != DebuffKind.Paralyze)
        {
            return 1f;
        }

        var duration = math.max(0.0001f, debuff.Duration);
        var remainingTime = math.clamp(debuff.RemainingTime, 0f, duration);

        if (remainingTime <= 0f)
        {
            return 1f;
        }

        var elapsedRatio = 1f - remainingTime / duration;
        var holdRatio = math.saturate(debuff.Value0);

        if (elapsedRatio <= holdRatio)
        {
            return 0f;
        }

        var recoverySpan = math.max(0.0001f, 1f - holdRatio);
        var recoveryRatio = math.saturate((elapsedRatio - holdRatio) / recoverySpan);
        var smoothRatio = recoveryRatio * recoveryRatio * (3f - 2f * recoveryRatio);
        var recoveryPower = debuff.Value1 > 0f
            ? debuff.Value1
            : DebuffConstants.DefaultParalyzeRecoveryPower;

        return math.saturate(math.pow(smoothRatio, recoveryPower));
    }

    /// <summary>
    /// ReplaceIfStronger 用に、置換候補が現在の debuff より強いか判定する。
    /// </summary>
    /// <remarks>
    /// 倍率低下系は値が小さいほど強く、DoT は秒間ダメージが大きいほど強い。
    /// </remarks>
    public static bool IsReplacementStronger(ActiveDebuff currentDebuff, ActiveDebuff replacementDebuff)
    {
        if (currentDebuff.Kind != replacementDebuff.Kind)
        {
            return math.abs(replacementDebuff.Value0) > math.abs(currentDebuff.Value0);
        }

        switch (replacementDebuff.Kind)
        {
            case DebuffKind.MoveSpeedDown:
            case DebuffKind.AttackDown:
            case DebuffKind.DefenseDown:
            case DebuffKind.EvasionDown:
                return math.max(0f, replacementDebuff.Value0) <
                       math.max(0f, currentDebuff.Value0);
            case DebuffKind.DamageOverTime:
                return CalculateDotDamagePerSecond(replacementDebuff) >
                       CalculateDotDamagePerSecond(currentDebuff);
            case DebuffKind.Paralyze:
                return CalculateParalyzeStrengthScore(replacementDebuff) >
                       CalculateParalyzeStrengthScore(currentDebuff);
            case DebuffKind.Freeze:
                return replacementDebuff.RemainingTime > currentDebuff.RemainingTime;
            default:
                return math.abs(replacementDebuff.Value0) > math.abs(currentDebuff.Value0);
        }
    }

    private static float CalculateDotDamagePerSecond(ActiveDebuff debuff)
    {
        var tickInterval = debuff.TickInterval > 0f
            ? debuff.TickInterval
            : DebuffConstants.DefaultDotTickInterval;

        return math.max(0f, debuff.Value0) / tickInterval;
    }

    private static float CalculateParalyzeStrengthScore(ActiveDebuff debuff)
    {
        var durationScore = math.max(0f, debuff.RemainingTime);
        var holdScore = 1f + math.saturate(debuff.Value0);
        var curveScore = 1f + math.max(0f, debuff.Value1) * 0.1f;

        return durationScore * holdScore * curveScore;
    }

    /// <summary>
    /// 確率値を 0..1 に丸めた上で、今回 debuff を適用するか判定する。
    /// </summary>
    public static bool ShouldApply(float chance, ref Unity.Mathematics.Random random)
    {
        var normalizedChance = math.saturate(chance);

        if (normalizedChance <= 0f)
        {
            return false;
        }

        if (normalizedChance >= 1f)
        {
            return true;
        }

        return random.NextFloat() <= normalizedChance;
    }
}

/// <summary>
/// Attack skill の定義値。SkillEntity prefab と slot の両方で共有する固定寄りデータ。
/// </summary>
public struct AttackSkillConfig : IComponentData
{
    /// <summary>
    /// Skill 定義 ID。UI / debug / random seed の識別に使う。
    /// </summary>
    public int Id;

    /// <summary>
    /// Buff、skill level、player level を掛ける前の基礎ダメージ。
    /// </summary>
    public float BaseDamage;

    /// <summary>
    /// Buff 適用前の基礎 cooldown 秒。
    /// </summary>
    public float Cooltime;

    /// <summary>
    /// 対象候補を探す XZ 半径。
    /// </summary>
    public float BaseTargetRange;

    /// <summary>
    /// 1 つの攻撃円が命中判定に使う XZ 半径。
    /// </summary>
    public float BaseAttackRange;

    /// <summary>
    /// Skill level 1 の攻撃円数。
    /// </summary>
    public int BaseTargetCount;

    /// <summary>
    /// Level 上昇で攻撃円数を増やす係数。
    /// </summary>
    public float TargetCountLevelWeight;

    /// <summary>
    /// 1 回の発動で出せる攻撃円数の上限。
    /// </summary>
    public int MaxTargetCount;

    /// <summary>
    /// Level から増加分を計算するときの丸め方。
    /// </summary>
    public SkillCountRoundMode TargetCountRoundMode;

    /// <summary>
    /// SkillLogicSystem が選ぶ攻撃ロジック ID。
    /// </summary>
    public int LogicId;
}

/// <summary>
/// 2 段階攻撃、ランダム落下、繰り返し攻撃で使う追加定義値。
/// </summary>
public struct AttackSkillAdvancedConfig : IComponentData
{
    /// <summary>
    /// LogicId 1 の前方扇形角度。
    /// </summary>
    public float ForwardSectorAngleDegrees;

    /// <summary>
    /// LogicId 2 の直線貫通幅。0 の場合は BaseAttackRange を使う。
    /// </summary>
    public float LineWidth;

    /// <summary>
    /// LogicId 4/5 の 2 段階目基礎数。
    /// </summary>
    public int SecondaryBaseCount;

    /// <summary>
    /// LogicId 4/5 の 2 段階目数を level で増やす係数。
    /// </summary>
    public float SecondaryCountLevelWeight;

    /// <summary>
    /// LogicId 4/5 の 2 段階目数の上限。
    /// </summary>
    public int SecondaryMaxCount;

    /// <summary>
    /// LogicId 4/5 の 2 段階目探索半径。
    /// </summary>
    public float SecondaryTargetRange;

    /// <summary>
    /// LogicId 4/5 の 2 段階目攻撃半径倍率。
    /// </summary>
    public float SecondaryAttackRangeMultiplier;

    /// <summary>
    /// LogicId 6 のランダム落下座標を選ぶ半径。0 の場合は BaseTargetRange を使う。
    /// </summary>
    public float RandomGroundRadius;

    /// <summary>
    /// LogicId 7 の繰り返し基礎回数。
    /// </summary>
    public int RepeatBaseCount;

    /// <summary>
    /// LogicId 7 の繰り返し回数を level で増やす係数。
    /// </summary>
    public float RepeatCountLevelWeight;

    /// <summary>
    /// LogicId 7 の繰り返し回数上限。
    /// </summary>
    public int RepeatMaxCount;

    /// <summary>
    /// LogicId 7 の同じ場所へ再攻撃する間隔秒。
    /// </summary>
    public float RepeatInterval;
}

/// <summary>
/// Attack skill の全ロジックで共通利用する発動タイミングと演出用数値。
/// LogicId ごとの攻撃形状とは独立し、発動開始からの経過秒で判定する。
/// </summary>
public struct AttackSkillTimingConfig : IComponentData
{
    /// <summary>
    /// 発動開始から damage 判定を出すまでの秒数。
    /// </summary>
    public float DamageDelay;

    /// <summary>
    /// 発動開始から SFX を再生するまでの秒数。
    /// </summary>
    public float SfxDelay;

    /// <summary>
    /// SFX の再生音量。1 が基準。
    /// </summary>
    public float SfxVolume;

    /// <summary>
    /// 発動開始から VFX を生成するまでの秒数。
    /// </summary>
    public float VfxDelay;

    /// <summary>
    /// 生成した VFX GameObject を破棄するまでの秒数。
    /// </summary>
    public float VfxDuration;

    /// <summary>
    /// VFX prefab の元見た目半径。Prefab ごとのスケール差を吸収する。
    /// </summary>
    public float VfxPrefabRadius;

    /// <summary>
    /// VFX を表示したい半径。0 の場合は攻撃範囲へ合わせる。
    /// </summary>
    public float VfxDisplayRadius;

    /// <summary>
    /// 0 = SFX なし, 1 = SFX 再生完了を待ってから cooldown に入る。
    /// </summary>
    public byte HasSfx;

    /// <summary>
    /// 0 = VFX なし, 1 = VFX 生成完了を待ってから cooldown に入る。
    /// </summary>
    public byte HasVfx;
}

/// <summary>
/// Attack skill slot の実行状態。
/// Slot は stage 中に増減しないため、状態だけを上書きして archetype 変更を避ける。
/// </summary>
public struct AttackSkillState : IComponentData
{
    /// <summary>
    /// Cooldown 用 timer。IsCooltime が 1 のときだけ進む。
    /// </summary>
    public float Timer;

    /// <summary>
    /// Casting 中の発動経過秒。Damage / SFX / VFX delay はこの値を見る。
    /// </summary>
    public float CastElapsedTime;

    /// <summary>
    /// Skill level。Debug 操作やレベルアップで変化する。
    /// </summary>
    public int Level;

    /// <summary>
    /// 今回の casting 中に damage 判定を適用した回数。
    /// </summary>
    public int DamageApplyCount;

    /// <summary>
    /// 今回の casting で予定している damage 判定回数。
    /// </summary>
    public int DamageApplyTotalCount;

    /// <summary>
    /// 0 = ready, 1 = cooltime running。
    /// </summary>
    public byte IsCooltime;

    /// <summary>
    /// 0 = idle, 1 = SkillLogicSystem が今回処理する。
    /// </summary>
    public byte IsTriggered;

    /// <summary>
    /// 0 = idle, 1 = 発動中。ダメージ / SFX / VFX delay はこの状態で進む。
    /// </summary>
    public byte IsCasting;

    /// <summary>
    /// 0 = 未適用, 1 = 今回の発動で damage 判定済み。
    /// </summary>
    public byte DamageApplied;

    /// <summary>
    /// 0 = 未再生, 1 = 今回の発動で SFX 再生済み。
    /// </summary>
    public byte SfxPlayed;

    /// <summary>
    /// 0 = 未生成, 1 = 今回の発動で VFX 生成済み。
    /// </summary>
    public byte VfxSpawned;

    /// <summary>
    /// 0 = 通常, 1 = 発動開始フレーム。delay 0 の presentation を次フレームで拾うために使う。
    /// </summary>
    public byte StartedThisFrame;
}

/// <summary>
/// 発動開始時に確定した攻撃判定の入力。
/// Logic system が書き、damage / presentation / debug 表示が読む。
/// </summary>
public struct SkillCastTarget : IComponentData
{
    /// <summary>
    /// CircleList 表示/判定に使う中心座標配列。
    /// </summary>
    public FixedList512Bytes<float3> Positions;

    /// <summary>
    /// Positions と同じ index の攻撃半径。
    /// </summary>
    public FixedList512Bytes<float> AttackRanges;

    /// <summary>
    /// Positions と同じ index の HP ダメージ。
    /// </summary>
    public FixedList512Bytes<int> Damages;

    /// <summary>
    /// 今回の発動が使う判定形状。
    /// </summary>
    public AttackSkillCastShape Shape;

    /// <summary>
    /// 扇形/直線など、単一形状の起点。
    /// </summary>
    public float3 Origin;

    /// <summary>
    /// 扇形/直線の XZ 正規化方向。
    /// </summary>
    public float2 Direction;

    /// <summary>
    /// 扇形/直線の長さ。
    /// </summary>
    public float ShapeLength;

    /// <summary>
    /// 直線幅や形状固有の補助幅。
    /// </summary>
    public float ShapeWidth;

    /// <summary>
    /// 扇形判定で使う半角 cos 値。
    /// </summary>
    public float ShapeAngleCos;

    /// <summary>
    /// 旧経路や fallback で使う既定攻撃半径。
    /// </summary>
    public float AttackRange;

    /// <summary>
    /// 旧経路や fallback で使う既定 HP ダメージ。
    /// </summary>
    public int Damage;

    /// <summary>
    /// 同じ発動中に damage 判定を繰り返す回数。
    /// </summary>
    public int RepeatCount;

    /// <summary>
    /// RepeatCount が 2 以上のとき、2 回目以降の判定間隔秒。
    /// </summary>
    public float RepeatInterval;
}

/// <summary>
/// Attack skill の SFX / VFX prefab 参照。
/// Managed object を含むため、presentation 専用 system だけが読む。
/// </summary>
public struct AttackSkillPresentation : IComponentData
{
    /// <summary>
    /// 発動時に再生する AudioClip。null の場合は再生しない。
    /// </summary>
    public UnityObjectRef<AudioClip> SfxClip;

    /// <summary>
    /// 発動時に生成する VFX prefab。null の場合は生成しない。
    /// </summary>
    public UnityObjectRef<GameObject> VfxPrefab;
}

/// <summary>
/// Attack skill に倍率をかける passive buff skill の定義値。
/// </summary>
public struct BuffSkillConfig : IComponentData
{
    /// <summary>
    /// Buff 定義 ID。
    /// </summary>
    public int Id;

    /// <summary>
    /// Buff の処理 ID。現在は Target と Multiplier を直接使う。
    /// </summary>
    public int LogicId;

    /// <summary>
    /// 対象 status に掛ける倍率。
    /// </summary>
    public float Multiplier;

    /// <summary>
    /// この buff が補正する status。
    /// </summary>
    public BuffTargetStatus Target;
}

/// <summary>
/// Buff skill の実行状態。今は level のみだが、将来 stack / duration をここへ足す。
/// </summary>
public struct BuffSkillState : IComponentData
{
    public int Level;
}

/// <summary>
/// Authoring / default factory が返す attack skill の初期データ。
/// Baker はこの値を slot entity または SkillEntity prefab へ分解して追加する。
/// </summary>
public struct AttackSkillDefinition
{
    /// <summary>
    /// 攻撃の基礎定義。
    /// </summary>
    public AttackSkillConfig Config;

    /// <summary>
    /// LogicId ごとの追加定義。
    /// </summary>
    public AttackSkillAdvancedConfig AdvancedConfig;

    /// <summary>
    /// 発動中の damage / SFX / VFX タイミング。
    /// </summary>
    public AttackSkillTimingConfig Timing;

    /// <summary>
    /// Slot に配置するときの初期実行状態。
    /// </summary>
    public AttackSkillState State;

    /// <summary>
    /// Slot に配置するときの初期 target 状態。
    /// </summary>
    public SkillCastTarget CastTarget;

    /// <summary>
    /// 命中時に適用する debuff 定義配列。
    /// </summary>
    public FixedList512Bytes<SkillDebuffSpec> DebuffSpecs;
}

/// <summary>
/// Skill entity がどの owner の何番 slot かを表す。
/// </summary>
public struct SkillSlotComponent : IComponentData
{
    /// <summary>
    /// この slot を所有する Player / Boss などの entity。
    /// </summary>
    public Entity Owner;

    /// <summary>
    /// Owner 内の slot index。攻撃 skill は小さい index から優先発動する。
    /// </summary>
    public int SlotIndex;
}

/// <summary>
/// 装備済み skill slot だけを走査するためのタグ。
/// </summary>
public struct EquippedSkillTag : IComponentData
{

}

/// <summary>
/// Attack skill slot のタグ。
/// </summary>
public struct AttackSkillSlotTag : IComponentData
{

}

/// <summary>
/// Buff skill slot のタグ。
/// </summary>
public struct BuffSkillSlotTag : IComponentData
{

}

/// <summary>
/// owner に装備された buff skill の集約倍率。
/// 毎フレーム一時配列から作る値であり、component として保存しない。
/// </summary>
public struct BuffAccumulator
{
    /// <summary>
    /// ダメージに掛ける倍率。
    /// </summary>
    public float DamageMultiplier;

    /// <summary>
    /// 防御に掛ける倍率。
    /// </summary>
    public float DefenseMultiplier;

    /// <summary>
    /// Cooldown に掛ける倍率。
    /// </summary>
    public float CooltimeMultiplier;

    /// <summary>
    /// 攻撃範囲に掛ける倍率。
    /// </summary>
    public float AttackRangeMultiplier;

    /// <summary>
    /// 対象探索範囲に掛ける倍率。
    /// </summary>
    public float TargetRangeMultiplier;
}

/// <summary>
/// 初期装備 skill を作るための小さな factory。
/// </summary>
public static class SkillDefaults
{
    public static AttackSkillDefinition CreateDefaultAttackSkill(
        int id,
        float baseDamage,
        float cooltime,
        float baseTargetRange,
        float baseAttackRange,
        int level,
        int logicId)
    {
        return new AttackSkillDefinition
        {
            Config = new AttackSkillConfig
            {
                Id = id,
                BaseDamage = baseDamage,
                Cooltime = cooltime,
                BaseTargetRange = baseTargetRange,
                BaseAttackRange = baseAttackRange,
                BaseTargetCount = 1,
                TargetCountLevelWeight = 0f,
                MaxTargetCount = PlayerCombatConstants.MaxAttackCircleCount,
                TargetCountRoundMode = SkillCountRoundMode.Floor,
                LogicId = logicId
            },
            AdvancedConfig = SkillDefaults.CreateDefaultAdvancedConfig(),
            Timing = new AttackSkillTimingConfig
            {
                DamageDelay = 0f,
                SfxDelay = 0f,
                SfxVolume = 1f,
                VfxDelay = 0f,
                VfxDuration = 1f,
                VfxPrefabRadius = 1f,
                VfxDisplayRadius = 0f,
                HasSfx = 0,
                HasVfx = 0
            },
            State = new AttackSkillState
            {
                Timer = 0f,
                CastElapsedTime = 0f,
                Level = math.clamp(level, PlayerCombatConstants.MinSkillLevel, PlayerCombatConstants.MaxSkillLevel),
                DamageApplyCount = 0,
                DamageApplyTotalCount = 0,
                IsCooltime = 0,
                IsTriggered = 0,
                IsCasting = 0,
                DamageApplied = 0,
                SfxPlayed = 0,
                VfxSpawned = 0,
                StartedThisFrame = 0
            },
            CastTarget = new SkillCastTarget
            {
                Positions = default,
                AttackRanges = default,
                Damages = default,
                Shape = AttackSkillCastShape.CircleList,
                Origin = float3.zero,
                Direction = new float2(0f, 1f),
                ShapeLength = 0f,
                ShapeWidth = 0f,
                ShapeAngleCos = 1f,
                AttackRange = 0f,
                Damage = 0,
                RepeatCount = 1,
                RepeatInterval = 0f
            },
        };
    }

    public static AttackSkillAdvancedConfig CreateDefaultAdvancedConfig()
    {
        return new AttackSkillAdvancedConfig
        {
            ForwardSectorAngleDegrees = 90f,
            LineWidth = 0f,
            SecondaryBaseCount = 1,
            SecondaryCountLevelWeight = 0f,
            SecondaryMaxCount = PlayerCombatConstants.MaxAttackCircleCount,
            SecondaryTargetRange = 8f,
            SecondaryAttackRangeMultiplier = 0.5f,
            RandomGroundRadius = 0f,
            RepeatBaseCount = 1,
            RepeatCountLevelWeight = 0f,
            RepeatMaxCount = PlayerCombatConstants.MaxAttackCircleCount,
            RepeatInterval = 0.35f
        };
    }
}

/// <summary>
/// SkillCastTarget の可変長 circle 配列を安全に扱う helper。
/// </summary>
public static class SkillCastTargetUtility
{
    public static bool AddCircle(
        ref SkillCastTarget castTarget,
        float3 position,
        float attackRange,
        int damage)
    {
        if (castTarget.Positions.Length >= PlayerCombatConstants.MaxAttackCircleCount)
        {
            return false;
        }

        castTarget.Positions.Add(position);
        castTarget.AttackRanges.Add(math.max(0f, attackRange));
        castTarget.Damages.Add(math.max(0, damage));
        return true;
    }

    public static float GetAttackRange(SkillCastTarget castTarget, int index)
    {
        if (index >= 0 && index < castTarget.AttackRanges.Length)
        {
            return castTarget.AttackRanges[index];
        }

        return math.max(0f, castTarget.AttackRange);
    }

    public static int GetDamage(SkillCastTarget castTarget, int index)
    {
        if (index >= 0 && index < castTarget.Damages.Length)
        {
            return castTarget.Damages[index];
        }

        return math.max(0, castTarget.Damage);
    }
}
