using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// SkillEntity prefab / scene object で attack skill の定義値を編集するための Authoring。
/// </summary>
public sealed class AttackSkillAuthoring : MonoBehaviour
{
    [SerializeField]
    private int Id = 0;

    [SerializeField]
    private float BaseDamage = 10f;

    [SerializeField]
    private float Cooltime = 1f;

    [SerializeField]
    private float BaseTargetRange = 30f;

    [SerializeField]
    private float BaseAttackRange = 4f;

    [SerializeField]
    private int Level = 1;

    [SerializeField]
    private int LogicId = 0;

    private void OnValidate()
    {
        BaseDamage = math.max(0f, BaseDamage);
        Cooltime = math.max(0f, Cooltime);
        BaseTargetRange = math.max(0f, BaseTargetRange);
        BaseAttackRange = math.max(0f, BaseAttackRange);
        Level = math.max(1, Level);
    }

    public AttackSkillDefinition CreateAttackSkill()
    {
        return AttackSkillAuthoringUtility.CreateAttackSkill(
            Id,
            BaseDamage,
            Cooltime,
            BaseTargetRange,
            BaseAttackRange,
            Level,
            LogicId);
    }

    private sealed class Baker : Baker<AttackSkillAuthoring>
    {
        public override void Bake(AttackSkillAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var definition = authoring.CreateAttackSkill();

            AddComponent(entity, definition.Config);
        }
    }
}

/// <summary>
/// SkillEntity authoring が使うデータ生成関数。
/// </summary>
public static class AttackSkillAuthoringUtility
{
    public static AttackSkillDefinition CreateAttackSkill(
        int id,
        float baseDamage,
        float cooltime,
        float baseTargetRange,
        float baseAttackRange,
        int level,
        int logicId)
    {
        return SkillDefaults.CreateDefaultAttackSkill(
            id,
            math.max(0f, baseDamage),
            math.max(0f, cooltime),
            math.max(0f, baseTargetRange),
            math.max(0f, baseAttackRange),
            math.max(1, level),
            logicId);
    }
}
