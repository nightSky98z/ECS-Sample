using Unity.Entities;

/// <summary>
/// プレイヤーが同時に装備できる skill slot 数。
/// </summary>
public static class PlayerCombatConstants
{
    public const int MaxAttackSkillCount = 5;
    public const int MaxBuffSkillCount = 5;
    public const int MaxSkillCount = MaxAttackSkillCount + MaxBuffSkillCount;

    public const float DamageRatePerLevel = 0.1f;
    public const int TargetSelectionDirectionBucketCount = 16;
    public const float TargetSelectionDistanceWeight = 0.6f;
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
/// Attack skill の定義値。SkillEntity prefab と slot の両方で共有する固定寄りデータ。
/// </summary>
public struct AttackSkillConfig : IComponentData
{
    public int Id;
    public float BaseDamage;
    public float Cooltime;
    public float BaseTargetRange;
    public float BaseAttackRange;
    public int LogicId;
}

/// <summary>
/// Attack skill slot の実行状態。
/// </summary>
public struct AttackSkillState : IComponentData
{
    public float Timer;
    public int Level;

    /// <summary>
    /// 0 = ready, 1 = cooltime running。
    /// </summary>
    public byte IsCooltime;

    /// <summary>
    /// 0 = idle, 1 = SkillLogicSystem が今回処理する。
    /// </summary>
    public byte IsTriggered;
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
    public AttackSkillState State;
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
                LogicId = logicId
            },
            State = new AttackSkillState
            {
                Timer = 0f,
                Level = level,
                IsCooltime = 0,
                IsTriggered = 0
            }
        };
    }
}
