using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

// =============================================================================
// スキルとデバフのデータ定義（Component・定数・計算処理）
//
// ・スキルは「設定（Config）」と「実行状態（State）」の Component に分け、
//   設定は読み取り専用、状態だけを毎フレーム書き換える。
// ・可変長のデータは FixedList に入れ、Component の付け外し（構造変更）が起きないようにしている。
// =============================================================================

/// <summary>
/// プレイヤーの戦闘で共有する上限値と係数。
/// 配列の長さや FixedList の容量と一致させる必要があるため、実行中に変更しない。
/// </summary>
public static class PlayerCombatConstants
{
    /// <summary>
    /// プレイヤーが同時に装備できる攻撃スキルの数。
    /// </summary>
    public const int MaxAttackSkillCount = 5;

    /// <summary>
    /// プレイヤーが同時に装備できるバフスキルの数。
    /// </summary>
    public const int MaxBuffSkillCount = 5;

    /// <summary>
    /// 攻撃スキルとバフスキルを合わせたスロットの総数。
    /// </summary>
    public const int MaxSkillCount = MaxAttackSkillCount + MaxBuffSkillCount;

    /// <summary>
    /// スキルレベルの下限。Inspector・デバッグ操作・実行時の計算で共通に使う。
    /// </summary>
    public const int MinSkillLevel = 1;

    /// <summary>
    /// スキルレベルの上限。
    /// </summary>
    public const int MaxSkillLevel = 6;

    /// <summary>
    /// スキルレベルが 1 上がるごとに加算されるダメージ倍率。
    /// </summary>
    public const float DamageRatePerLevel = 0.1f;

    /// <summary>
    /// ターゲットを「敵が多い方向」から選ぶときに、周囲 360° を何方向に分けるか。
    /// </summary>
    public const int TargetSelectionDirectionBucketCount = 16;

    /// <summary>
    /// ターゲット選択で、近い敵を少し優先するための重み。
    /// </summary>
    public const float TargetSelectionDistanceWeight = 0.6f;

    /// <summary>
    /// 1 回のスキル発動で置ける攻撃範囲（円）の最大数。
    /// </summary>
    public const int MaxAttackCircleCount = 32;
}

/// <summary>
/// バフスキルが倍率をかけるステータスの種類。
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
/// レベルから攻撃範囲（円）の数を計算するときの、小数の丸め方。
/// </summary>
public enum SkillCountRoundMode
{
    Floor,
    Round,
    Ceil
}

/// <summary>
/// 攻撃スキルの攻撃パターン。Inspector で選択し、数値は AttackSkillConfig.LogicId として保存される。
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
/// 発動時に確定するダメージ判定の形。
/// </summary>
public enum AttackSkillCastShape
{
    /// <summary>円形の範囲を複数並べる（多くの攻撃パターンがこれを使う）。</summary>
    CircleList,
    /// <summary>前方の扇形。</summary>
    ForwardSector,
    /// <summary>前方へ伸びる直線（長方形）。</summary>
    PiercingLine
}

/// <summary>
/// デバフの種類。
/// </summary>
public enum DebuffKind
{
    /// <summary>移動速度低下。</summary>
    MoveSpeedDown,
    /// <summary>継続ダメージ（一定間隔で HP が減る）。</summary>
    DamageOverTime,
    /// <summary>攻撃力低下。</summary>
    AttackDown,
    /// <summary>防御力低下。</summary>
    DefenseDown,
    /// <summary>回避率低下。</summary>
    EvasionDown,
    /// <summary>氷結（完全に停止する）。</summary>
    Freeze,
    /// <summary>麻痺（一時停止したあと、徐々に動けるようになる）。</summary>
    Paralyze
}

/// <summary>
/// 同じ DebuffId のデバフがすでにかかっているときに、どう処理するか。
/// </summary>
public enum DebuffStackPolicy
{
    /// <summary>新しいデバフで上書きする（残り時間もリセット）。</summary>
    RefreshDuration,
    /// <summary>新しいほうが強いときだけ上書きし、弱いときは残り時間だけ延長する。</summary>
    ReplaceIfStronger,
    /// <summary>別のデバフとして重ねがけする。</summary>
    StackIndependent
}

/// <summary>
/// スキルが命中したときに付与するデバフの定義（スキル Entity に Buffer として持たせる）。
/// 値は Bake 時に正規化され、命中時に対象の DebuffRuntimeState へコピーされる。
/// </summary>
public struct SkillDebuffSpec : IBufferElementData
{
    /// <summary>
    /// デバフを識別する ID。StackPolicy で「同じデバフか」を判定するときに使う。
    /// </summary>
    public int DebuffId;

    /// <summary>
    /// デバフの種類。
    /// </summary>
    public DebuffKind Kind;

    /// <summary>
    /// 同じデバフがすでにかかっている場合の処理方法。
    /// </summary>
    public DebuffStackPolicy StackPolicy;

    /// <summary>
    /// 命中時に発生する確率（0 = 発生しない、1 = 必ず発生）。
    /// </summary>
    public float Chance;

    /// <summary>
    /// 継続時間（秒）。0 以下なら付与しない。
    /// </summary>
    public float Duration;

    /// <summary>
    /// 主な効果量。種類によって意味が変わる。
    /// 倍率系は 1 が基準、継続ダメージは 1 回あたりのダメージ、麻痺は「完全に止まっている時間」の割合。
    /// </summary>
    public float Value0;

    /// <summary>
    /// 補助の効果量。
    /// 麻痺では回復カーブの指数（1 = 一定ペースで回復、2 以上 = 最初はゆっくり、後半で一気に回復）。
    /// </summary>
    public float Value1;

    /// <summary>
    /// 継続ダメージの発生間隔（秒）。継続ダメージ以外では使わない。
    /// </summary>
    public float TickInterval;
}

/// <summary>
/// Inspector で編集する、命中時のデバフ定義。Bake 時に SkillDebuffSpec に変換される。
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
/// モンスターにかかっている 1 つのデバフ。
/// FixedList の中に値として保存するため、Entity や Managed オブジェクトへの参照は持たない。
/// </summary>
public struct ActiveDebuff
{
    /// <summary>
    /// デバフを識別する ID。
    /// </summary>
    public int DebuffId;

    /// <summary>
    /// デバフの種類。
    /// </summary>
    public DebuffKind Kind;

    /// <summary>
    /// もう一度付与されたときの処理方法。
    /// </summary>
    public DebuffStackPolicy StackPolicy;

    /// <summary>
    /// 残り時間（秒）。0 以下になったフレームで削除される。
    /// </summary>
    public float RemainingTime;

    /// <summary>
    /// 付与したときの継続時間（秒）。麻痺のように、経過の割合で効果が変わるデバフが使う。
    /// </summary>
    public float Duration;

    /// <summary>
    /// 主な効果量。種類によって意味が変わる（SkillDebuffSpec.Value0 を参照）。
    /// </summary>
    public float Value0;

    /// <summary>
    /// 補助の効果量（SkillDebuffSpec.Value1 を参照）。
    /// </summary>
    public float Value1;

    /// <summary>
    /// 継続ダメージの発生間隔（秒）。
    /// </summary>
    public float TickInterval;

    /// <summary>
    /// 前回の継続ダメージから経過した時間（秒）。
    /// </summary>
    public float TickTimer;
}

/// <summary>
/// モンスターにかかっているデバフの一覧。
/// デバフの付与・解除のたびに Component を付け外しすると構造変更のコストがかかるため、
/// この Component は常に持たせておき、中の固定長リストだけを書き換える。
/// </summary>
public struct DebuffRuntimeState : IComponentData
{
    /// <summary>
    /// 現在有効なデバフ（最大 DebuffConstants.MaxActiveDebuffCount 個）。
    /// </summary>
    public FixedList512Bytes<ActiveDebuff> ActiveDebuffs;
}

/// <summary>
/// すべてのデバフの効果をまとめた倍率。
/// 移動や AI の System はデバフの一覧を直接見ず、この倍率だけを読む。
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
/// 氷結中の Entity に付くタグ。
/// 氷結は「移動速度 0 倍」ではなく、Velocity を外して移動系 System の Query から外すことで表す。
/// </summary>
public struct FreezeTag : IComponentData
{

}

/// <summary>
/// 氷結の残り時間と、解除時に戻す速度。
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
    /// 氷結を解除するときに付け直す Velocity。水平方向の速度は、解除後のフレームで AI が上書きする。
    /// </summary>
    public Velocity RestoreVelocity;
}

/// <summary>
/// デバフ処理で共有する定数。
/// </summary>
public static class DebuffConstants
{
    /// <summary>1 体に同時にかけられるデバフの最大数（FixedList512Bytes に収まる数）。</summary>
    public const int MaxActiveDebuffCount = 12;

    /// <summary>継続ダメージの発生間隔が未設定のときに使う値（秒）。</summary>
    public const float DefaultDotTickInterval = 1f;

    /// <summary>麻痺の回復カーブ指数が未設定のときに使う値。</summary>
    public const float DefaultParalyzeRecoveryPower = 2f;
}

/// <summary>
/// デバフの計算処理。System から切り離した static 関数なので、単体テストしやすい。
/// </summary>
public static class DebuffMath
{
    /// <summary>
    /// デバフ定義の値を、実行時に扱える範囲（確率 0〜1、時間 0 以上など）に収める。
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
    /// デバフ定義から、対象に付与する ActiveDebuff を作る。
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
    /// デバフがかかっていない状態（すべての倍率が 1）の集約値を作る。
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
    /// デバフ 1 つ分の効果を集約値に掛け合わせる。同じ種類のデバフが複数あれば、倍率は掛け算で重なる。
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
    /// 継続ダメージの経過時間を進め、このフレームで発生するダメージ量を返す。
    /// </summary>
    /// <param name="debuff">対象のデバフ。</param>
    /// <param name="deltaTime">経過時間（秒）。</param>
    /// <param name="nextTickTimer">次のフレームへ持ち越す経過時間。</param>
    /// <returns>このフレームで発生したダメージ量。</returns>
    public static int CalculateDotDamage(ActiveDebuff debuff, float deltaTime, out float nextTickTimer)
    {
        var tickInterval = debuff.TickInterval > 0f
            ? debuff.TickInterval
            : DebuffConstants.DefaultDotTickInterval;
        var tickTimer = debuff.TickTimer + math.max(0f, deltaTime);
        // FPS が低く 1 フレームで複数回分の時間が経過した場合も、取りこぼさずにまとめて発生させる。
        var tickCount = (int)math.floor(tickTimer / tickInterval);

        nextTickTimer = tickTimer - tickCount * tickInterval;

        if (debuff.Kind != DebuffKind.DamageOverTime || tickCount <= 0)
        {
            return 0;
        }

        return tickCount * math.max(0, (int)math.ceil(debuff.Value0));
    }

    /// <summary>
    /// かかっている麻痺の中で、最も効果が強い（速度倍率が小さい）ものの倍率を返す。
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
    /// 麻痺の経過の割合から、移動速度倍率（0〜1）を計算する。
    /// </summary>
    /// <remarks>
    /// 前半（Value0 の割合）は完全に停止し、その後 smoothstep を Value1 乗したカーブで 1 倍まで回復する。
    /// 「しびれて止まる → だんだん動けるようになる」動きを表現している。
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
    /// ReplaceIfStronger のときに、新しいデバフが今のデバフより強いかを判定する。
    /// </summary>
    /// <remarks>
    /// 倍率を下げる系は値が小さいほど強い、継続ダメージは秒間ダメージが大きいほど強い、
    /// というように、種類ごとに「強さ」の基準を変えている。
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

    /// <summary>
    /// 継続ダメージの秒間ダメージ。
    /// </summary>
    private static float CalculateDotDamagePerSecond(ActiveDebuff debuff)
    {
        var tickInterval = debuff.TickInterval > 0f
            ? debuff.TickInterval
            : DebuffConstants.DefaultDotTickInterval;

        return math.max(0f, debuff.Value0) / tickInterval;
    }

    /// <summary>
    /// 麻痺の強さを比較するためのスコア（残り時間・停止の割合・回復の遅さを掛け合わせる）。
    /// </summary>
    private static float CalculateParalyzeStrengthScore(ActiveDebuff debuff)
    {
        var durationScore = math.max(0f, debuff.RemainingTime);
        var holdScore = 1f + math.saturate(debuff.Value0);
        var curveScore = 1f + math.max(0f, debuff.Value1) * 0.1f;

        return durationScore * holdScore * curveScore;
    }

    /// <summary>
    /// 発生確率（0〜1）に従って、今回デバフを付与するかを判定する。
    /// 0 と 1 のときは乱数を使わずに結果を返す。
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
/// 攻撃スキルの基本設定。スキル Entity の Prefab とスロットの両方で共有する、変更されないデータ。
/// </summary>
public struct AttackSkillConfig : IComponentData
{
    /// <summary>
    /// スキルの ID。UI・デバッグ表示・乱数 seed の識別に使う。
    /// </summary>
    public int Id;

    /// <summary>
    /// 基礎ダメージ（バフ・スキルレベル・プレイヤーレベルの倍率をかける前の値）。
    /// </summary>
    public float BaseDamage;

    /// <summary>
    /// 基礎クールタイム（秒）。バフで短縮される前の値。
    /// </summary>
    public float Cooltime;

    /// <summary>
    /// ターゲットを探す半径（XZ 平面）。
    /// </summary>
    public float BaseTargetRange;

    /// <summary>
    /// 攻撃範囲 1 つ分（円）の半径（XZ 平面）。
    /// </summary>
    public float BaseAttackRange;

    /// <summary>
    /// スキルレベル 1 のときの攻撃範囲（円）の数。
    /// </summary>
    public int BaseTargetCount;

    /// <summary>
    /// レベルが上がるごとに攻撃範囲の数を増やす係数。
    /// </summary>
    public float TargetCountLevelWeight;

    /// <summary>
    /// 1 回の発動で置ける攻撃範囲の数の上限。
    /// </summary>
    public int MaxTargetCount;

    /// <summary>
    /// レベルから攻撃範囲の数を計算するときの丸め方。
    /// </summary>
    public SkillCountRoundMode TargetCountRoundMode;

    /// <summary>
    /// 攻撃パターンの番号（AttackSkillLogicKind の値）。
    /// </summary>
    public int LogicId;
}

/// <summary>
/// 特定の攻撃パターンだけが使う追加設定（扇形の角度、二段階攻撃、ランダム落下、連続攻撃など）。
/// 括弧内の番号は、その設定を使う攻撃パターン（LogicId）。
/// </summary>
public struct AttackSkillAdvancedConfig : IComponentData
{
    /// <summary>
    /// [1] 前方扇形の角度（度）。
    /// </summary>
    public float ForwardSectorAngleDegrees;

    /// <summary>
    /// [2] 直線攻撃の幅。0 の場合は BaseAttackRange を使う。
    /// </summary>
    public float LineWidth;

    /// <summary>
    /// [4][5] 二段階目（拡散・連鎖）の基本数。
    /// </summary>
    public int SecondaryBaseCount;

    /// <summary>
    /// [4][5] レベルが上がるごとに二段階目の数を増やす係数。
    /// </summary>
    public float SecondaryCountLevelWeight;

    /// <summary>
    /// [4][5] 二段階目の数の上限。
    /// </summary>
    public int SecondaryMaxCount;

    /// <summary>
    /// [4][5] 二段階目を探す（または散らばらせる）半径。
    /// </summary>
    public float SecondaryTargetRange;

    /// <summary>
    /// [4][5] 二段階目の攻撃範囲の大きさ（一段階目に対する倍率）。
    /// </summary>
    public float SecondaryAttackRangeMultiplier;

    /// <summary>
    /// [6] ランダム落下の位置を選ぶ半径。0 の場合は BaseTargetRange を使う。
    /// </summary>
    public float RandomGroundRadius;

    /// <summary>
    /// [7] 連続攻撃の基本回数。
    /// </summary>
    public int RepeatBaseCount;

    /// <summary>
    /// [7] レベルが上がるごとに連続攻撃の回数を増やす係数。
    /// </summary>
    public float RepeatCountLevelWeight;

    /// <summary>
    /// [7] 連続攻撃の回数の上限。
    /// </summary>
    public int RepeatMaxCount;

    /// <summary>
    /// [7] 連続攻撃の間隔（秒）。
    /// </summary>
    public float RepeatInterval;
}

/// <summary>
/// ダメージ・SFX・VFX を出すタイミングと、演出の設定。すべての攻撃パターンで共通。
/// タイミングは「発動開始からの経過時間（秒）」で指定する。
/// </summary>
public struct AttackSkillTimingConfig : IComponentData
{
    /// <summary>
    /// 発動開始からダメージを与えるまでの時間（秒）。
    /// </summary>
    public float DamageDelay;

    /// <summary>
    /// 発動開始から SFX を再生するまでの時間（秒）。
    /// </summary>
    public float SfxDelay;

    /// <summary>
    /// SFX の音量（1 が基準）。
    /// </summary>
    public float SfxVolume;

    /// <summary>
    /// 発動開始から VFX を生成するまでの時間（秒）。
    /// </summary>
    public float VfxDelay;

    /// <summary>
    /// 生成した VFX を削除するまでの時間（秒）。
    /// </summary>
    public float VfxDuration;

    /// <summary>
    /// VFX Prefab の元の見た目の半径。Prefab ごとの大きさの違いを吸収するために使う。
    /// </summary>
    public float VfxPrefabRadius;

    /// <summary>
    /// VFX を表示したい半径。0 の場合は攻撃範囲に合わせる。
    /// </summary>
    public float VfxDisplayRadius;

    /// <summary>
    /// 1 なら SFX あり（再生が終わるまでクールタイムに入らない）。
    /// </summary>
    public byte HasSfx;

    /// <summary>
    /// 1 なら VFX あり（生成が終わるまでクールタイムに入らない）。
    /// </summary>
    public byte HasVfx;
}

/// <summary>
/// 攻撃スキルの実行状態（クールタイム中・発動中など）。
/// スロットはステージ中に増減しないため、Component を付け外しせずに値だけを書き換えて状態を表す。
/// フラグは byte（0 / 1）で持ち、Burst で扱いやすくしている。
/// </summary>
public struct AttackSkillState : IComponentData
{
    /// <summary>
    /// クールタイムの経過時間（秒）。IsCooltime が 1 のときだけ進む。
    /// </summary>
    public float Timer;

    /// <summary>
    /// 発動開始からの経過時間（秒）。ダメージ・SFX・VFX のタイミングはこの値で判定する。
    /// </summary>
    public float CastElapsedTime;

    /// <summary>
    /// スキルレベル。レベルアップやデバッグ操作で変わる。
    /// </summary>
    public int Level;

    /// <summary>
    /// 今回の発動で、すでにダメージを与えた回数。
    /// </summary>
    public int DamageApplyCount;

    /// <summary>
    /// 今回の発動でダメージを与える予定の回数（連続攻撃なら 2 以上）。
    /// </summary>
    public int DamageApplyTotalCount;

    /// <summary>
    /// 1 ならクールタイム中、0 なら発動可能。
    /// </summary>
    public byte IsCooltime;

    /// <summary>
    /// 1 なら発動予約済み（このフレームで SkillLogicSystem が処理する）。
    /// </summary>
    public byte IsTriggered;

    /// <summary>
    /// 1 なら発動中。ダメージ・SFX・VFX の待ち時間はこの状態のときに進む。
    /// </summary>
    public byte IsCasting;

    /// <summary>
    /// 1 なら今回の発動で予定していたダメージをすべて与えた。
    /// </summary>
    public byte DamageApplied;

    /// <summary>
    /// 1 なら今回の発動で SFX を再生済み。
    /// </summary>
    public byte SfxPlayed;

    /// <summary>
    /// 1 なら今回の発動で VFX を生成済み。
    /// </summary>
    public byte VfxSpawned;

    /// <summary>
    /// 1 なら発動を開始したフレーム。待ち時間 0 の演出を次のフレームで確実に拾うために使う。
    /// </summary>
    public byte StartedThisFrame;
}

/// <summary>
/// 発動開始時に確定した攻撃範囲とダメージ。
/// SkillLogicSystem が書き込み、ダメージ処理・演出・デバッグ表示が読む。
/// </summary>
public struct SkillCastTarget : IComponentData
{
    /// <summary>
    /// 攻撃範囲（円）の中心座標の一覧。
    /// </summary>
    public FixedList512Bytes<float3> Positions;

    /// <summary>
    /// 各円の半径（Positions と同じ順番）。
    /// </summary>
    public FixedList512Bytes<float> AttackRanges;

    /// <summary>
    /// 各円のダメージ（Positions と同じ順番）。
    /// </summary>
    public FixedList512Bytes<int> Damages;

    /// <summary>
    /// 今回の発動で使う判定の形。
    /// </summary>
    public AttackSkillCastShape Shape;

    /// <summary>
    /// 扇形・直線の起点。
    /// </summary>
    public float3 Origin;

    /// <summary>
    /// 扇形・直線の向き（XZ 平面、正規化済み）。
    /// </summary>
    public float2 Direction;

    /// <summary>
    /// 扇形の半径、または直線の長さ。
    /// </summary>
    public float ShapeLength;

    /// <summary>
    /// 直線の幅など、形ごとの補助的な幅。
    /// </summary>
    public float ShapeWidth;

    /// <summary>
    /// 扇形の半角の cos。判定時に角度を計算せず、内積と比較するだけで済むようにしている。
    /// </summary>
    public float ShapeAngleCos;

    /// <summary>
    /// 円ごとの半径が取得できないときに使う、既定の攻撃半径。
    /// </summary>
    public float AttackRange;

    /// <summary>
    /// 円ごとのダメージが取得できないときに使う、既定のダメージ。
    /// </summary>
    public int Damage;

    /// <summary>
    /// 1 回の発動でダメージを与える回数（連続攻撃用）。
    /// </summary>
    public int RepeatCount;

    /// <summary>
    /// RepeatCount が 2 以上のときの、2 回目以降の間隔（秒）。
    /// </summary>
    public float RepeatInterval;
}

/// <summary>
/// 攻撃スキルの SFX / VFX の参照。
/// Managed オブジェクト（AudioClip・GameObject）への参照を含むため、演出用の System だけが読む。
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
/// 攻撃スキルに倍率をかける、常時発動型のバフスキルの設定。
/// </summary>
public struct BuffSkillConfig : IComponentData
{
    /// <summary>
    /// バフスキルの ID。
    /// </summary>
    public int Id;

    /// <summary>
    /// バフの処理番号。現在は使っておらず、Target と Multiplier だけで効果が決まる。
    /// </summary>
    public int LogicId;

    /// <summary>
    /// 対象のステータスにかける倍率。
    /// </summary>
    public float Multiplier;

    /// <summary>
    /// 倍率をかけるステータス。
    /// </summary>
    public BuffTargetStatus Target;
}

/// <summary>
/// バフスキルの実行状態。
/// </summary>
public struct BuffSkillState : IComponentData
{
    /// <summary>
    /// バフスキルのレベル。
    /// </summary>
    public int Level;
}

/// <summary>
/// 攻撃スキル 1 つ分の初期データをまとめたもの。
/// Baker はこれを Config・State などの Component に分けて Entity に追加する。
/// </summary>
public struct AttackSkillDefinition
{
    /// <summary>
    /// 基本設定。
    /// </summary>
    public AttackSkillConfig Config;

    /// <summary>
    /// 攻撃パターンごとの追加設定。
    /// </summary>
    public AttackSkillAdvancedConfig AdvancedConfig;

    /// <summary>
    /// ダメージ・SFX・VFX のタイミング。
    /// </summary>
    public AttackSkillTimingConfig Timing;

    /// <summary>
    /// 実行状態の初期値。
    /// </summary>
    public AttackSkillState State;

    /// <summary>
    /// 攻撃範囲の初期値（空）。
    /// </summary>
    public SkillCastTarget CastTarget;

    /// <summary>
    /// 命中時に付与するデバフの一覧。
    /// </summary>
    public FixedList512Bytes<SkillDebuffSpec> DebuffSpecs;
}

/// <summary>
/// スキル Entity が「誰の」「何番目の」スロットに装備されているかを表す。
/// スキルを所有者とは別の Entity にすることで、スキルの追加・入れ替えを所有者側の変更なしで行える。
/// </summary>
public struct SkillSlotComponent : IComponentData
{
    /// <summary>
    /// スキルの所有者（プレイヤーなど）。
    /// </summary>
    public Entity Owner;

    /// <summary>
    /// スロット番号。攻撃スキルは番号が小さいものから優先して発動する。
    /// </summary>
    public int SlotIndex;
}

/// <summary>
/// 装備中のスキルだけを Query で取り出すためのタグ。
/// </summary>
public struct EquippedSkillTag : IComponentData
{

}

/// <summary>
/// 攻撃スキルのスロットを表すタグ。
/// </summary>
public struct AttackSkillSlotTag : IComponentData
{

}

/// <summary>
/// バフスキルのスロットを表すタグ。
/// </summary>
public struct BuffSkillSlotTag : IComponentData
{

}

/// <summary>
/// 所有者が装備しているバフスキルの倍率を合算したもの。
/// 必要なときにその場で計算する一時的な値なので、Component としては保存しない。
/// </summary>
public struct BuffAccumulator
{
    /// <summary>
    /// ダメージにかける倍率。
    /// </summary>
    public float DamageMultiplier;

    /// <summary>
    /// 防御力にかける倍率。
    /// </summary>
    public float DefenseMultiplier;

    /// <summary>
    /// クールタイムにかける倍率。
    /// </summary>
    public float CooltimeMultiplier;

    /// <summary>
    /// 攻撃範囲にかける倍率。
    /// </summary>
    public float AttackRangeMultiplier;

    /// <summary>
    /// ターゲットを探す範囲にかける倍率。
    /// </summary>
    public float TargetRangeMultiplier;
}

/// <summary>
/// スキルの既定値を作る関数をまとめたクラス。
/// </summary>
public static class SkillDefaults
{
    /// <summary>
    /// 最小限の設定から、攻撃スキル 1 つ分の初期データを作る。演出（SFX / VFX）とデバフはなし。
    /// </summary>
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

    /// <summary>
    /// 攻撃パターンごとの追加設定の既定値を作る。
    /// </summary>
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
/// SkillCastTarget の円のリスト（位置・半径・ダメージの 3 つの配列）をまとめて安全に操作する関数。
/// </summary>
public static class SkillCastTargetUtility
{
    /// <summary>
    /// 攻撃範囲（円）を 1 つ追加する。上限に達している場合は追加せず false を返す。
    /// </summary>
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

    /// <summary>
    /// index 番目の円の半径を返す。範囲外の index の場合は既定の半径を返す。
    /// </summary>
    public static float GetAttackRange(SkillCastTarget castTarget, int index)
    {
        if (index >= 0 && index < castTarget.AttackRanges.Length)
        {
            return castTarget.AttackRanges[index];
        }

        return math.max(0f, castTarget.AttackRange);
    }

    /// <summary>
    /// index 番目の円のダメージを返す。範囲外の index の場合は既定のダメージを返す。
    /// </summary>
    public static int GetDamage(SkillCastTarget castTarget, int index)
    {
        if (index >= 0 && index < castTarget.Damages.Length)
        {
            return castTarget.Damages[index];
        }

        return math.max(0, castTarget.Damage);
    }
}
