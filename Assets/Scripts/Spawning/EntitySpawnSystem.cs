using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

public partial struct EntitySpawnSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<SpawnRequest>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var requests in SystemAPI.Query<DynamicBuffer<SpawnRequest>>())
        {
            for (var requestIndex = 0; requestIndex < requests.Length; requestIndex++)
            {
                var request = requests[requestIndex];
                var spawnedEntity = entityCommandBuffer.Instantiate(request.Prefab);

                entityCommandBuffer.SetComponent(spawnedEntity, request.Transform);
            }

            requests.Clear();
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}
