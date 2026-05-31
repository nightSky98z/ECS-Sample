using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Id0 の瞬時インパクト攻撃スキル定義。
/// Runtime 攻撃処理は SkillLogicSystem の LogicId 0 を使う。
/// </summary>
public sealed class InstantImpactSkillAuthoring : MonoBehaviour, IAttackSkillDefinitionAuthoring
{
    private const int SkillId = 0;
    private const int LogicId = 0;

    [Tooltip("バフやレベル倍率を掛ける前の基礎ダメージ。")]
    [SerializeField]
    private float BaseDamage = 10f;

    [Tooltip("発動後に再使用できるまでの秒数。")]
    [SerializeField]
    private float Cooltime = 1f;

    [Tooltip("攻撃対象を探す XZ 半径。")]
    [SerializeField]
    private float BaseTargetRange = 20f;

    [Tooltip("選ばれた対象を中心にダメージを与える XZ 半径。")]
    [SerializeField]
    private float BaseAttackRange = 4f;

    [Header("攻撃円数")]
    [Tooltip("スキルレベル 1 のときに出す攻撃円の数。")]
    [SerializeField]
    private int BaseTargetCount = 1;

    [Tooltip("レベル上昇で攻撃円数を増やす重み。計算式は BaseTargetCount + F((Level - 1) * TargetCountLevelWeight)。")]
    [SerializeField]
    private float TargetCountLevelWeight = 0f;

    [Tooltip("1 回の発動で出せる攻撃円の最大数。内部固定容量により 32 以下に制限する。")]
    [SerializeField]
    private int MaxTargetCount = PlayerCombatConstants.MaxAttackCircleCount;

    [Tooltip("レベルから攻撃円数を計算するときの丸め方。")]
    [SerializeField]
    private SkillCountRoundMode TargetCountRoundMode = SkillCountRoundMode.Floor;

    [Tooltip("この定義から作るスキルの初期レベル。")]
    [SerializeField]
    private int Level = 1;

    [Header("タイミング")]
    [Tooltip("発動してからダメージ判定が出るまでの秒数。")]
    [SerializeField]
    private float DamageDelay = 0.2f;

    [Tooltip("発動してから SFX を再生するまでの秒数。")]
    [SerializeField]
    private float SfxDelay = 0.1f;

    [Tooltip("SFX の音量。1 が基準音量。")]
    [SerializeField]
    private float SfxVolume = 1f;

    [Tooltip("発動してから VFX を生成するまでの秒数。")]
    [SerializeField]
    private float VfxDelay = 0.2f;

    [Tooltip("生成した VFX GameObject を残す秒数。0 なら即時破棄。")]
    [SerializeField]
    private float VfxDuration = 1f;

    [Tooltip("VFX プレハブをそのまま生成したとき、見た目で何 m 半径に見えるか。Prefab の元サイズ差をここで吸収する。")]
    [SerializeField]
    [FormerlySerializedAs("VfxBaseRadius")]
    private float VfxPrefabRadius = 1f;

    [Tooltip("VFX を表示したい半径。0 の場合は実際の攻撃半径に自動で合わせる。")]
    [SerializeField]
    private float VfxDisplayRadius = 0f;

    [Header("演出")]
    [Tooltip("スキル発動時に再生する SFX。未指定なら再生しない。")]
    [SerializeField]
    private AudioClip SfxClip = null;

    [Tooltip("スキル発動時に生成する VFX プレハブ。未指定なら生成しない。")]
    [SerializeField]
    private GameObject VfxPrefab = null;

    private void OnValidate()
    {
        BaseDamage = math.max(0f, BaseDamage);
        Cooltime = math.max(0f, Cooltime);
        BaseTargetRange = math.max(0f, BaseTargetRange);
        BaseAttackRange = math.max(0f, BaseAttackRange);
        BaseTargetCount = math.clamp(BaseTargetCount, 1, PlayerCombatConstants.MaxAttackCircleCount);
        TargetCountLevelWeight = math.max(0f, TargetCountLevelWeight);
        MaxTargetCount = math.clamp(
            math.max(BaseTargetCount, MaxTargetCount),
            1,
            PlayerCombatConstants.MaxAttackCircleCount);
        Level = math.clamp(Level, PlayerCombatConstants.MinSkillLevel, PlayerCombatConstants.MaxSkillLevel);
        DamageDelay = math.max(0f, DamageDelay);
        SfxDelay = math.max(0f, SfxDelay);
        SfxVolume = math.max(0f, SfxVolume);
        VfxDelay = math.max(0f, VfxDelay);
        VfxDuration = math.max(0f, VfxDuration);
        VfxPrefabRadius = math.max(0.0001f, VfxPrefabRadius);
        VfxDisplayRadius = math.max(0f, VfxDisplayRadius);
    }

    public AttackSkillDefinition CreateAttackSkill()
    {
        var definition = AttackSkillAuthoringUtility.CreateAttackSkill(
            SkillId,
            BaseDamage,
            Cooltime,
            BaseTargetRange,
            BaseAttackRange,
            Level,
            LogicId,
            DamageDelay,
            SfxDelay,
            SfxVolume,
            VfxDelay,
            VfxDuration,
            VfxPrefabRadius,
            VfxDisplayRadius);

        ApplyTargetCountConfig(ref definition.Config);
        definition.Timing.HasSfx = SfxClip != null ? (byte)1 : (byte)0;
        definition.Timing.HasVfx = VfxPrefab != null ? (byte)1 : (byte)0;
        return definition;
    }

    public bool TryCreatePresentation(out AttackSkillPresentation presentation)
    {
        if (SfxClip == null &&
            VfxPrefab == null)
        {
            presentation = default;
            return false;
        }

        presentation = new AttackSkillPresentation
        {
            SfxClip = SfxClip,
            VfxPrefab = VfxPrefab
        };
        return true;
    }

    private void ApplyTargetCountConfig(ref AttackSkillConfig config)
    {
        config.BaseTargetCount = BaseTargetCount;
        config.TargetCountLevelWeight = TargetCountLevelWeight;
        config.MaxTargetCount = MaxTargetCount;
        config.TargetCountRoundMode = TargetCountRoundMode;
    }

    private sealed class Baker : Baker<InstantImpactSkillAuthoring>
    {
        public override void Bake(InstantImpactSkillAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var definition = authoring.CreateAttackSkill();

            AddComponent(entity, definition.Config);
            AddComponent(entity, definition.Timing);

            if (authoring.SfxClip != null)
            {
                DependsOn(authoring.SfxClip);
            }

            if (authoring.VfxPrefab != null)
            {
                DependsOn(authoring.VfxPrefab);
            }

            if (authoring.TryCreatePresentation(out var presentation))
            {
                AddComponent(entity, presentation);
            }
        }
    }
}
