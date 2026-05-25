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

    [Tooltip("Velocity.y に加える重力加速度。")]
    [SerializeField]
    private float GravityAcceleration = -9.81f;

    private class Baker : Unity.Entities.Baker<MonsterEntity>
    {
        public override void Bake(MonsterEntity authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent<MonsterTag>(entity);
            AddComponent<MonsterSimpleAi>(entity);
            AddComponent(entity, new Velocity
            {
                Value = float3.zero
            });
            AddComponent(entity, new MoveSpeed
            {
                Value = authoring.MoveSpeed
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
