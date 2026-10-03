using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

/// <summary>
/// 木や岩などの障害物（StaticObstacleTag）にめり込んだ Entity を、水平方向に押し戻す。
/// 経路探索で障害物を避けきれなかった場合の「最後の安全策」として動作する。
/// 押し戻しは XZ 平面だけで行い、高さ（Y）は変えない。
/// </summary>
[UpdateAfter(typeof(MovementSystem))]
[UpdateBefore(typeof(GroundSensorSystem))]
public partial struct StaticObstacleCollisionSystem : ISystem
{
    // 押し戻しを繰り返す最大回数（障害物の角などで 1 回では解消しきれない場合のため）。
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

                // 複数の障害物の角では、1 回押し戻しても別の障害物にめり込むことがあるため、数回だけ繰り返す。
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
/// 障害物からの押し戻しの計算処理。
/// </summary>
public static class StaticObstacleCollisionMath
{
    /// <summary>
    /// 判定に使う球の中心を求める（足元から半径の分だけ上に持ち上げ、地面と重ならないようにする）。
    /// </summary>
    public static float3 CalculateQueryCenter(float3 position, float radius)
    {
        return new float3(position.x, position.y + radius, position.z);
    }

    /// <summary>
    /// 障害物上の最も近い点から離れる方向へ、めり込んだ分だけ水平に押し戻す。
    /// </summary>
    /// <param name="position">押し戻す Entity の位置。Y はそのまま保つ。</param>
    /// <param name="queryCenter">判定に使った球の中心。</param>
    /// <param name="radius">当たり判定の半径。</param>
    /// <param name="hitPosition">障害物上の最も近い点。</param>
    /// <param name="hitSurfaceNormal">その点の法線。球の中心と最も近い点が水平方向で重なっている場合だけ使う。</param>
    /// <returns>障害物の外側へ押し戻した位置。</returns>
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

        // 中心が障害物の真上・真下にあって方向が決まらない場合は、面の法線の方向へ押し出す。
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
