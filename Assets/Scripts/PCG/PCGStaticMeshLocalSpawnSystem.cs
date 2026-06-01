using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

/// <summary>
/// Map Cell instance に baked local PCG 配置を一度だけ展開する。
///
/// PCG の random 配置は Baker 側で prefab-local な buffer に固定される。
/// runtime では cell root transform に合わせて instantiate するだけにし、毎 frame の random 生成を避ける。
/// </summary>
[UpdateAfter(typeof(MapCellSystem))]
[UpdateBefore(typeof(StaticObstacleCollisionSystem))]
public partial struct PCGStaticMeshLocalSpawnSystem : ISystem
{
    private EntityQuery physicsColliderQuery;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MapCell>();
        state.RequireForUpdate<PCGStaticMeshLocalInstance>();

        physicsColliderQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<PhysicsCollider>()
            .Build(ref state);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var physicsColliderMask = physicsColliderQuery.GetEntityQueryMask();
        var mapCellConfigLookup = SystemAPI.GetComponentLookup<MapCellConfig>(true);
        var hasPlayer = TryGetPlayerPosition(ref state, out var playerPosition);
        var hasCameraBounds = TryGetCameraBounds(ref state, out var cameraBounds);

        SpawnCenterCellStaticMeshes(
            ref state,
            ref entityCommandBuffer,
            physicsColliderMask,
            mapCellConfigLookup,
            hasPlayer,
            playerPosition,
            hasCameraBounds);
        SpawnNonCenterCellStaticMeshes(
            ref state,
            ref entityCommandBuffer,
            physicsColliderMask,
            hasPlayer,
            playerPosition,
            hasCameraBounds,
            cameraBounds);

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

    private bool TryGetPlayerPosition(ref SystemState state, out float3 playerPosition)
    {
        foreach (var transform in
                 SystemAPI.Query<RefRO<LocalTransform>>()
                     .WithAll<PlayerTag>())
        {
            playerPosition = transform.ValueRO.Position;
            return true;
        }

        playerPosition = float3.zero;
        return false;
    }

    private bool TryGetCameraBounds(ref SystemState state, out MonsterSpawnCameraBounds cameraBounds)
    {
        foreach (var bounds in
                 SystemAPI.Query<RefRO<MonsterSpawnCameraBounds>>())
        {
            cameraBounds = bounds.ValueRO;
            return cameraBounds.IsValid != 0;
        }

        cameraBounds = default;
        return false;
    }

    private void SpawnCenterCellStaticMeshes(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        EntityQueryMask physicsColliderMask,
        ComponentLookup<MapCellConfig> mapCellConfigLookup,
        bool hasPlayer,
        float3 playerPosition,
        bool hasCameraBounds)
    {
        foreach (var (cell, cellTransform, localInstances, cellEntity) in
                 SystemAPI.Query<RefRW<MapCell>, RefRO<LocalTransform>, DynamicBuffer<PCGStaticMeshLocalInstance>>()
                     .WithNone<MapCellUnloadState>()
                     .WithEntityAccess())
        {
            if (cell.ValueRO.LocalStaticMeshSpawned != 0 ||
                !IsPlayerCenterCell(mapCellConfigLookup, cell.ValueRO, hasPlayer, playerPosition))
            {
                continue;
            }

            var config = mapCellConfigLookup[cell.ValueRO.ConfigEntity];
            var spawnBudget = hasCameraBounds
                ? MapStreamingUtility.NormalizeFrameBudget(config.MaxVisibleStaticMeshSpawnsPerFrame)
                : MapStreamingUtility.NormalizeFrameBudget(config.MaxStaticMeshSpawnsPerFrame);

            SpawnCellStaticMeshes(
                ref entityCommandBuffer,
                physicsColliderMask,
                cell,
                cellTransform.ValueRO,
                localInstances,
                cellEntity,
                spawnBudget);
        }
    }

    private void SpawnNonCenterCellStaticMeshes(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        EntityQueryMask physicsColliderMask,
        bool hasPlayer,
        float3 playerPosition,
        bool hasCameraBounds,
        MonsterSpawnCameraBounds cameraBounds)
    {
        foreach (var (config, configEntity) in
                 SystemAPI.Query<RefRO<MapCellConfig>>()
                     .WithEntityAccess())
        {
            var centerCoord = hasPlayer
                ? MapCellUtility.CalculateCellCoord(playerPosition, config.ValueRO.CellSize)
                : int2.zero;
            var visibleSpawnBudget = hasCameraBounds
                ? MapStreamingUtility.NormalizeFrameBudget(config.ValueRO.MaxVisibleStaticMeshSpawnsPerFrame)
                : 0;
            var backgroundSpawnBudget = MapStreamingUtility.NormalizeFrameBudget(
                config.ValueRO.MaxStaticMeshSpawnsPerFrame);

            if (visibleSpawnBudget > 0)
            {
                SpawnNonCenterCellStaticMeshesForConfig(
                    ref state,
                    ref entityCommandBuffer,
                    physicsColliderMask,
                    configEntity,
                    config.ValueRO,
                    centerCoord,
                    hasPlayer,
                    playerPosition,
                    hasCameraBounds,
                    cameraBounds,
                    spawnVisibleCells: true,
                    maxSpawnCount: visibleSpawnBudget);
            }

            SpawnNonCenterCellStaticMeshesForConfig(
                ref state,
                ref entityCommandBuffer,
                physicsColliderMask,
                configEntity,
                config.ValueRO,
                centerCoord,
                hasPlayer,
                playerPosition,
                hasCameraBounds,
                cameraBounds,
                spawnVisibleCells: false,
                maxSpawnCount: backgroundSpawnBudget);
        }
    }

    private int SpawnNonCenterCellStaticMeshesForConfig(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        EntityQueryMask physicsColliderMask,
        Entity configEntity,
        MapCellConfig config,
        int2 centerCoord,
        bool hasPlayer,
        float3 playerPosition,
        bool hasCameraBounds,
        MonsterSpawnCameraBounds cameraBounds,
        bool spawnVisibleCells,
        int maxSpawnCount)
    {
        var remainingSpawnBudget = MapStreamingUtility.NormalizeFrameBudget(maxSpawnCount);

        foreach (var (cell, cellTransform, localInstances, cellEntity) in
                 SystemAPI.Query<RefRW<MapCell>, RefRO<LocalTransform>, DynamicBuffer<PCGStaticMeshLocalInstance>>()
                     .WithNone<MapCellUnloadState>()
                     .WithEntityAccess())
        {
            if (cell.ValueRO.ConfigEntity != configEntity ||
                cell.ValueRO.LocalStaticMeshSpawned != 0 ||
                (hasPlayer && math.all(cell.ValueRO.Coord == centerCoord)))
            {
                continue;
            }

            var isVisiblePriorityCell = IsVisiblePriorityCell(
                cell.ValueRO,
                config,
                hasPlayer,
                playerPosition,
                hasCameraBounds,
                cameraBounds);

            if (spawnVisibleCells != isVisiblePriorityCell)
            {
                continue;
            }

            if (remainingSpawnBudget <= 0)
            {
                break;
            }

            remainingSpawnBudget -= SpawnCellStaticMeshes(
                ref entityCommandBuffer,
                physicsColliderMask,
                cell,
                cellTransform.ValueRO,
                localInstances,
                cellEntity,
                remainingSpawnBudget);
        }

        return maxSpawnCount - remainingSpawnBudget;
    }

    private static bool IsVisiblePriorityCell(
        MapCell cell,
        MapCellConfig config,
        bool hasPlayer,
        float3 playerPosition,
        bool hasCameraBounds,
        MonsterSpawnCameraBounds cameraBounds)
    {
        if (!hasPlayer ||
            !hasCameraBounds ||
            cameraBounds.IsValid == 0 ||
            cameraBounds.VisibleGroundRadius <= 0f)
        {
            return false;
        }

        return MapStreamingUtility.ShouldPrioritizeStaticMeshSpawn(
            playerPosition.xz,
            cell.Coord,
            config.CellSize,
            cameraBounds.VisibleGroundRadius,
            config.StaticMeshVisiblePadding);
    }

    private static int SpawnCellStaticMeshes(
        ref EntityCommandBuffer entityCommandBuffer,
        EntityQueryMask physicsColliderMask,
        RefRW<MapCell> cell,
        LocalTransform cellTransform,
        DynamicBuffer<PCGStaticMeshLocalInstance> localInstances,
        Entity cellEntity,
        int maxSpawnCount)
    {
        var instanceIndex = cell.ValueRO.LocalStaticMeshSpawnCursor;
        var spawnedCount = 0;

        while (instanceIndex < localInstances.Length && spawnedCount < maxSpawnCount)
        {
            var localInstance = localInstances[instanceIndex];
            instanceIndex++;

            if (localInstance.Prefab == Entity.Null)
            {
                continue;
            }

            var objectEntity = entityCommandBuffer.Instantiate(localInstance.Prefab);

            entityCommandBuffer.SetComponent(
                objectEntity,
                PCGStaticMeshUtility.CreateWorldPlacementTransform(
                    cellTransform,
                    localInstance));
            // LinkedEntityGroup 内の collider entity に StaticObstacleTag を付け、障害物 query の対象にする。
            entityCommandBuffer.AddComponentForLinkedEntityGroup(
                objectEntity,
                physicsColliderMask,
                new StaticObstacleTag());
            entityCommandBuffer.AppendToBuffer(cellEntity, new MapCellOwnedEntityElement
            {
                Value = objectEntity
            });
            spawnedCount++;
        }

        cell.ValueRW.LocalStaticMeshSpawnCursor = instanceIndex;

        if (instanceIndex >= localInstances.Length)
        {
            cell.ValueRW.LocalStaticMeshSpawned = 1;
            cell.ValueRW.NavBuildDelayFrames = 1;
        }

        return spawnedCount;
    }

    private static bool IsPlayerCenterCell(
        ComponentLookup<MapCellConfig> mapCellConfigLookup,
        MapCell cell,
        bool hasPlayer,
        float3 playerPosition)
    {
        if (!hasPlayer ||
            cell.ConfigEntity == Entity.Null ||
            !mapCellConfigLookup.HasComponent(cell.ConfigEntity))
        {
            return false;
        }

        var config = mapCellConfigLookup[cell.ConfigEntity];
        var centerCoord = MapCellUtility.CalculateCellCoord(playerPosition, config.CellSize);

        return math.all(cell.Coord == centerCoord);
    }

}
