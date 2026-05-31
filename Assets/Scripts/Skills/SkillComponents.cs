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
/// Attack skill の発動タイミングと演出用数値。
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
    public float AttackRange;
    public int Damage;
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
    public AttackSkillTimingConfig Timing;
    public AttackSkillState State;
    public SkillCastTarget CastTarget;
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
                AttackRange = 0f,
                Damage = 0
            },
        };
    }
}
