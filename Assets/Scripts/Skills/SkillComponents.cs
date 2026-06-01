using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// プレイヤーが同時に装備できる skill slot 数。
/// </summary>
public static class PlayerCombatConstants
{
    public const int MaxAttackSkillCount = 5;
    public const int MaxBuffSkillCount = 5;
    public const int MaxSkillCount = MaxAttackSkillCount + MaxBuffSkillCount;
    public const int MinSkillLevel = 1;
    public const int MaxSkillLevel = 6;

    public const float DamageRatePerLevel = 0.1f;
    public const int TargetSelectionDirectionBucketCount = 16;
    public const float TargetSelectionDistanceWeight = 0.6f;
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
/// </summary>
public struct SkillDebuffSpec : IBufferElementData
{
    public int DebuffId;
    public DebuffKind Kind;
    public DebuffStackPolicy StackPolicy;
    public float Chance;
    public float Duration;
    public float Value0;
    public float Value1;
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

    [Tooltip("効果値 0。速度低下なら移動速度倍率、DoT なら tick ダメージ。")]
    public float Value0;

    [Tooltip("効果値 1。将来の拡張用。")]
    public float Value1;

    [Tooltip("DoT の tick 間隔秒。0 以下なら 1 秒として扱う。")]
    public float TickInterval;
}

/// <summary>
/// Monster が保持する実行中 debuff。
/// </summary>
public struct ActiveDebuff
{
    public int DebuffId;
    public DebuffKind Kind;
    public DebuffStackPolicy StackPolicy;
    public float RemainingTime;
    public float Value0;
    public float Value1;
    public float TickInterval;
    public float TickTimer;
}

/// <summary>
/// Monster に固定で持たせる debuff runtime 状態。
/// </summary>
public struct DebuffRuntimeState : IComponentData
{
    public FixedList512Bytes<ActiveDebuff> ActiveDebuffs;
}

/// <summary>
/// 各 system が読む集約済み debuff 値。
/// </summary>
public struct DebuffAggregate : IComponentData
{
    public float MoveSpeedMultiplier;
    public float AttackMultiplier;
    public float DefenseMultiplier;
    public float EvasionMultiplier;
    public byte IsMovementLocked;
    public byte IsActionLocked;
}

/// <summary>
/// Debuff 処理で共有する固定値。
/// </summary>
public static class DebuffConstants
{
    public const int MaxActiveDebuffCount = 12;
    public const float DefaultDotTickInterval = 1f;
}

/// <summary>
/// Debuff system から独立して検査できる計算。
/// </summary>
public static class DebuffMath
{
    public static SkillDebuffSpec NormalizeSpec(SkillDebuffSpec spec)
    {
        spec.Chance = math.saturate(spec.Chance);
        spec.Duration = math.max(0f, spec.Duration);
        spec.TickInterval = spec.TickInterval > 0f
            ? spec.TickInterval
            : DebuffConstants.DefaultDotTickInterval;

        return spec;
    }

    public static ActiveDebuff CreateActiveDebuff(SkillDebuffSpec spec)
    {
        var normalizedSpec = NormalizeSpec(spec);

        return new ActiveDebuff
        {
            DebuffId = normalizedSpec.DebuffId,
            Kind = normalizedSpec.Kind,
            StackPolicy = normalizedSpec.StackPolicy,
            RemainingTime = normalizedSpec.Duration,
            Value0 = normalizedSpec.Value0,
            Value1 = normalizedSpec.Value1,
            TickInterval = normalizedSpec.TickInterval,
            TickTimer = 0f
        };
    }

    public static DebuffAggregate CreateNeutralAggregate()
    {
        return new DebuffAggregate
        {
            MoveSpeedMultiplier = 1f,
            AttackMultiplier = 1f,
            DefenseMultiplier = 1f,
            EvasionMultiplier = 1f,
            IsMovementLocked = 0,
            IsActionLocked = 0
        };
    }

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
            case DebuffKind.Freeze:
            case DebuffKind.Paralyze:
                aggregate.MoveSpeedMultiplier = 0f;
                aggregate.IsMovementLocked = 1;
                aggregate.IsActionLocked = 1;
                break;
        }

        aggregate.MoveSpeedMultiplier = math.max(0f, aggregate.MoveSpeedMultiplier);
        aggregate.AttackMultiplier = math.max(0f, aggregate.AttackMultiplier);
        aggregate.DefenseMultiplier = math.max(0f, aggregate.DefenseMultiplier);
        aggregate.EvasionMultiplier = math.max(0f, aggregate.EvasionMultiplier);
        return aggregate;
    }

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
            case DebuffKind.Freeze:
            case DebuffKind.Paralyze:
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
    public int Id;
    public float BaseDamage;
    public float Cooltime;
    public float BaseTargetRange;
    public float BaseAttackRange;
    public int BaseTargetCount;
    public float TargetCountLevelWeight;
    public int MaxTargetCount;
    public SkillCountRoundMode TargetCountRoundMode;
    public int LogicId;
}

/// <summary>
/// 2 段階攻撃、ランダム落下、繰り返し攻撃で使う追加定義値。
/// </summary>
public struct AttackSkillAdvancedConfig : IComponentData
{
    public float ForwardSectorAngleDegrees;
    public float LineWidth;
    public int SecondaryBaseCount;
    public float SecondaryCountLevelWeight;
    public int SecondaryMaxCount;
    public float SecondaryTargetRange;
    public float SecondaryAttackRangeMultiplier;
    public float RandomGroundRadius;
    public int RepeatBaseCount;
    public float RepeatCountLevelWeight;
    public int RepeatMaxCount;
    public float RepeatInterval;
}

/// <summary>
/// Attack skill の全ロジックで共通利用する発動タイミングと演出用数値。
/// </summary>
public struct AttackSkillTimingConfig : IComponentData
{
    public float DamageDelay;
    public float SfxDelay;
    public float SfxVolume;
    public float VfxDelay;
    public float VfxDuration;
    public float VfxPrefabRadius;
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
/// </summary>
public struct AttackSkillState : IComponentData
{
    public float Timer;
    public float CastElapsedTime;
    public int Level;
    public int DamageApplyCount;
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
/// </summary>
public struct SkillCastTarget : IComponentData
{
    public FixedList512Bytes<float3> Positions;
    public FixedList512Bytes<float> AttackRanges;
    public FixedList512Bytes<int> Damages;
    public AttackSkillCastShape Shape;
    public float3 Origin;
    public float2 Direction;
    public float ShapeLength;
    public float ShapeWidth;
    public float ShapeAngleCos;
    public float AttackRange;
    public int Damage;
    public int RepeatCount;
    public float RepeatInterval;
}

/// <summary>
/// Attack skill の SFX / VFX prefab 参照。
/// </summary>
public struct AttackSkillPresentation : IComponentData
{
    public UnityObjectRef<AudioClip> SfxClip;
    public UnityObjectRef<GameObject> VfxPrefab;
}

/// <summary>
/// Attack skill に倍率をかける passive buff skill の定義値。
/// </summary>
public struct BuffSkillConfig : IComponentData
{
    public int Id;
    public int LogicId;
    public float Multiplier;
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
/// </summary>
public struct AttackSkillDefinition
{
    public AttackSkillConfig Config;
    public AttackSkillAdvancedConfig AdvancedConfig;
    public AttackSkillTimingConfig Timing;
    public AttackSkillState State;
    public SkillCastTarget CastTarget;
    public FixedList512Bytes<SkillDebuffSpec> DebuffSpecs;
}

/// <summary>
/// Skill entity がどの owner の何番 slot かを表す。
/// </summary>
public struct SkillSlotComponent : IComponentData
{
    public Entity Owner;
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
/// </summary>
public struct BuffAccumulator
{
    public float DamageMultiplier;
    public float DefenseMultiplier;
    public float CooltimeMultiplier;
    public float AttackRangeMultiplier;
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
