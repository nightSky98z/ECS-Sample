using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

public partial struct EntitySpawnSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SpawnRequest>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (request, requestEntity) in
                SystemAPI.Query<RefRO<SpawnRequest>>()
                    .WithEntityAccess())
        {
            var spawnedEntity = entityCommandBuffer.Instantiate(request.ValueRO.Prefab);

            entityCommandBuffer.SetComponent(spawnedEntity, request.ValueRO.Transform);

            entityCommandBuffer.DestroyEntity(requestEntity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}
