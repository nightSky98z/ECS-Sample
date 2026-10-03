using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

/// <summary>
/// 生成要求（SpawnRequest）を処理して Entity を生成する汎用の System。
/// 生成したい側は要求を Buffer に追加するだけでよく、生成の処理はこの System にまとめている。
/// 処理した要求は削除するため、同じ要求で 2 回生成されることはない。
/// </summary>
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
        // ループ中に Entity を生成すると Query が壊れるため、EntityCommandBuffer に積んで最後にまとめて反映する。
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
