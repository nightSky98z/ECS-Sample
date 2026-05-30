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
    public const float TargetSelectionDistanceWeight = 0.35f;
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
/// Attack skill の実行データ。
/// </summary>
public struct AttackSkillComponent : IComponentData
{
    public int Id;
    public float BaseDamage;
    public float Cooltime;
    public float Timer;
    public float BaseTargetRange;
    public float BaseAttackRange;
    public int Level;
    public int LogicId;
    public byte IsCooltime;
    public byte IsTriggered;
}

/// <summary>
/// Attack skill に倍率をかける passive buff skill。
/// </summary>
public struct BuffSkillComponent : IComponentData
{
    public int Id;
    public int Level;
    public int LogicId;
    public float Multiplier;
    public BuffTargetStatus Target;
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
    public static AttackSkillComponent CreateDefaultAttackSkill(
        int id,
        float baseDamage,
        float cooltime,
        float baseTargetRange,
        float baseAttackRange,
        int level,
        int logicId)
    {
        return new AttackSkillComponent
        {
            Id = id,
            BaseDamage = baseDamage,
            Cooltime = cooltime,
            Timer = 0f,
            BaseTargetRange = baseTargetRange,
            BaseAttackRange = baseAttackRange,
            Level = level,
            LogicId = logicId,
            IsCooltime = 0,
            IsTriggered = 0
        };
    }
}
