using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

/// <summary>
/// セルが生成されたら、Bake 時に計算しておいた配置をもとに、木や岩などの静的メッシュを生成する。
/// ・配置の計算は Bake 時に済んでいるため、ゲーム中は生成するだけで済む
/// ・1 フレームに生成する数には上限を設け、処理落ちを防ぐ（続きは次のフレームで生成する）
/// ・生成の優先順位：プレイヤーがいるセル → 画面に映るセル → その他のセル
/// </summary>
[UpdateAfter(typeof(MapCellSystem))]
[UpdateBefore(typeof(StaticObstacleCollisionSystem))]
public partial struct PCGStaticMeshLocalSpawnSystem : ISystem
{
    // Collider を持つ Entity を見分けるための Query（障害物タグを付ける対象を選ぶのに使う）。
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

        // 1. プレイヤーがいるセルを最優先で生成する
        SpawnCenterCellStaticMeshes(
            ref state,
            ref entityCommandBuffer,
            physicsColliderMask,
            mapCellConfigLookup,
            hasPlayer,
            playerPosition,
            hasCameraBounds);
        // 2. それ以外のセルを、画面に映るセル → その他のセルの順で生成する
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

    /// <summary>
    /// プレイヤーがいるセルの静的メッシュを生成する。
    /// </summary>
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

    /// <summary>
    /// プレイヤーがいるセル以外の静的メッシュを、画面に映るセルを優先して生成する。
    /// 画面に映るセルとその他のセルで、1 フレームの生成数の上限を別々に持つ。
    /// </summary>
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

    /// <summary>
    /// 画面に映るセル（spawnVisibleCells が true）、またはその他のセルの静的メッシュを、上限の数まで生成する。
    /// </summary>
    /// <returns>生成した数。</returns>
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

    /// <summary>
    /// セルが画面に映る可能性があり、優先して生成すべきかを返す。
    /// </summary>
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

    /// <summary>
    /// セル 1 つ分の静的メッシュを、前回の続きから上限の数まで生成する。
    /// すべて生成し終えたら、セルを「生成済み」にし、経路探索用データの作成を 1 フレーム後に始めるよう設定する。
    /// </summary>
    /// <returns>生成した数。</returns>
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
            // Prefab の中で Collider を持つ Entity に障害物タグを付け、当たり判定と経路探索の対象にする。
            entityCommandBuffer.AddComponentForLinkedEntityGroup(
                objectEntity,
                physicsColliderMask,
                new StaticObstacleTag());
            // セルが削除されるときに一緒に削除できるよう、セルの持ち物として登録する。
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

    /// <summary>
    /// セルがプレイヤーのいるセルかを返す。
    /// </summary>
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
