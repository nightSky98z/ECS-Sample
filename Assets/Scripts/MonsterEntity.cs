using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// モンスターを見つけるためのタグ、データなし。
/// </summary>
public struct MonsterTag : IComponentData
{

}

/// <summary>
/// 通常 monster として recycle 対象にできることを示すタグ。
/// </summary>
public struct MonsterRecycleTag : IComponentData
{

}

/// <summary>
/// GameObject の Monster Prefab を ECS の Monster Entity に変換する Authoring。
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

    [Header("Lifecycle")]
    [Tooltip("通常モンスターだけ ON。Boss / Elite / 特殊敵は OFF にして死亡後に削除する。")]
    [SerializeField]
    private bool RecycleAfterDeath = false;

    [Tooltip("Velocity.y に加える重力加速度。")]
    [SerializeField]
    private float GravityAcceleration = -9.81f;

    [Header("Destroy VFX")]
    [SerializeField]
    [Min(0.01f)]
    private float DestroyVfxDuration = 0.45f;

    [SerializeField]
    private Color DestroyVfxStartColor = new Color(1f, 0.2f, 0.05f, 1f);

    [SerializeField]
    private Color DestroyVfxEndColor = new Color(0.05f, 0.05f, 0.05f, 0f);

    [SerializeField]
    [Min(0f)]
    private float DestroyVfxEndScale = 0.05f;

    [Header("Hit VFX")]
    [SerializeField]
    [Min(0.01f)]
    private float HitVfxDuration = 0.12f;

    [SerializeField]
    private Color HitVfxColor = Color.white;

    private void OnValidate()
    {
        DestroyVfxDuration = MonsterDestroyVfxMath.NormalizeDuration(DestroyVfxDuration);
        DestroyVfxEndScale = math.max(0f, DestroyVfxEndScale);
        HitVfxDuration = MonsterHitVfxMath.NormalizeDuration(HitVfxDuration);
    }

    private class Baker : Unity.Entities.Baker<MonsterEntity>
    {
        public override void Bake(MonsterEntity authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent<MonsterTag>(entity);

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
/// Monster prefab 配下の renderer Entity に、material VFX 用の初期色を焼く Baker。
/// </summary>
public sealed class MonsterMaterialBaseColorBaker : Unity.Entities.Baker<Renderer>
{
    public override void Bake(Renderer authoring)
    {
        if (authoring.GetComponentInParent<MonsterEntity>(true) == null)
        {
            return;
        }

        var entity = GetEntity(TransformUsageFlags.Renderable);

        AddComponent(entity, new MonsterMaterialBaseColor
        {
            Value = MonsterEntityBakingUtility.GetRendererBaseColor(authoring)
        });
    }
}

/// <summary>
/// Monster baking が共有する authoring-time の色変換処理。
/// </summary>
public static class MonsterEntityBakingUtility
{
    public static float4 ConvertColorToLinearFloat4(Color color)
    {
        var linearColor = color.linear;

        return new float4(
            linearColor.r,
            linearColor.g,
            linearColor.b,
            linearColor.a);
    }

    public static float4 GetFirstRendererBaseColor(MonsterEntity authoring)
    {
        var meshRenderer = authoring.GetComponentInChildren<Renderer>(true);

        if (meshRenderer == null)
        {
            return new float4(1f, 1f, 1f, 1f);
        }

        return GetRendererBaseColor(meshRenderer);
    }

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
