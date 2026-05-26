using Unity.Collections;
using Unity.Entities;
using Unity.Physics;
using Unity.Transforms;

/// <summary>
/// Map Cell instance に baked local PCG 配置を一度だけ展開する。
/// </summary>
[UpdateAfter(typeof(MapCellSystem))]
[UpdateBefore(typeof(StaticObstacleCollisionSystem))]
public partial struct PCGStaticMeshLocalSpawnSystem : ISystem
{
    private EntityQuery physicsColliderQuery;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MapCell>();
        state.RequireForUpdate<PCGStaticMeshLocalInstance>();

        physicsColliderQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<PhysicsCollider>()
            .Build(ref state);
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var physicsColliderMask = physicsColliderQuery.GetEntityQueryMask();

        foreach (var (cellTransform, localInstances, cellEntity) in
                 SystemAPI.Query<RefRO<LocalTransform>, DynamicBuffer<PCGStaticMeshLocalInstance>>()
                     .WithAll<MapCell>()
                     .WithNone<PCGStaticMeshLocalSpawned>()
                     .WithEntityAccess())
        {
            for (var instanceIndex = 0; instanceIndex < localInstances.Length; instanceIndex++)
            {
                var localInstance = localInstances[instanceIndex];

                if (localInstance.Prefab == Entity.Null)
                {
                    continue;
                }

                var objectEntity = entityCommandBuffer.Instantiate(localInstance.Prefab);

                entityCommandBuffer.SetComponent(
                    objectEntity,
                    PCGStaticMeshUtility.CreateWorldPlacementTransform(
                        cellTransform.ValueRO,
                        localInstance));
                entityCommandBuffer.AddComponent(objectEntity, new MapCellOwnedEntity
                {
                    CellEntity = cellEntity
                });
                entityCommandBuffer.AddComponentForLinkedEntityGroup(
                    objectEntity,
                    physicsColliderMask,
                    new StaticObstacleTag());
            }

            entityCommandBuffer.AddComponent<PCGStaticMeshLocalSpawned>(cellEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}
