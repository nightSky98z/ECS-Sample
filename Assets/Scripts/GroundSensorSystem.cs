using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

/// <summary>
/// SensorCollider の設定と CollisionWorld で接地状態を更新する。
/// </summary>
[UpdateAfter(typeof(MovementSystem))]
[UpdateAfter(typeof(StaticObstacleCollisionSystem))]
public partial struct GroundSensorSystem : ISystem
{
    private ComponentLookup<GroundTag> groundLookup;
    private ComponentLookup<GroundFallRescue> fallRescueLookup;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<GroundSensor>();
        state.RequireForUpdate<PhysicsWorldSingleton>();
        groundLookup = state.GetComponentLookup<GroundTag>(true);
        fallRescueLookup = state.GetComponentLookup<GroundFallRescue>(true);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        groundLookup.Update(ref state);
        fallRescueLookup.Update(ref state);

        var collisionWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().CollisionWorld;
        var collisionFilter = new CollisionFilter
        {
            BelongsTo = ~0u,
            CollidesWith = ~0u,
            GroupIndex = 0
        };
        var groundHits = new NativeList<DistanceHit>(Allocator.Temp);

        foreach (var (transform, velocity, groundSnap, groundSensor, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRW<Velocity>, RefRW<GroundSnap>, RefRO<GroundSensor>>()
                     .WithNone<MonsterDestroyVfxState>()
                     .WithEntityAccess())
        {
            var transformValue = transform.ValueRO;
            var sensorShape = GroundSensorMath.CalculateWorldShape(
                groundSensor.ValueRO,
                transformValue.Position,
                transformValue.Rotation,
                transformValue.Scale);

            if (sensorShape.QueryRadius <= 0f)
            {
                groundSnap.ValueRW.IsGrounded = 0;
                continue;
            }

            groundHits.Clear();
            collisionWorld.OverlapSphere(
                sensorShape.Center,
                sensorShape.QueryRadius,
                ref groundHits,
                collisionFilter,
                QueryInteraction.IgnoreTriggers);

            var hasGround = false;
            var bestGroundY = groundSnap.ValueRO.GroundY;
            var bestSnapResult = new GroundSnapResult
            {
                Position = transformValue.Position,
                Velocity = velocity.ValueRO.Value,
                IsGrounded = 0
            };

            for (var hitIndex = 0; hitIndex < groundHits.Length; hitIndex++)
            {
                var groundHit = groundHits[hitIndex];

                if (!groundLookup.HasComponent(groundHit.Entity))
                {
                    continue;
                }

                var snapResult = PhysicsMath.SnapToGroundFromSensor(
                    transformValue.Position,
                    velocity.ValueRO.Value,
                    sensorShape.Center.y,
                    sensorShape.Radius,
                    groundHit.Position.y,
                    groundSensor.ValueRO.Skin);

                if (snapResult.IsGrounded == 0)
                {
                    continue;
                }

                if (!hasGround || snapResult.Position.y > bestSnapResult.Position.y)
                {
                    hasGround = true;
                    bestGroundY = groundHit.Position.y;
                    bestSnapResult = snapResult;
                }
            }

            groundSnap.ValueRW.GroundY = bestGroundY;

            if (!hasGround)
            {
                if (fallRescueLookup.HasComponent(entity))
                {
                    var rescueResult = PhysicsMath.RescueFallenBelowGround(
                        transformValue.Position,
                        velocity.ValueRO.Value,
                        groundSnap.ValueRO.GroundY,
                        fallRescueLookup[entity].MaxBelowGroundY);

                    if (rescueResult.IsGrounded != 0)
                    {
                        transform.ValueRW.Position = rescueResult.Position;
                        velocity.ValueRW.Value = rescueResult.Velocity;
                        groundSnap.ValueRW.IsGrounded = rescueResult.IsGrounded;
                        continue;
                    }
                }

                groundSnap.ValueRW.IsGrounded = 0;
                continue;
            }

            transform.ValueRW.Position = bestSnapResult.Position;
            velocity.ValueRW.Value = bestSnapResult.Velocity;
            groundSnap.ValueRW.IsGrounded = bestSnapResult.IsGrounded;
        }

        groundHits.Dispose();
    }
}

/// <summary>
/// CollisionWorld の接地 query に渡す SensorCollider sphere のワールド形状。
/// </summary>
public struct GroundSensorWorldShape
{
    public float3 Center;
    public float Radius;
    public float QueryRadius;
}

/// <summary>
/// GroundSensorSystem が使う接地センサー計算。
/// </summary>
public static class GroundSensorMath
{
    /// <summary>
    /// root local の GroundSensor を CollisionWorld query 用のワールド sphere に変換する。
    /// </summary>
    /// <param name="sensor">Bake 済みの root local センサー情報。</param>
    /// <param name="position">Entity root のワールド位置。</param>
    /// <param name="rotation">Entity root のワールド回転。</param>
    /// <param name="scale">Entity root の uniform scale。</param>
    /// <returns>CollisionWorld へ渡す sphere 中心、実半径、skin 込み query 半径。</returns>
    public static GroundSensorWorldShape CalculateWorldShape(
        GroundSensor sensor,
        float3 position,
        quaternion rotation,
        float scale)
    {
        var sensorScale = math.abs(scale);
        var center = position + math.rotate(rotation, sensor.LocalCenter * scale);
        var radius = sensor.Radius * sensorScale;

        return new GroundSensorWorldShape
        {
            Center = center,
            Radius = radius,
            QueryRadius = radius + sensor.Skin
        };
    }
}
