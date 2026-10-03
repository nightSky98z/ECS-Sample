using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

/// <summary>
/// 足元の球（GroundSensor）と Unity Physics の CollisionWorld を使って接地を判定し、地面の高さに合わせる。
/// 地面が見つからず、大きく落下していた場合は、最後に記録した地面の高さへ戻す。
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
        // 結果を入れるリストは Entity ごとに作らず、使い回してメモリ確保を減らす。
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

            // 足元の球と重なっている Collider を取得する。
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

            // 地面（GroundTag）の中から、最も高い位置に立てるものを選ぶ。
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
                // 地面が見つからない場合は、すり抜けて落ちていないかを確認する。
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
/// 接地判定に使う球の、ワールド座標での形。
/// </summary>
public struct GroundSensorWorldShape
{
    /// <summary>球の中心（ワールド座標）。</summary>
    public float3 Center;

    /// <summary>球の半径（スケール反映済み）。</summary>
    public float Radius;

    /// <summary>判定に使う半径（Radius に余裕の幅 Skin を足したもの）。</summary>
    public float QueryRadius;
}

/// <summary>
/// 接地判定用の球の計算処理。
/// </summary>
public static class GroundSensorMath
{
    /// <summary>
    /// Entity のローカル座標で定義された球を、ワールド座標の球に変換する。
    /// </summary>
    /// <param name="sensor">Bake 済みの球の情報（ローカル座標）。</param>
    /// <param name="position">Entity のワールド位置。</param>
    /// <param name="rotation">Entity のワールド回転。</param>
    /// <param name="scale">Entity のスケール（全軸共通）。</param>
    /// <returns>球の中心・半径・判定に使う半径。</returns>
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
