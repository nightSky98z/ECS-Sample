using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// プレイヤーを表すタグ。データは持たず、Query でプレイヤーを見分けるためだけに使う。
/// </summary>
public struct PlayerTag : IComponentData
{

}

/// <summary>
/// プレイヤーの移動入力。PlayerInputSystem が毎フレーム書き込む。
/// </summary>
public struct PlayerInput : IComponentData
{
    /// <summary>
    /// X がワールドの X 方向、Y がワールドの Z 方向の入力。長さは最大 1。
    /// </summary>
    public float2 Move;
}

/// <summary>
/// プレイヤーの Prefab（GameObject）を ECS の Entity に変換する Authoring。
/// この MonoBehaviour はゲーム中の処理を持たず、Inspector の設定値を Baker で Component に変換するだけ。
/// ゲーム中の更新は PlayerInputSystem・MovementSystem・スキル関連の System が行う。
/// </summary>
public class PlayerEntity : MonoBehaviour
{
    [Tooltip("XZ 平面のゲーム用接触半径。MovementSystem の押し戻しで使う。")]
    [SerializeField]
    private float CollisionRadius = 0.5f;

    [Tooltip("XZ 平面の入力移動速度。レベルアップでは変更しない。")]
    [SerializeField]
    private float MoveSpeed = 5f;

    [Tooltip("ノックバック速度を 1 秒あたりどれだけ減衰させるか。大きいほど早く止まる。")]
    [SerializeField]
    private float KnockbackDecayPerSecond = 18f;

    [Tooltip("プレイヤーの最大 HP。初期 HP も同じ値になる。")]
    [SerializeField]
    private int MaxHp = 100;

    [Tooltip("レベルアップごとに増える最大 HP。MoveSpeed はレベルアップでは変更しない。")]
    [SerializeField]
    private int MaxHpPerLevel = 5;

    [Tooltip("プレイヤーレベル 1 ごとのスキルダメージ加算率。0.02 はレベルごとに +2%。")]
    [SerializeField]
    private float SkillDamageRatePerLevel = ExperienceConstants.DefaultSkillDamageRatePerLevel;

    [Tooltip("Lv1 から Lv2 に必要な経験値。")]
    [SerializeField]
    private int BaseRequiredExperience = ExperienceConstants.BaseRequiredExperience;

    [Tooltip("次レベルの必要経験値に掛ける倍率。2 なら常に前レベルの 2 倍。")]
    [SerializeField]
    private float RequiredExperienceMultiplierPerLevel = ExperienceConstants.DefaultRequiredExperienceMultiplierPerLevel;

    [Tooltip("Velocity.y に加える重力加速度。")]
    [SerializeField]
    private float GravityAcceleration = -9.81f;

    [Tooltip("低 FPS ですり抜けた時、最後に記録した地面高さより何 m 下で救済するか。0 は無効。")]
    [SerializeField]
    private float FallRescueDepth = 3f;

    [Header("初期攻撃スキル")]
    [Tooltip("SkillEntity 定義プレハブ。スロットにはこの定義から作った実行データだけをコピーする。")]
    [SerializeField]
    private MonoBehaviour DefaultAttackSkillEntity = null;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う攻撃スキル ID。")]
    [SerializeField]
    private int DefaultAttackSkillId = 0;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う基礎ダメージ。")]
    [SerializeField]
    private float DefaultAttackSkillBaseDamage = 10f;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使うクールタイム秒。")]
    [SerializeField]
    private float DefaultAttackSkillCooltime = 1f;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使うターゲット探索半径。")]
    [SerializeField]
    private float DefaultAttackSkillBaseTargetRange = 30f;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う攻撃範囲半径。")]
    [SerializeField]
    private float DefaultAttackSkillBaseAttackRange = 4f;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う Lv1 の攻撃円数。")]
    [SerializeField]
    private int DefaultAttackSkillBaseTargetCount = 1;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う、レベル上昇ごとの攻撃円数増加重み。")]
    [SerializeField]
    private float DefaultAttackSkillTargetCountLevelWeight = 0f;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う攻撃円数上限。")]
    [SerializeField]
    private int DefaultAttackSkillMaxTargetCount = PlayerCombatConstants.MaxAttackCircleCount;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う攻撃円数の丸め方。")]
    [SerializeField]
    private SkillCountRoundMode DefaultAttackSkillTargetCountRoundMode = SkillCountRoundMode.Floor;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う初期スキルレベル。")]
    [SerializeField]
    private int DefaultAttackSkillLevel = 1;

    [Tooltip("DefaultAttackSkillEntity が未指定の場合に使う攻撃形状。0: 対象中心円形、1: 前方扇形、2: 直線貫通、3: 自分中心 AoE、4: 拡散爆発、5: 連鎖、6: 自分中心ランダム落下、7: 繰り返し攻撃。")]
    [SerializeField]
    [FormerlySerializedAs("DefaultAttackSkillLogicId")]
    [InspectorName("デフォルト攻撃ロジック")]
    private AttackSkillLogicKind DefaultAttackSkillLogic = AttackSkillLogicKind.TargetCenteredCircle;

    /// <summary>
    /// Inspector で入力された値を、有効な範囲に収める。
    /// </summary>
    private void OnValidate()
    {
        DefaultAttackSkillBaseDamage = math.max(0f, DefaultAttackSkillBaseDamage);
        DefaultAttackSkillCooltime = math.max(0f, DefaultAttackSkillCooltime);
        DefaultAttackSkillBaseTargetRange = math.max(0f, DefaultAttackSkillBaseTargetRange);
        DefaultAttackSkillBaseAttackRange = math.max(0f, DefaultAttackSkillBaseAttackRange);
        DefaultAttackSkillBaseTargetCount = math.clamp(
            DefaultAttackSkillBaseTargetCount,
            1,
            PlayerCombatConstants.MaxAttackCircleCount);
        DefaultAttackSkillTargetCountLevelWeight = math.max(0f, DefaultAttackSkillTargetCountLevelWeight);
        DefaultAttackSkillMaxTargetCount = math.clamp(
            math.max(DefaultAttackSkillBaseTargetCount, DefaultAttackSkillMaxTargetCount),
            1,
            PlayerCombatConstants.MaxAttackCircleCount);
        DefaultAttackSkillLevel = math.clamp(
            DefaultAttackSkillLevel,
            PlayerCombatConstants.MinSkillLevel,
            PlayerCombatConstants.MaxSkillLevel);
        MaxHp = math.max(1, MaxHp);
        MaxHpPerLevel = math.max(0, MaxHpPerLevel);
        SkillDamageRatePerLevel = math.max(0f, SkillDamageRatePerLevel);
        BaseRequiredExperience = math.max(1, BaseRequiredExperience);
        RequiredExperienceMultiplierPerLevel = math.max(1f, RequiredExperienceMultiplierPerLevel);
        FallRescueDepth = math.max(0f, FallRescueDepth);
    }

    private class Baker : Unity.Entities.Baker<PlayerEntity>
    {
        /// <summary>
        /// プレイヤーの Prefab を Entity に変換し、移動・HP・経験値などの Component を追加する。
        /// あわせて、スキルスロット用の Entity も作ってプレイヤーに紐付ける。
        /// </summary>
        public override void Bake(PlayerEntity authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            if (authoring.DefaultAttackSkillEntity != null)
            {
                DependsOn(authoring.DefaultAttackSkillEntity);
            }

            AddComponent<PlayerTag>(entity);
            AddComponent(entity, new EntityDisplayName
            {
                Value = new FixedString64Bytes(authoring.name)
            });
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
            var experienceLevelConfig = new ExperienceLevelConfig
            {
                BaseRequiredExperience = authoring.BaseRequiredExperience,
                RequiredExperienceMultiplierPerLevel = authoring.RequiredExperienceMultiplierPerLevel
            };

            AddComponent(entity, HealthMath.CreateFullHealth(authoring.MaxHp));
            AddComponent(entity, experienceLevelConfig);
            AddComponent(entity, ExperienceMath.CreateInitialExperience(experienceLevelConfig));
            AddComponent(entity, new PlayerLevelStats
            {
                BaseMaxHp = authoring.MaxHp,
                MaxHpPerLevel = authoring.MaxHpPerLevel,
                SkillDamageRatePerLevel = authoring.SkillDamageRatePerLevel
            });
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
            AddComponent(entity, new GroundFallRescue
            {
                MaxBelowGroundY = authoring.FallRescueDepth
            });
            AddComponent(entity, new FacingDirection
            {
                Value = new float2(0f, 1f)
            });

            if (GroundSensorAuthoringUtility.TryCreateGroundSensor(authoring, out var groundSensor))
            {
                AddComponent(entity, groundSensor);
            }

            // HP バーは必須ではないため、Prefab に目印がある場合だけ Component を追加する。
            if (HealthBarAnchorAuthoringUtility.TryCreateHealthBarAnchor(authoring.transform, out var healthBarAnchor))
            {
                AddComponent(entity, healthBarAnchor);
            }

            BakeSkillSlots(entity, authoring);
        }

        /// <summary>
        /// 攻撃スキル用・バフスキル用のスロット Entity をそれぞれ作り、プレイヤーに紐付ける。
        /// スキルをプレイヤーとは別の Entity にすることで、スキルの付け替えをプレイヤー本体を変更せずに行える。
        /// </summary>
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

                // 0 番の攻撃スロットにだけ初期スキルを装備する。
                // スキルを追加するときは、空いているスロットに同じ Component を追加すればよい。
                AddComponent<EquippedSkillTag>(slot);
                var defaultSkill = authoring.CreateDefaultAttackSkill();

                AddComponent(slot, defaultSkill.Config);
                AddComponent(slot, defaultSkill.AdvancedConfig);
                AddComponent(slot, defaultSkill.Timing);
                AddComponent(slot, defaultSkill.CastTarget);
                AddComponent(slot, defaultSkill.State);

                if (defaultSkill.DebuffSpecs.Length > 0)
                {
                    var debuffBuffer = AddBuffer<SkillDebuffSpec>(slot);

                    for (var debuffIndex = 0; debuffIndex < defaultSkill.DebuffSpecs.Length; debuffIndex++)
                    {
                        debuffBuffer.Add(defaultSkill.DebuffSpecs[debuffIndex]);
                    }
                }

                if (authoring.TryGetDefaultAttackSkillAuthoring(out var skillAuthoring) &&
                    skillAuthoring.TryCreatePresentation(out var presentation))
                {
                    AddComponent(slot, presentation);
                }
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

    /// <summary>
    /// 初期スキルのデータを作る。スキル定義の Prefab が設定されていればそれを使い、
    /// なければ Inspector の「Default Attack Skill ～」の値から作る。
    /// </summary>
    private AttackSkillDefinition CreateDefaultAttackSkill()
    {
        if (DefaultAttackSkillEntity != null)
        {
            if (TryGetDefaultAttackSkillAuthoring(out var skillAuthoring))
            {
                return skillAuthoring.CreateAttackSkill();
            }
        }

        var fallbackSkill = SkillDefaults.CreateDefaultAttackSkill(
            DefaultAttackSkillId,
            DefaultAttackSkillBaseDamage,
            DefaultAttackSkillCooltime,
            DefaultAttackSkillBaseTargetRange,
            DefaultAttackSkillBaseAttackRange,
            DefaultAttackSkillLevel,
            (int)DefaultAttackSkillLogic);

        fallbackSkill.Config.BaseTargetCount = DefaultAttackSkillBaseTargetCount;
        fallbackSkill.Config.TargetCountLevelWeight = DefaultAttackSkillTargetCountLevelWeight;
        fallbackSkill.Config.MaxTargetCount = DefaultAttackSkillMaxTargetCount;
        fallbackSkill.Config.TargetCountRoundMode = DefaultAttackSkillTargetCountRoundMode;
        return fallbackSkill;
    }

    /// <summary>
    /// Inspector で設定されたスキル定義の Prefab が、攻撃スキルの定義として使えるかを確認する。
    /// </summary>
    private bool TryGetDefaultAttackSkillAuthoring(out IAttackSkillDefinitionAuthoring skillAuthoring)
    {
        skillAuthoring = DefaultAttackSkillEntity as IAttackSkillDefinitionAuthoring;
        return skillAuthoring != null;
    }
}
