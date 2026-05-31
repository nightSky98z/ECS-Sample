using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// SkillEntity prefab / scene object で attack skill の定義値を編集するための Authoring。
/// </summary>
public sealed class AttackSkillAuthoring : MonoBehaviour
{
    [Tooltip("スキル定義 ID。UI やデバッグ表示でこのスキルを識別する。")]
    [SerializeField]
    private int Id = 0;

    [Tooltip("バフやレベル倍率を掛ける前の基礎ダメージ。")]
    [SerializeField]
    private float BaseDamage = 10f;

    [Tooltip("発動後に再使用できるまでの秒数。")]
    [SerializeField]
    private float Cooltime = 1f;

    [Tooltip("攻撃対象を探す XZ 半径。")]
    [SerializeField]
    private float BaseTargetRange = 30f;

    [Tooltip("選ばれた対象を中心にダメージを与える XZ 半径。")]
    [SerializeField]
    private float BaseAttackRange = 4f;

    [Tooltip("この定義から作るスキルの初期レベル。")]
    [SerializeField]
    private int Level = 1;

    [Tooltip("SkillLogicSystem が実行する攻撃ロジック ID。")]
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
