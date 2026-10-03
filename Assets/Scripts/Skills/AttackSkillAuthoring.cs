using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// 攻撃スキルの定義を提供する Authoring の共通インターフェース。
/// PlayerEntity はこのインターフェースを通してスキルの定義を受け取るため、
/// 定義の作り方が違う Authoring（AttackSkillAuthoring / InstantImpactSkillAuthoring）を同じように扱える。
/// </summary>
public interface IAttackSkillDefinitionAuthoring
{
    /// <summary>
    /// 攻撃スキルの初期データを作る。
    /// </summary>
    AttackSkillDefinition CreateAttackSkill();

    /// <summary>
    /// SFX / VFX の参照を作る。演出がない場合は false を返す。
    /// </summary>
    bool TryCreatePresentation(out AttackSkillPresentation presentation);
}

/// <summary>
/// 攻撃スキルの定義を Inspector で編集するための Authoring。スキル定義用の Prefab（SkillEntity）に付ける。
/// 攻撃パターン・ダメージ・範囲・タイミング・デバフ・演出をすべてここで設定でき、コードを書かずに新しいスキルを作れる。
/// </summary>
public sealed class AttackSkillAuthoring : MonoBehaviour, IAttackSkillDefinitionAuthoring
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

    [Tooltip("SkillLogicSystem が実行する攻撃形状。0: 対象中心円形、1: 前方扇形、2: 直線貫通、3: 自分中心 AoE、4: 拡散爆発、5: 連鎖、6: 自分中心ランダム落下、7: 繰り返し攻撃。")]
    [SerializeField]
    [FormerlySerializedAs("LogicId")]
    [InspectorName("攻撃ロジック")]
    private AttackSkillLogicKind LogicKind = AttackSkillLogicKind.TargetCenteredCircle;

    [Header("形状設定")]
    [Tooltip("LogicId 1 の扇形角度。90 なら前方 45 度ずつに攻撃する。")]
    [SerializeField]
    private float ForwardSectorAngleDegrees = 90f;

    [Tooltip("LogicId 2 の直線攻撃の幅。0 の場合は攻撃半径を幅として使う。")]
    [SerializeField]
    private float LineWidth = 0f;

    [Header("追加ロジック")]
    [Tooltip("LogicId 4/5 で使う 2 段階目の基礎ターゲット数。")]
    [SerializeField]
    private int SecondaryBaseCount = 1;

    [Tooltip("LogicId 4/5 で使う 2 段階目ターゲット数のレベル重み。")]
    [SerializeField]
    private float SecondaryCountLevelWeight = 0f;

    [Tooltip("LogicId 4/5 で使う 2 段階目ターゲット数の上限。")]
    [SerializeField]
    private int SecondaryMaxCount = PlayerCombatConstants.MaxAttackCircleCount;

    [Tooltip("LogicId 4/5 で使う 2 段階目の探索半径。")]
    [SerializeField]
    private float SecondaryTargetRange = 8f;

    [Tooltip("LogicId 4 の小さい爆発半径倍率。1 なら通常攻撃範囲と同じ。")]
    [SerializeField]
    private float SecondaryAttackRangeMultiplier = 0.5f;

    [Tooltip("LogicId 6 で自分中心にランダム座標を選ぶ半径。0 ならターゲット探索半径を使う。")]
    [SerializeField]
    private float RandomGroundRadius = 0f;

    [Tooltip("LogicId 7 で同じ場所に繰り返す基礎回数。")]
    [SerializeField]
    private int RepeatBaseCount = 1;

    [Tooltip("LogicId 7 で繰り返し回数を増やすレベル重み。")]
    [SerializeField]
    private float RepeatCountLevelWeight = 0f;

    [Tooltip("LogicId 7 で繰り返し回数の上限。")]
    [SerializeField]
    private int RepeatMaxCount = PlayerCombatConstants.MaxAttackCircleCount;

    [Tooltip("LogicId 7 で同じ場所に再攻撃する間隔秒。")]
    [SerializeField]
    private float RepeatInterval = 0.35f;

    [Header("命中時デバフ")]
    [Tooltip("攻撃が命中した敵に付与するデバフ。空ならデバフなし。")]
    [SerializeField]
    private SkillDebuffAuthoring[] OnHitDebuffs = null;

    [Header("タイミング（全攻撃ロジック共通）")]
    [Tooltip("全攻撃ロジック共通。発動してからダメージ判定が出るまでの秒数。")]
    [SerializeField]
    [InspectorName("ダメージ発生遅延")]
    private float DamageDelay = 0f;

    [Tooltip("全攻撃ロジック共通。発動してから SFX を再生するまでの秒数。")]
    [SerializeField]
    [InspectorName("SFX 再生遅延")]
    private float SfxDelay = 0f;

    [Tooltip("全攻撃ロジック共通。SFX の音量。1 が基準音量。")]
    [SerializeField]
    [InspectorName("SFX 音量")]
    private float SfxVolume = 1f;

    [Tooltip("全攻撃ロジック共通。発動してから VFX を生成するまでの秒数。")]
    [SerializeField]
    [InspectorName("VFX 生成遅延")]
    private float VfxDelay = 0f;

    [Tooltip("全攻撃ロジック共通。生成した VFX GameObject を残す秒数。0 なら即時破棄。")]
    [SerializeField]
    [InspectorName("VFX 表示時間")]
    private float VfxDuration = 1f;

    [Tooltip("全攻撃ロジック共通。VFX プレハブをそのまま生成したとき、見た目で何 m 半径に見えるか。Prefab の元サイズ差をここで吸収する。")]
    [SerializeField]
    [FormerlySerializedAs("VfxBaseRadius")]
    [InspectorName("VFX プレハブ半径")]
    private float VfxPrefabRadius = 1f;

    [Tooltip("全攻撃ロジック共通。VFX を表示したい半径。0 の場合は実際の攻撃半径に自動で合わせる。")]
    [SerializeField]
    [InspectorName("VFX 表示半径")]
    private float VfxDisplayRadius = 0f;

    [Header("演出")]
    [Tooltip("スキル発動時に再生する SFX。未指定なら再生しない。")]
    [SerializeField]
    private AudioClip SfxClip = null;

    [Tooltip("スキル発動時に生成する VFX プレハブ。未指定なら生成しない。")]
    [SerializeField]
    private GameObject VfxPrefab = null;

    /// <summary>
    /// Inspector で入力された値を、有効な範囲に収める。
    /// </summary>
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
        ForwardSectorAngleDegrees = math.clamp(ForwardSectorAngleDegrees, 1f, 360f);
        LineWidth = math.max(0f, LineWidth);
        SecondaryBaseCount = math.clamp(SecondaryBaseCount, 1, PlayerCombatConstants.MaxAttackCircleCount);
        SecondaryCountLevelWeight = math.max(0f, SecondaryCountLevelWeight);
        SecondaryMaxCount = math.clamp(
            math.max(SecondaryBaseCount, SecondaryMaxCount),
            1,
            PlayerCombatConstants.MaxAttackCircleCount);
        SecondaryTargetRange = math.max(0f, SecondaryTargetRange);
        SecondaryAttackRangeMultiplier = math.max(0f, SecondaryAttackRangeMultiplier);
        RandomGroundRadius = math.max(0f, RandomGroundRadius);
        RepeatBaseCount = math.clamp(RepeatBaseCount, 1, PlayerCombatConstants.MaxAttackCircleCount);
        RepeatCountLevelWeight = math.max(0f, RepeatCountLevelWeight);
        RepeatMaxCount = math.clamp(
            math.max(RepeatBaseCount, RepeatMaxCount),
            1,
            PlayerCombatConstants.MaxAttackCircleCount);
        RepeatInterval = math.max(0f, RepeatInterval);
        NormalizeDebuffAuthoringArray(OnHitDebuffs);
        DamageDelay = math.max(0f, DamageDelay);
        SfxDelay = math.max(0f, SfxDelay);
        SfxVolume = math.max(0f, SfxVolume);
        VfxDelay = math.max(0f, VfxDelay);
        VfxDuration = math.max(0f, VfxDuration);
        VfxPrefabRadius = math.max(0.0001f, VfxPrefabRadius);
        VfxDisplayRadius = math.max(0f, VfxDisplayRadius);
    }

    /// <summary>
    /// Inspector の設定値から攻撃スキルの初期データを作る。
    /// </summary>
    public AttackSkillDefinition CreateAttackSkill()
    {
        var definition = AttackSkillAuthoringUtility.CreateAttackSkill(
            Id,
            BaseDamage,
            Cooltime,
            BaseTargetRange,
            BaseAttackRange,
            Level,
            (int)LogicKind,
            DamageDelay,
            SfxDelay,
            SfxVolume,
            VfxDelay,
            VfxDuration,
            VfxPrefabRadius,
            VfxDisplayRadius);

        ApplyTargetCountConfig(ref definition.Config);
        definition.AdvancedConfig = CreateAdvancedConfig();
        CopyDebuffSpecs(ref definition.DebuffSpecs);
        // 演出が設定されている場合だけ、演出の完了を待ってからクールタイムに入る。
        definition.Timing.HasSfx = SfxClip != null ? (byte)1 : (byte)0;
        definition.Timing.HasVfx = VfxPrefab != null ? (byte)1 : (byte)0;
        return definition;
    }

    /// <summary>
    /// SFX / VFX の参照を作る。どちらも設定されていなければ false を返す。
    /// </summary>
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

    /// <summary>
    /// 攻撃範囲の数に関する設定を反映する。
    /// </summary>
    private void ApplyTargetCountConfig(ref AttackSkillConfig config)
    {
        config.BaseTargetCount = BaseTargetCount;
        config.TargetCountLevelWeight = TargetCountLevelWeight;
        config.MaxTargetCount = MaxTargetCount;
        config.TargetCountRoundMode = TargetCountRoundMode;
    }

    /// <summary>
    /// 攻撃パターンごとの追加設定を作る。
    /// </summary>
    private AttackSkillAdvancedConfig CreateAdvancedConfig()
    {
        return AttackSkillAuthoringUtility.CreateAdvancedConfig(
            ForwardSectorAngleDegrees,
            LineWidth,
            SecondaryBaseCount,
            SecondaryCountLevelWeight,
            SecondaryMaxCount,
            SecondaryTargetRange,
            SecondaryAttackRangeMultiplier,
            RandomGroundRadius,
            RepeatBaseCount,
            RepeatCountLevelWeight,
            RepeatMaxCount,
            RepeatInterval);
    }

    /// <summary>
    /// Inspector で設定したデバフを、実行時の形式に変換してコピーする。
    /// </summary>
    private void CopyDebuffSpecs(ref FixedList512Bytes<SkillDebuffSpec> debuffSpecs)
    {
        AttackSkillAuthoringUtility.CopyDebuffSpecs(OnHitDebuffs, ref debuffSpecs);
    }

    /// <summary>
    /// Inspector で入力されたデバフの値を、有効な範囲に収める。
    /// </summary>
    private static void NormalizeDebuffAuthoringArray(SkillDebuffAuthoring[] debuffs)
    {
        if (debuffs == null)
        {
            return;
        }

        for (var debuffIndex = 0; debuffIndex < debuffs.Length; debuffIndex++)
        {
            debuffs[debuffIndex].Chance = math.saturate(debuffs[debuffIndex].Chance);
            debuffs[debuffIndex].Duration = math.max(0f, debuffs[debuffIndex].Duration);
            debuffs[debuffIndex].TickInterval = debuffs[debuffIndex].TickInterval > 0f
                ? debuffs[debuffIndex].TickInterval
                : DebuffConstants.DefaultDotTickInterval;

            if (debuffs[debuffIndex].Kind == DebuffKind.Paralyze)
            {
                debuffs[debuffIndex].Value0 = math.saturate(debuffs[debuffIndex].Value0);
                debuffs[debuffIndex].Value1 = debuffs[debuffIndex].Value1 > 0f
                    ? debuffs[debuffIndex].Value1
                    : DebuffConstants.DefaultParalyzeRecoveryPower;
            }
        }
    }

    private sealed class Baker : Baker<AttackSkillAuthoring>
    {
        /// <summary>
        /// スキルの定義を Component に変換する。演出用の AudioClip や Prefab が変更されたときも Bake し直されるよう、依存関係を登録する。
        /// </summary>
        public override void Bake(AttackSkillAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var definition = authoring.CreateAttackSkill();

            AddComponent(entity, definition.Config);
            AddComponent(entity, definition.AdvancedConfig);
            AddComponent(entity, definition.Timing);

            if (definition.DebuffSpecs.Length > 0)
            {
                var debuffBuffer = AddBuffer<SkillDebuffSpec>(entity);

                for (var debuffIndex = 0; debuffIndex < definition.DebuffSpecs.Length; debuffIndex++)
                {
                    debuffBuffer.Add(definition.DebuffSpecs[debuffIndex]);
                }
            }

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

/// <summary>
/// スキル定義の Authoring（SkillEntity）で使う、データ作成の関数。
/// </summary>
public static class AttackSkillAuthoringUtility
{
    /// <summary>
    /// 攻撃パターンごとの追加設定を、値を有効な範囲に収めたうえで作る。
    /// </summary>
    public static AttackSkillAdvancedConfig CreateAdvancedConfig(
        float forwardSectorAngleDegrees,
        float lineWidth,
        int secondaryBaseCount,
        float secondaryCountLevelWeight,
        int secondaryMaxCount,
        float secondaryTargetRange,
        float secondaryAttackRangeMultiplier,
        float randomGroundRadius,
        int repeatBaseCount,
        float repeatCountLevelWeight,
        int repeatMaxCount,
        float repeatInterval)
    {
        return new AttackSkillAdvancedConfig
        {
            ForwardSectorAngleDegrees = math.clamp(forwardSectorAngleDegrees, 1f, 360f),
            LineWidth = math.max(0f, lineWidth),
            SecondaryBaseCount = math.clamp(
                secondaryBaseCount,
                1,
                PlayerCombatConstants.MaxAttackCircleCount),
            SecondaryCountLevelWeight = math.max(0f, secondaryCountLevelWeight),
            SecondaryMaxCount = math.clamp(
                math.max(secondaryBaseCount, secondaryMaxCount),
                1,
                PlayerCombatConstants.MaxAttackCircleCount),
            SecondaryTargetRange = math.max(0f, secondaryTargetRange),
            SecondaryAttackRangeMultiplier = math.max(0f, secondaryAttackRangeMultiplier),
            RandomGroundRadius = math.max(0f, randomGroundRadius),
            RepeatBaseCount = math.clamp(
                repeatBaseCount,
                1,
                PlayerCombatConstants.MaxAttackCircleCount),
            RepeatCountLevelWeight = math.max(0f, repeatCountLevelWeight),
            RepeatMaxCount = math.clamp(
                math.max(repeatBaseCount, repeatMaxCount),
                1,
                PlayerCombatConstants.MaxAttackCircleCount),
            RepeatInterval = math.max(0f, repeatInterval)
        };
    }

    /// <summary>
    /// Inspector で設定したデバフを実行時の形式に変換してコピーする。
    /// 継続時間や発生確率が 0 のデバフは効果がないため除外し、上限を超えた分も除外する。
    /// </summary>
    public static void CopyDebuffSpecs(
        SkillDebuffAuthoring[] authoringDebuffs,
        ref FixedList512Bytes<SkillDebuffSpec> debuffSpecs)
    {
        debuffSpecs = default;

        if (authoringDebuffs == null)
        {
            return;
        }

        for (var debuffIndex = 0; debuffIndex < authoringDebuffs.Length; debuffIndex++)
        {
            if (debuffSpecs.Length >= DebuffConstants.MaxActiveDebuffCount)
            {
                return;
            }

            var authoringDebuff = authoringDebuffs[debuffIndex];
            var spec = DebuffMath.NormalizeSpec(new SkillDebuffSpec
            {
                DebuffId = authoringDebuff.DebuffId,
                Kind = authoringDebuff.Kind,
                StackPolicy = authoringDebuff.StackPolicy,
                Chance = authoringDebuff.Chance,
                Duration = authoringDebuff.Duration,
                Value0 = authoringDebuff.Value0,
                Value1 = authoringDebuff.Value1,
                TickInterval = authoringDebuff.TickInterval
            });

            if (spec.Duration <= 0f || spec.Chance <= 0f)
            {
                continue;
            }

            debuffSpecs.Add(spec);
        }
    }

    /// <summary>
    /// 基本の設定だけで攻撃スキルを作る（タイミングは既定値）。
    /// </summary>
    public static AttackSkillDefinition CreateAttackSkill(
        int id,
        float baseDamage,
        float cooltime,
        float baseTargetRange,
        float baseAttackRange,
        int level,
        int logicId)
    {
        return CreateAttackSkill(
            id,
            baseDamage,
            cooltime,
            baseTargetRange,
            baseAttackRange,
            level,
            logicId,
            damageDelay: 0f,
            sfxDelay: 0f,
            sfxVolume: 1f,
            vfxDelay: 0f,
            vfxDuration: 1f,
            vfxPrefabRadius: 1f,
            vfxDisplayRadius: 0f);
    }

    public static AttackSkillDefinition CreateAttackSkill(
        int id,
        float baseDamage,
        float cooltime,
        float baseTargetRange,
        float baseAttackRange,
        int level,
        int logicId,
        float damageDelay,
        float sfxDelay,
        float sfxVolume,
        float vfxDelay,
        float vfxDuration)
    {
        // VFX の大きさは既定値を使う。
        return CreateAttackSkill(
            id,
            baseDamage,
            cooltime,
            baseTargetRange,
            baseAttackRange,
            level,
            logicId,
            damageDelay,
            sfxDelay,
            sfxVolume,
            vfxDelay,
            vfxDuration,
            vfxPrefabRadius: 1f,
            vfxDisplayRadius: 0f);
    }

    public static AttackSkillDefinition CreateAttackSkill(
        int id,
        float baseDamage,
        float cooltime,
        float baseTargetRange,
        float baseAttackRange,
        int level,
        int logicId,
        float damageDelay,
        float sfxDelay,
        float sfxVolume,
        float vfxDelay,
        float vfxDuration,
        float vfxPrefabRadius,
        float vfxDisplayRadius)
    {
        // 基本の設定に加えて、ダメージ・SFX・VFX のタイミングと VFX の大きさも指定して作る。
        var definition = SkillDefaults.CreateDefaultAttackSkill(
            id,
            math.max(0f, baseDamage),
            math.max(0f, cooltime),
            math.max(0f, baseTargetRange),
            math.max(0f, baseAttackRange),
            math.max(1, level),
            logicId);

        definition.Timing = new AttackSkillTimingConfig
        {
            DamageDelay = math.max(0f, damageDelay),
            SfxDelay = math.max(0f, sfxDelay),
            SfxVolume = math.max(0f, sfxVolume),
            VfxDelay = math.max(0f, vfxDelay),
            VfxDuration = math.max(0f, vfxDuration),
            VfxPrefabRadius = math.max(0.0001f, vfxPrefabRadius),
            VfxDisplayRadius = math.max(0f, vfxDisplayRadius),
            HasSfx = 0,
            HasVfx = 0
        };

        return definition;
    }
}
