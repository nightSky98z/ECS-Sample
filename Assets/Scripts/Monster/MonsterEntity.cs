using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// モンスターを表すタグ。データは持たず、Query でモンスターを見分けるためだけに使う。
/// </summary>
public struct MonsterTag : IComponentData
{

}

/// <summary>
/// 倒されたあとに再利用（Recycle）できる、通常のモンスターを表すタグ。
/// 大量に出現する通常モンスターは Destroy せずに使い回し、生成・破棄の負荷を減らす。
/// ボスやエリートなどの特殊な敵にはこのタグを付けず、死亡演出のあとに削除する。
/// </summary>
public struct MonsterRecycleTag : IComponentData
{

}

/// <summary>
/// モンスターの Prefab（GameObject）を ECS の Entity に変換する Authoring。
/// Inspector の設定値は Bake 時に Component にコピーされる。
/// ゲーム中の HP・デバフ・演出の状態は、MonsterDestroySystem・DebuffSystem・VFX 関連の System が更新する。
/// </summary>
public class MonsterEntity : MonoBehaviour
{
    [Tooltip("XZ 平面のゲーム用接触半径。プレイヤーを押し戻す境界として使う。")]
    [SerializeField]
    private float CollisionRadius = 0.5f;

    [Tooltip("プレイヤーへ向かう移動速度。")]
    [SerializeField]
    private float MoveSpeed = 2.5f;

    [Tooltip("モンスターの最大 HP。初期 HP も同じ値になる。")]
    [SerializeField]
    private int MaxHp = 30;

    [Tooltip("死亡時にプレイヤーが得る経験値。")]
    [SerializeField]
    private int ExperienceRewardValue = 10;

    [Header("Lifecycle")]
    [Tooltip("通常モンスターだけ有効にする。ボス、エリート、特殊敵は無効にして死亡後に削除する。")]
    [SerializeField]
    private bool RecycleAfterDeath = false;

    [Tooltip("Velocity.y に加える重力加速度。")]
    [SerializeField]
    private float GravityAcceleration = -9.81f;

    [Tooltip("低 FPS ですり抜けた時、最後に記録した地面高さより何 m 下で救済するか。0 は無効。")]
    [SerializeField]
    private float FallRescueDepth = 3f;

    [Header("Destroy VFX")]
    [Tooltip("死亡 VFX の再生時間秒。この時間後に通常モンスターは再利用され、特殊敵は削除される。")]
    [SerializeField]
    [Min(0.01f)]
    private float DestroyVfxDuration = 0.45f;

    [Tooltip("死亡 VFX 開始時に適用する色。")]
    [SerializeField]
    private Color DestroyVfxStartColor = new Color(1f, 0.2f, 0.05f, 1f);

    [Tooltip("死亡 VFX 終了時に近づける色。アルファ 0 なら透明化する。")]
    [SerializeField]
    private Color DestroyVfxEndColor = new Color(0.05f, 0.05f, 0.05f, 0f);

    [Tooltip("死亡 VFX 終了時のスケール倍率。0 に近いほど小さく消える。")]
    [SerializeField]
    [Min(0f)]
    private float DestroyVfxEndScale = 0.05f;

    [Header("Hit VFX")]
    [Tooltip("被弾 VFX の再生時間秒。短いほど一瞬だけ点滅する。")]
    [SerializeField]
    [Min(0.01f)]
    private float HitVfxDuration = 0.12f;

    [Tooltip("被弾 VFX 中に一時的に適用する色。")]
    [SerializeField]
    private Color HitVfxColor = Color.white;

    /// <summary>
    /// Inspector で入力された値を、有効な範囲に収める。
    /// </summary>
    private void OnValidate()
    {
        ExperienceRewardValue = ExperienceMath.NormalizeReward(ExperienceRewardValue);
        DestroyVfxDuration = MonsterDestroyVfxMath.NormalizeDuration(DestroyVfxDuration);
        DestroyVfxEndScale = math.max(0f, DestroyVfxEndScale);
        HitVfxDuration = MonsterHitVfxMath.NormalizeDuration(HitVfxDuration);
        FallRescueDepth = math.max(0f, FallRescueDepth);
    }

    private class Baker : Unity.Entities.Baker<MonsterEntity>
    {
        /// <summary>
        /// モンスターの Prefab を Entity に変換し、AI・HP・デバフ・接地・演出に必要な Component を追加する。
        /// </summary>
        public override void Bake(MonsterEntity authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent<MonsterTag>(entity);
            AddComponent(entity, new EntityDisplayName
            {
                Value = new FixedString64Bytes(authoring.name)
            });

            if (authoring.RecycleAfterDeath)
            {
                AddComponent<MonsterRecycleTag>(entity);
            }

            AddComponent<MonsterSimpleAi>(entity);
            AddComponent(entity, new Velocity
            {
                Value = float3.zero
            });
            AddComponent(entity, new MoveSpeed
            {
                Value = authoring.MoveSpeed
            });
            AddComponent(entity, new DebuffRuntimeState
            {
                ActiveDebuffs = default
            });
            // デバフの集約値は常に持たせておく。デバフがないときも倍率 1 として読めるので、AI 側で条件分岐が不要になる。
            AddComponent(entity, DebuffMath.CreateNeutralAggregate());
            AddComponent(entity, HealthMath.CreateFullHealth(authoring.MaxHp));
            AddComponent(entity, new ExperienceReward
            {
                Value = ExperienceMath.NormalizeReward(authoring.ExperienceRewardValue)
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
            AddComponent(entity, new MonsterDestroyVfxConfig
            {
                Duration = MonsterDestroyVfxMath.NormalizeDuration(authoring.DestroyVfxDuration),
                StartBaseColor = MonsterEntityBakingUtility.ConvertColorToLinearFloat4(authoring.DestroyVfxStartColor),
                EndBaseColor = MonsterEntityBakingUtility.ConvertColorToLinearFloat4(authoring.DestroyVfxEndColor),
                EndScale = math.max(0f, authoring.DestroyVfxEndScale)
            });
            AddComponent(entity, new MonsterHitVfxConfig
            {
                Duration = MonsterHitVfxMath.NormalizeDuration(authoring.HitVfxDuration),
                HitBaseColor = MonsterEntityBakingUtility.ConvertColorToLinearFloat4(authoring.HitVfxColor),
                RestBaseColor = MonsterEntityBakingUtility.GetFirstRendererBaseColor(authoring)
            });
            AddComponent(entity, new MonsterHitVfxState
            {
                ElapsedTime = 0f,
                IsPlaying = 0
            });

            if (GroundSensorAuthoringUtility.TryCreateGroundSensor(authoring, out var groundSensor))
            {
                AddComponent(entity, groundSensor);
            }

            if (HealthBarAnchorAuthoringUtility.TryCreateHealthBarAnchor(authoring.transform, out var healthBarAnchor))
            {
                AddComponent(entity, healthBarAnchor);
            }
        }
    }
}

/// <summary>
/// モンスターの見た目（Renderer）に、色を Entity ごとに変えられる Component（URPMaterialPropertyBaseColor）を追加する Baker。
/// これにより、マテリアルを複製せずに被弾時・死亡時の色の変化を表現できる。
/// </summary>
public sealed class MonsterBaseColorOverrideBaker : Unity.Entities.Baker<Renderer>
{
    public override void Bake(Renderer authoring)
    {
        if (authoring.GetComponentInParent<MonsterEntity>(true) == null)
        {
            return;
        }

        var entity = GetEntity(TransformUsageFlags.Renderable);

        AddComponent(entity, new Unity.Rendering.URPMaterialPropertyBaseColor
        {
            Value = MonsterEntityBakingUtility.GetRendererBaseColor(authoring)
        });
    }
}

/// <summary>
/// モンスターの Bake 時に使う色の変換処理。
/// マテリアルへの参照は Component に保存せず、Bake の時点で必要な色だけを float4 として取り出す。
/// </summary>
public static class MonsterEntityBakingUtility
{
    /// <summary>
    /// Color を、Entities Graphics で使うリニア色空間の float4 に変換する。
    /// </summary>
    public static float4 ConvertColorToLinearFloat4(Color color)
    {
        var linearColor = color.linear;

        return new float4(
            linearColor.r,
            linearColor.g,
            linearColor.b,
            linearColor.a);
    }

    /// <summary>
    /// モンスターの最初の Renderer から元の色を取得する（被弾演出のあと、この色に戻すため）。
    /// </summary>
    public static float4 GetFirstRendererBaseColor(MonsterEntity authoring)
    {
        var meshRenderer = authoring.GetComponentInChildren<Renderer>(true);

        if (meshRenderer == null)
        {
            return new float4(1f, 1f, 1f, 1f);
        }

        return GetRendererBaseColor(meshRenderer);
    }

    /// <summary>
    /// Renderer のマテリアルから基本色を取得する（URP の _BaseColor、なければ _Color を使う）。
    /// </summary>
    public static float4 GetRendererBaseColor(Renderer renderer)
    {
        if (renderer.sharedMaterial == null)
        {
            return new float4(1f, 1f, 1f, 1f);
        }

        var material = renderer.sharedMaterial;

        if (material.HasProperty("_BaseColor"))
        {
            return ConvertColorToLinearFloat4(material.GetColor("_BaseColor"));
        }

        if (material.HasProperty("_Color"))
        {
            return ConvertColorToLinearFloat4(material.GetColor("_Color"));
        }

        return new float4(1f, 1f, 1f, 1f);
    }
}
