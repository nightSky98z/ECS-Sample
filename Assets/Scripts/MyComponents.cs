using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Entity の現在速度。
/// </summary>
public struct Velocity : IComponentData
{
    // x,z は移動で処理され、y はジャンプと重力で処理される。
    public float3 Value;
}

/// <summary>
/// Entity の移動速度。
/// </summary>
public struct MoveSpeed : IComponentData
{
    public float Value;
}

/// <summary>
/// 被弾など、入力とは別に短時間だけ残る速度。
/// </summary>
public struct KnockbackVelocity : IComponentData
{
    public float3 Value;
    public float DecayPerSecond;
}

/// <summary>
/// Entity の HP。CurrentHp / MaxHp は damage、heal、level up で更新する runtime 状態。
/// </summary>
public struct HealthComponent : IComponentData
{
    public int CurrentHp;
    public int MaxHp;
}

/// <summary>
/// UI や debug 用の表示名。FixedString なので component 内に所有する。
/// </summary>
public struct EntityDisplayName : IComponentData
{
    public FixedString64Bytes Value;
}

/// <summary>
/// 頭上 HP bar の prefab 内 anchor。LocalOffset は Entity local 空間、Size は UI pixel size。
/// </summary>
public struct HealthBarAnchor : IComponentData
{
    public float3 LocalOffset;
    public float2 Size;
}

/// <summary>
/// XZ 平面で使うゲーム用の接触半径。
/// </summary>
public struct CollisionRadius : IComponentData
{
    public float Value;
}

/// <summary>
/// Velocity.y に適用する重力加速度。
/// </summary>
public struct Gravity : IComponentData
{
    public float Acceleration;
}

/// <summary>
/// Entity の接地状態と、最後に検出した地面高さ。
/// </summary>
public struct GroundSnap : IComponentData
{
    public float GroundY;

    /// <summary>
    /// 0 = airborne, 1 = grounded。
    /// </summary>
    public byte IsGrounded;
}

/// <summary>
/// 接地できる地形 Entity のタグ。
/// </summary>
public struct GroundTag : IComponentData
{

}

/// <summary>
/// XZ 平面の移動で侵入できない static obstacle のタグ。
/// </summary>
public struct StaticObstacleTag : IComponentData
{

}

/// <summary>
/// 接地判定に使う SensorCollider の SphereCollider 情報。
/// </summary>
public struct GroundSensor : IComponentData
{
    public float3 LocalCenter;
    public float Radius;
    public float Skin;
}

/// <summary>
/// XZ 平面での向き。x が world X、y が world Z。
/// </summary>
public struct FacingDirection : IComponentData
{
    public float2 Value;
}

/// <summary>
/// プレイヤーへ直線移動する最小 AI のタグ。
/// </summary>
public struct MonsterSimpleAi : IComponentData
{

}
