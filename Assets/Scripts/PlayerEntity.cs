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

    [Tooltip("Velocity.y に加える重力加速度。")]
    [SerializeField]
    private float GravityAcceleration = -9.81f;

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
        }
    }
}
