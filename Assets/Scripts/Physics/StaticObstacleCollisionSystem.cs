using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

/// <summary>
/// DOTS Physics の static obstacle に対して、CollisionRadius を持つ Entity を XZ 平面で押し戻す。
///
/// この System は最後の保険として障害物侵入を解消する。経路探索が失敗しても entity が木や岩に
/// めり込み続けないようにするため、押し戻しは Y を変更せず水平面だけに限定する。
/// </summary>
[UpdateAfter(typeof(MovementSystem))]
[UpdateBefore(typeof(GroundSensorSystem))]
public partial struct StaticObstacleCollisionSystem : ISystem
{
    private const int MaxResolveIterations = 3;

    private ComponentLookup<StaticObstacleTag> obstacleLookup;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PhysicsWorldSingleton>();
        state.RequireForUpdate<StaticObstacleTag>();

        obstacleLookup = state.GetComponentLookup<StaticObstacleTag>(true);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        obstacleLookup.Update(ref state);

        var collisionWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().CollisionWorld;
        var collisionFilter = new CollisionFilter
        {
            BelongsTo = ~0u,
            CollidesWith = ~0u,
            GroupIndex = 0
        };
        var obstacleHits = new NativeList<DistanceHit>(Allocator.Temp);

        foreach (var (transform, collisionRadius) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRO<CollisionRadius>>()
                     .WithNone<MonsterDestroyVfxState>())
        {
            var radius = collisionRadius.ValueRO.Value;

            if (radius <= 0f)
            {
                continue;
            }

            var resolvedPosition = transform.ValueRO.Position;
            for (var resolveIteration = 0; resolveIteration < MaxResolveIterations; resolveIteration++)
            {
                var resolvedCenter = StaticObstacleCollisionMath.CalculateQueryCenter(
                    resolvedPosition,
                    radius);
                var movedThisIteration = false;

                // 複数障害物の角では 1 回の query だけでは押し戻しが足りないため、少数回だけ反復する。
                obstacleHits.Clear();
                collisionWorld.OverlapSphere(
                    resolvedCenter,
                    radius,
                    ref obstacleHits,
                    collisionFilter,
                    QueryInteraction.IgnoreTriggers);

                for (var hitIndex = 0; hitIndex < obstacleHits.Length; hitIndex++)
                {
                    var obstacleHit = obstacleHits[hitIndex];

                    if (!obstacleLookup.HasComponent(obstacleHit.Entity))
                    {
                        continue;
                    }

                    var nextPosition = StaticObstacleCollisionMath.ResolveHorizontalPenetration(
                        resolvedPosition,
                        resolvedCenter,
                        radius,
                        obstacleHit.Position,
                        obstacleHit.SurfaceNormal);
                    var positionDelta = nextPosition - resolvedPosition;

                    if (math.lengthsq(positionDelta.xz) <= 0.000001f)
                    {
                        continue;
                    }

                    resolvedPosition = nextPosition;
                    resolvedCenter += positionDelta;
                    movedThisIteration = true;
                }

                if (!movedThisIteration)
                {
                    break;
                }
            }

            transform.ValueRW.Position = resolvedPosition;
        }

        obstacleHits.Dispose();
    }
}

/// <summary>
/// StaticObstacleCollisionSystem が使う XZ 平面の押し戻し計算。
/// </summary>
public static class StaticObstacleCollisionMath
{
    /// <summary>
    /// 足元 pivot と同じ XZ に、接触半径ぶん持ち上げた sphere query 中心を作る。
    /// </summary>
    public static float3 CalculateQueryCenter(float3 position, float radius)
    {
        return new float3(position.x, position.y + radius, position.z);
    }

    /// <summary>
    /// sphere query の hit 位置から XZ 平面だけを押し戻す。
    /// </summary>
    /// <param name="position">補正対象 Entity の root 位置。Y は補正後も維持する。</param>
    /// <param name="queryCenter">CollisionWorld に渡した sphere 中心。</param>
    /// <param name="radius">XZ 平面で使う接触半径。</param>
    /// <param name="hitPosition">static obstacle 上の最近接点。</param>
    /// <param name="hitSurfaceNormal">最近接点の法線。中心と最近接点の XZ が一致した場合だけ使う。</param>
    /// <returns>static obstacle の外側へ押し戻した root 位置。</returns>
    public static float3 ResolveHorizontalPenetration(
        float3 position,
        float3 queryCenter,
        float radius,
        float3 hitPosition,
        float3 hitSurfaceNormal)
    {
        if (radius <= 0f)
        {
            return position;
        }

        var centerToHit = new float2(
            queryCenter.x - hitPosition.x,
            queryCenter.z - hitPosition.z);
        var distanceSq = math.lengthsq(centerToHit);
        float2 normal;

        if (distanceSq <= 0.000001f)
        {
            normal = new float2(hitSurfaceNormal.x, hitSurfaceNormal.z);
            distanceSq = math.lengthsq(normal);

            if (distanceSq <= 0.000001f)
            {
                return position;
            }

            normal *= math.rsqrt(distanceSq);

            return new float3(
                position.x + normal.x * radius,
                position.y,
                position.z + normal.y * radius);
        }

        var distance = math.sqrt(distanceSq);

        if (distance >= radius)
        {
            return position;
        }

        normal = centerToHit / distance;
        var penetration = radius - distance;

        return new float3(
            position.x + normal.x * penetration,
            position.y,
            position.z + normal.y * penetration);
    }
}
