using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// プレイヤーを見つけるためのタグ、データなし
/// </summary>
public struct PlayerTag : IComponentData
{

}

/// <summary>
/// 1フレームごとに入力 System が書き込む移動入力。
/// </summary>
public struct PlayerInput : IComponentData
{
    public float2 Move;
}

/// <summary>
/// GameObject の Player Prefab を ECS の Player Entity に変換する Authoring。
/// </summary>
public class PlayerEntity : MonoBehaviour
{
    [Tooltip("XZ 平面のゲーム用接触半径。MovementSystem の押し戻しに使う。")]
    [SerializeField]
    private float CollisionRadius = 0.5f;

    [SerializeField]
    private float MoveSpeed = 5f;

    [SerializeField]
    private float KnockbackDecayPerSecond = 18f;

    [Tooltip("プレイヤーの最大 HP。初期 HP も同じ値になる。")]
    [SerializeField]
    private int MaxHp = 100;

    [Tooltip("Velocity.y に加える重力加速度。")]
    [SerializeField]
    private float GravityAcceleration = -9.81f;

    [Header("Default Attack Skill")]
    [Tooltip("SkillEntity 定義 prefab。slot にはこの定義から作った実行データだけをコピーする。")]
    [SerializeField]
    private AttackSkillAuthoring DefaultAttackSkillEntity = null;

    [SerializeField]
    private int DefaultAttackSkillId = 0;

    [SerializeField]
    private float DefaultAttackSkillBaseDamage = 10f;

    [SerializeField]
    private float DefaultAttackSkillCooltime = 1f;

    [SerializeField]
    private float DefaultAttackSkillBaseTargetRange = 30f;

    [SerializeField]
    private float DefaultAttackSkillBaseAttackRange = 4f;

    [SerializeField]
    private int DefaultAttackSkillLevel = 1;

    [SerializeField]
    private int DefaultAttackSkillLogicId = 0;

    private void OnValidate()
    {
        DefaultAttackSkillBaseDamage = math.max(0f, DefaultAttackSkillBaseDamage);
        DefaultAttackSkillCooltime = math.max(0f, DefaultAttackSkillCooltime);
        DefaultAttackSkillBaseTargetRange = math.max(0f, DefaultAttackSkillBaseTargetRange);
        DefaultAttackSkillBaseAttackRange = math.max(0f, DefaultAttackSkillBaseAttackRange);
        DefaultAttackSkillLevel = math.max(1, DefaultAttackSkillLevel);
    }

    private class Baker : Unity.Entities.Baker<PlayerEntity>
    {
        public override void Bake(PlayerEntity authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent<PlayerTag>(entity);
            AddComponent(entity, new PlayerInput
            {
                Move = float2.zero
            });
            AddComponent(entity, new Velocity
            {
                Value = float3.zero
            });
            AddComponent(entity, new MoveSpeed
            {
                Value = authoring.MoveSpeed
            });
            AddComponent(entity, new KnockbackVelocity
            {
                Value = float3.zero,
                DecayPerSecond = authoring.KnockbackDecayPerSecond
            });
            AddComponent(entity, HealthMath.CreateHealth(
                authoring.MaxHp,
                authoring.MaxHp));
            AddComponent(entity, new CollisionRadius
            {
                Value = authoring.CollisionRadius
            });
            AddComponent(entity, new Gravity
            {
                Acceleration = authoring.GravityAcceleration
            });
            AddComponent(entity, new GroundSnap
            {
                GroundY = 0f,
                IsGrounded = 0
            });
            AddComponent(entity, new FacingDirection
            {
                Value = new float2(0f, 1f)
            });

            if (GroundSensorAuthoringUtility.TryCreateGroundSensor(authoring, out var groundSensor))
            {
                AddComponent(entity, groundSensor);
            }

            if (HealthBarAnchorAuthoringUtility.TryCreateHealthBarAnchor(authoring.transform, out var healthBarAnchor))
            {
                AddComponent(entity, healthBarAnchor);
            }

            BakeSkillSlots(entity, authoring);
        }

        private void BakeSkillSlots(Entity owner, PlayerEntity authoring)
        {
            for (var slotIndex = 0; slotIndex < PlayerCombatConstants.MaxAttackSkillCount; slotIndex++)
            {
                var slot = CreateAdditionalEntity(
                    TransformUsageFlags.None,
                    false,
                    $"AttackSkillSlot_{slotIndex}");

                AddComponent<AttackSkillSlotTag>(slot);
                AddComponent(slot, new SkillSlotComponent
                {
                    Owner = owner,
                    SlotIndex = slotIndex
                });

                if (slotIndex != 0)
                {
                    continue;
                }

                AddComponent<EquippedSkillTag>(slot);
                AddComponent(slot, authoring.CreateDefaultAttackSkill());
            }

            for (var slotIndex = 0; slotIndex < PlayerCombatConstants.MaxBuffSkillCount; slotIndex++)
            {
                var slot = CreateAdditionalEntity(
                    TransformUsageFlags.None,
                    false,
                    $"BuffSkillSlot_{slotIndex}");

                AddComponent<BuffSkillSlotTag>(slot);
                AddComponent(slot, new SkillSlotComponent
                {
                    Owner = owner,
                    SlotIndex = slotIndex
                });
            }
        }
    }

    private AttackSkillComponent CreateDefaultAttackSkill()
    {
        if (DefaultAttackSkillEntity != null)
        {
            return DefaultAttackSkillEntity.CreateAttackSkill();
        }

        return SkillDefaults.CreateDefaultAttackSkill(
            DefaultAttackSkillId,
            DefaultAttackSkillBaseDamage,
            DefaultAttackSkillCooltime,
            DefaultAttackSkillBaseTargetRange,
            DefaultAttackSkillBaseAttackRange,
            DefaultAttackSkillLevel,
            DefaultAttackSkillLogicId);
    }
}
