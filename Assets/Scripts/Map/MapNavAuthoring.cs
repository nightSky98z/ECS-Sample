using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// Map Cell prefab 周辺の navigation grid 設定を ECS に渡す Authoring。
/// </summary>
public sealed class MapNavAuthoring : MonoBehaviour
{
    [SerializeField]
    [Tooltip("1 つのマップセル内のタイル数。タイルサイズ 2 で 50 x 50 なら 100 x 100 のセルを覆う。")]
    private Vector2Int GridSize = new Vector2Int(50, 50);

    [SerializeField]
    [Min(0.1f)]
    [Tooltip("ナビゲーションタイル 1 枚のワールドサイズ。")]
    private float TileSize = 2f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("障害物 AABB を通行不可タイルへラスタライズするときに使う水平半径。")]
    private float AgentRadius = 0.5f;

    private void OnValidate()
    {
        GridSize = new Vector2Int(
            math.max(1, GridSize.x),
            math.max(1, GridSize.y));
        TileSize = MapNavUtility.NormalizeTileSize(TileSize);
        AgentRadius = MapNavUtility.NormalizeAgentRadius(AgentRadius);
    }

    private sealed class Baker : Baker<MapNavAuthoring>
    {
        public override void Bake(MapNavAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent(entity, new MapNavConfig
            {
                GridSize = MapNavUtility.NormalizeGridSize(new int2(
                    authoring.GridSize.x,
                    authoring.GridSize.y)),
                TileSize = MapNavUtility.NormalizeTileSize(authoring.TileSize),
                AgentRadius = MapNavUtility.NormalizeAgentRadius(authoring.AgentRadius)
            });
        }
    }
}

/// <summary>
/// Map Cell 上に作る navigation grid の設定。
/// </summary>
public struct MapNavConfig : IComponentData
{
    public int2 GridSize;
    public float TileSize;
    public float AgentRadius;
}

/// <summary>
/// Runtime に存在する Map Cell ごとの navigation grid 境界。
/// </summary>
public struct MapNavCellData : IComponentData
{
    public Entity ConfigEntity;
    public int2 Coord;
    public int2 GridSize;
    public float TileSize;
    public float AgentRadius;
    public float3 Origin;
    public int Version;
}

/// <summary>
/// Map Cell 内の 1 tile に対応する walkable / flow field データ。
/// </summary>
[InternalBufferCapacity(0)]
public struct MapNavTile : IBufferElementData
{
    /// <summary>
    /// 0 = blocked, 1 = walkable。
    /// </summary>
    public byte Walkable;
    public int Cost;
    public float2 Direction;
}

/// <summary>
/// Flow field がどの player tile を goal としているかを表す。
/// </summary>
public struct MapNavFlowState : IComponentData
{
    public int2 GoalTile;
    public float2 GoalDirection;
    public int Version;

    /// <summary>
    /// 0 = reachable goal なし, 1 = reachable goal あり。
    /// </summary>
    public byte HasReachableGoal;
}

/// <summary>
/// Map Cell navigation grid と flow field の計算。
/// </summary>
public static class MapNavUtility
{
    public const int UnreachableCost = int.MaxValue;
    public static readonly int2 NoReachableGoalTile = new int2(-1, -1);

    public static int2 NormalizeGridSize(int2 gridSize)
    {
        return new int2(
            math.max(1, gridSize.x),
            math.max(1, gridSize.y));
    }

    public static float NormalizeTileSize(float tileSize)
    {
        return math.max(0.1f, math.abs(tileSize));
    }

    public static float NormalizeAgentRadius(float agentRadius)
    {
        return math.max(0f, math.abs(agentRadius));
    }

    public static int GetTileCount(int2 gridSize)
    {
        var safeGridSize = NormalizeGridSize(gridSize);

        return safeGridSize.x * safeGridSize.y;
    }

    /// <summary>
    /// Cell 中心から、grid の最小 XZ 側 world 位置を返す。
    /// </summary>
    public static float3 CalculateCellOrigin(float3 cellCenter, int2 gridSize, float tileSize)
    {
        var safeGridSize = NormalizeGridSize(gridSize);
        var safeTileSize = NormalizeTileSize(tileSize);
        var gridWorldSize = new float2(
            safeGridSize.x * safeTileSize,
            safeGridSize.y * safeTileSize);

        return new float3(
            cellCenter.x - gridWorldSize.x * 0.5f,
            cellCenter.y,
            cellCenter.z - gridWorldSize.y * 0.5f);
    }

    public static bool IsInsideGrid(int2 tile, int2 gridSize)
    {
        return tile.x >= 0 &&
               tile.y >= 0 &&
               tile.x < gridSize.x &&
               tile.y < gridSize.y;
    }

    public static int GetTileIndex(int2 tile, int2 gridSize)
    {
        return tile.y * gridSize.x + tile.x;
    }

    public static int2 ClampTile(int2 tile, int2 gridSize)
    {
        return new int2(
            math.clamp(tile.x, 0, gridSize.x - 1),
            math.clamp(tile.y, 0, gridSize.y - 1));
    }

    public static int2 WorldToTile(float3 position, float3 origin, float tileSize)
    {
        var safeTileSize = NormalizeTileSize(tileSize);

        return new int2(
            (int)math.floor((position.x - origin.x) / safeTileSize),
            (int)math.floor((position.z - origin.z) / safeTileSize));
    }

    public static float3 TileToWorldCenter(float3 origin, int2 tile, float tileSize)
    {
        var safeTileSize = NormalizeTileSize(tileSize);

        return new float3(
            origin.x + (tile.x + 0.5f) * safeTileSize,
            origin.y,
            origin.z + (tile.y + 0.5f) * safeTileSize);
    }

    public static float2 CalculateGoalExitDirection(
        float3 targetPosition,
        float3 origin,
        int2 goalTile,
        float tileSize)
    {
        var goalCenter = TileToWorldCenter(origin, goalTile, tileSize);
        var toTarget = new float2(
            targetPosition.x - goalCenter.x,
            targetPosition.z - goalCenter.z);
        var distanceSq = math.lengthsq(toTarget);

        if (distanceSq <= 0.000001f)
        {
            return float2.zero;
        }

        return toTarget * math.rsqrt(distanceSq);
    }

    /// <summary>
    /// agent 半径ぶん広げた tile が static obstacle AABB と XZ 平面で重なるかを返す。
    /// </summary>
    public static bool DoesTileOverlapAabb(
        float3 tileCenter,
        float tileSize,
        float agentRadius,
        Aabb obstacleAabb)
    {
        var halfExtent = NormalizeTileSize(tileSize) * 0.5f + NormalizeAgentRadius(agentRadius);
        var minX = tileCenter.x - halfExtent;
        var maxX = tileCenter.x + halfExtent;
        var minZ = tileCenter.z - halfExtent;
        var maxZ = tileCenter.z + halfExtent;

        return minX <= obstacleAabb.Max.x &&
               maxX >= obstacleAabb.Min.x &&
               minZ <= obstacleAabb.Max.z &&
               maxZ >= obstacleAabb.Min.z;
    }

    public static bool TryFindNearestWalkableTile(
        DynamicBuffer<MapNavTile> tiles,
        int2 gridSize,
        int2 desiredTile,
        out int2 walkableTile)
    {
        var safeDesiredTile = ClampTile(desiredTile, gridSize);
        var desiredIndex = GetTileIndex(safeDesiredTile, gridSize);

        if (tiles[desiredIndex].Walkable != 0)
        {
            walkableTile = safeDesiredTile;
            return true;
        }

        var bestDistanceSq = int.MaxValue;
        var bestTile = int2.zero;
        var foundTile = false;

        for (var tileY = 0; tileY < gridSize.y; tileY++)
        {
            for (var tileX = 0; tileX < gridSize.x; tileX++)
            {
                var tile = new int2(tileX, tileY);
                var tileIndex = GetTileIndex(tile, gridSize);

                if (tiles[tileIndex].Walkable == 0)
                {
                    continue;
                }

                var delta = tile - safeDesiredTile;
                var distanceSq = delta.x * delta.x + delta.y * delta.y;

                if (distanceSq >= bestDistanceSq)
                {
                    continue;
                }

                bestDistanceSq = distanceSq;
                bestTile = tile;
                foundTile = true;
            }
        }

        walkableTile = bestTile;
        return foundTile;
    }

    public static void ClearFlowField(DynamicBuffer<MapNavTile> tiles)
    {
        for (var tileIndex = 0; tileIndex < tiles.Length; tileIndex++)
        {
            var tile = tiles[tileIndex];

            tile.Cost = UnreachableCost;
            tile.Direction = float2.zero;
            tiles[tileIndex] = tile;
        }
    }

    /// <summary>
    /// walkable tile だけを使って、goal へ向かう cell-local flow field を作る。
    /// </summary>
    public static void RebuildFlowField(
        DynamicBuffer<MapNavTile> tiles,
        int2 gridSize,
        int2 goalTile)
    {
        ClearFlowField(tiles);

        if (!IsInsideGrid(goalTile, gridSize))
        {
            return;
        }

        var goalIndex = GetTileIndex(goalTile, gridSize);

        if (tiles[goalIndex].Walkable == 0)
        {
            return;
        }

        var queue = new NativeArray<int>(tiles.Length, Allocator.Temp);
        var queueHead = 0;
        var queueTail = 0;
        var goal = tiles[goalIndex];

        goal.Cost = 0;
        tiles[goalIndex] = goal;
        queue[queueTail] = goalIndex;
        queueTail++;

        while (queueHead < queueTail)
        {
            var currentIndex = queue[queueHead];
            queueHead++;

            var currentTile = new int2(
                currentIndex % gridSize.x,
                currentIndex / gridSize.x);
            var currentCost = tiles[currentIndex].Cost;

            for (var deltaY = -1; deltaY <= 1; deltaY++)
            {
                for (var deltaX = -1; deltaX <= 1; deltaX++)
                {
                    if (deltaX == 0 && deltaY == 0)
                    {
                        continue;
                    }

                    AddReachableNeighbor(
                        tiles,
                        gridSize,
                        currentTile,
                        new int2(deltaX, deltaY),
                        currentCost + 1,
                        queue,
                        ref queueTail);
                }
            }
        }

        queue.Dispose();
        WriteFlowDirections(tiles, gridSize);
    }

    public static bool TrySampleDirection(
        DynamicBuffer<MapNavTile> tiles,
        MapNavCellData navCell,
        float3 worldPosition,
        out float2 direction)
    {
        var tile = WorldToTile(
            worldPosition,
            navCell.Origin,
            navCell.TileSize);

        if (!IsInsideGrid(tile, navCell.GridSize))
        {
            direction = float2.zero;
            return false;
        }

        var tileIndex = GetTileIndex(tile, navCell.GridSize);
        var navTile = tiles[tileIndex];

        if (IsUsableFlowTile(navTile))
        {
            direction = navTile.Direction;
            return true;
        }

        if (navTile.Walkable != 0 && navTile.Cost == 0)
        {
            direction = float2.zero;
            return false;
        }

        return TrySampleNearbyDirection(
            tiles,
            navCell.GridSize,
            tile,
            CalculateSampleRecoveryRadius(navCell.AgentRadius, navCell.TileSize),
            out direction);
    }

    public static int CalculateSampleRecoveryRadius(float agentRadius, float tileSize)
    {
        return math.max(1, (int)math.ceil(NormalizeAgentRadius(agentRadius) / NormalizeTileSize(tileSize)) + 1);
    }

    public static void SetTileDirection(
        DynamicBuffer<MapNavTile> tiles,
        int2 gridSize,
        int2 tile,
        float2 direction)
    {
        if (!IsInsideGrid(tile, gridSize))
        {
            return;
        }

        var tileIndex = GetTileIndex(tile, gridSize);
        var navTile = tiles[tileIndex];

        navTile.Direction = direction;
        tiles[tileIndex] = navTile;
    }

    private static bool TrySampleNearbyDirection(
        DynamicBuffer<MapNavTile> tiles,
        int2 gridSize,
        int2 centerTile,
        int maxRadius,
        out float2 direction)
    {
        for (var radius = 1; radius <= maxRadius; radius++)
        {
            var bestDirection = float2.zero;
            var bestDistanceSq = int.MaxValue;
            var bestCost = int.MaxValue;
            var foundDirection = false;

            for (var tileY = centerTile.y - radius; tileY <= centerTile.y + radius; tileY++)
            {
                for (var tileX = centerTile.x - radius; tileX <= centerTile.x + radius; tileX++)
                {
                    var delta = new int2(tileX - centerTile.x, tileY - centerTile.y);

                    if (math.max(math.abs(delta.x), math.abs(delta.y)) != radius)
                    {
                        continue;
                    }

                    var tile = new int2(tileX, tileY);

                    if (!IsInsideGrid(tile, gridSize))
                    {
                        continue;
                    }

                    var navTile = tiles[GetTileIndex(tile, gridSize)];

                    if (!IsUsableFlowTile(navTile))
                    {
                        continue;
                    }

                    var distanceSq = delta.x * delta.x + delta.y * delta.y;

                    if (distanceSq > bestDistanceSq ||
                        (distanceSq == bestDistanceSq && navTile.Cost >= bestCost))
                    {
                        continue;
                    }

                    bestDistanceSq = distanceSq;
                    bestCost = navTile.Cost;
                    bestDirection = navTile.Direction;
                    foundDirection = true;
                }
            }

            if (!foundDirection)
            {
                continue;
            }

            direction = bestDirection;
            return true;
        }

        direction = float2.zero;
        return false;
    }

    private static bool IsUsableFlowTile(MapNavTile navTile)
    {
        return navTile.Walkable != 0 &&
               navTile.Cost != UnreachableCost &&
               math.lengthsq(navTile.Direction) > 0.000001f;
    }

    private static void AddReachableNeighbor(
        DynamicBuffer<MapNavTile> tiles,
        int2 gridSize,
        int2 currentTile,
        int2 delta,
        int neighborCost,
        NativeArray<int> queue,
        ref int queueTail)
    {
        if (!CanMoveBetweenTiles(tiles, gridSize, currentTile, delta))
        {
            return;
        }

        var neighborTile = currentTile + delta;
        var neighborIndex = GetTileIndex(neighborTile, gridSize);
        var neighbor = tiles[neighborIndex];

        if (neighbor.Walkable == 0 || neighbor.Cost <= neighborCost)
        {
            return;
        }

        neighbor.Cost = neighborCost;
        tiles[neighborIndex] = neighbor;
        queue[queueTail] = neighborIndex;
        queueTail++;
    }

    private static bool CanMoveBetweenTiles(
        DynamicBuffer<MapNavTile> tiles,
        int2 gridSize,
        int2 currentTile,
        int2 delta)
    {
        var neighborTile = currentTile + delta;

        if (!IsInsideGrid(neighborTile, gridSize))
        {
            return false;
        }

        if (tiles[GetTileIndex(neighborTile, gridSize)].Walkable == 0)
        {
            return false;
        }

        if (delta.x == 0 || delta.y == 0)
        {
            return true;
        }

        var horizontalTile = currentTile + new int2(delta.x, 0);
        var verticalTile = currentTile + new int2(0, delta.y);

        if (!IsInsideGrid(horizontalTile, gridSize) ||
            !IsInsideGrid(verticalTile, gridSize))
        {
            return false;
        }

        return tiles[GetTileIndex(horizontalTile, gridSize)].Walkable != 0 &&
               tiles[GetTileIndex(verticalTile, gridSize)].Walkable != 0;
    }

    private static void WriteFlowDirections(
        DynamicBuffer<MapNavTile> tiles,
        int2 gridSize)
    {
        for (var tileY = 0; tileY < gridSize.y; tileY++)
        {
            for (var tileX = 0; tileX < gridSize.x; tileX++)
            {
                var tile = new int2(tileX, tileY);
                var tileIndex = GetTileIndex(tile, gridSize);
                var navTile = tiles[tileIndex];

                if (navTile.Walkable == 0 || navTile.Cost == UnreachableCost)
                {
                    navTile.Direction = float2.zero;
                    tiles[tileIndex] = navTile;
                    continue;
                }

                navTile.Direction = CalculateDirectionToLowestCostNeighbor(
                    tiles,
                    gridSize,
                    tile,
                    navTile.Cost);
                tiles[tileIndex] = navTile;
            }
        }
    }

    public static float2 CalculateDirectionToLowestCostNeighbor(
        DynamicBuffer<MapNavTile> tiles,
        int2 gridSize,
        int2 tile,
        int currentCost)
    {
        var bestCost = currentCost;
        var bestDelta = int2.zero;

        for (var deltaY = -1; deltaY <= 1; deltaY++)
        {
            for (var deltaX = -1; deltaX <= 1; deltaX++)
            {
                if (deltaX == 0 && deltaY == 0)
                {
                    continue;
                }

                var delta = new int2(deltaX, deltaY);
                var neighborTile = tile + delta;

                if (!CanMoveBetweenTiles(tiles, gridSize, tile, delta))
                {
                    continue;
                }

                var neighbor = tiles[GetTileIndex(neighborTile, gridSize)];

                if (neighbor.Walkable == 0 ||
                    neighbor.Cost >= bestCost)
                {
                    continue;
                }

                bestCost = neighbor.Cost;
                bestDelta = new int2(deltaX, deltaY);
            }
        }

        if (math.all(bestDelta == int2.zero))
        {
            return float2.zero;
        }

        var direction = new float2(bestDelta.x, bestDelta.y);

        return direction * math.rsqrt(math.lengthsq(direction));
    }
}

/// <summary>
/// Map Cell ごとの walkable grid を作る。
/// </summary>
[UpdateAfter(typeof(PCGStaticMeshLocalSpawnSystem))]
[UpdateBefore(typeof(FlowFieldSystem))]
public partial struct MapNavBuildSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MapCell>();
        state.RequireForUpdate<MapNavConfig>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        BuildMissingNavCells(ref state, ref entityCommandBuffer);

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

    private void BuildMissingNavCells(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer)
    {
        var obstacleAabbs = new NativeList<Aabb>(Allocator.Temp);

        CollectStaticObstacleAabbs(ref state, obstacleAabbs);

        foreach (var (cell, cellTransform, cellEntity) in
                 SystemAPI.Query<RefRW<MapCell>, RefRO<LocalTransform>>()
                     .WithNone<MapNavCellData>()
                     .WithEntityAccess())
        {
            if (ShouldDelayNavBuild(ref state, cellEntity, cell))
            {
                continue;
            }

            if (!state.EntityManager.HasComponent<MapNavConfig>(cell.ValueRO.ConfigEntity))
            {
                continue;
            }

            var config = state.EntityManager.GetComponentData<MapNavConfig>(cell.ValueRO.ConfigEntity);
            var gridSize = MapNavUtility.NormalizeGridSize(config.GridSize);
            var tileSize = MapNavUtility.NormalizeTileSize(config.TileSize);
            var agentRadius = MapNavUtility.NormalizeAgentRadius(config.AgentRadius);
            var navCell = new MapNavCellData
            {
                ConfigEntity = cell.ValueRO.ConfigEntity,
                Coord = cell.ValueRO.Coord,
                GridSize = gridSize,
                TileSize = tileSize,
                AgentRadius = agentRadius,
                Origin = MapNavUtility.CalculateCellOrigin(
                    cellTransform.ValueRO.Position,
                    gridSize,
                    tileSize),
                Version = GetNextVersion(ref state, cellEntity)
            };
            var navTiles = state.EntityManager.HasBuffer<MapNavTile>(cellEntity)
                ? entityCommandBuffer.SetBuffer<MapNavTile>(cellEntity)
                : entityCommandBuffer.AddBuffer<MapNavTile>(cellEntity);

            BuildWalkableTiles(navCell, obstacleAabbs, navTiles);

            if (state.EntityManager.HasComponent<MapNavCellData>(cellEntity))
            {
                entityCommandBuffer.SetComponent(cellEntity, navCell);
            }
            else
            {
                entityCommandBuffer.AddComponent(cellEntity, navCell);
            }
        }

        obstacleAabbs.Dispose();
    }

    private static bool ShouldDelayNavBuild(
        ref SystemState state,
        Entity cellEntity,
        RefRW<MapCell> cell)
    {
        if (state.EntityManager.HasBuffer<PCGStaticMeshLocalInstance>(cellEntity) &&
            cell.ValueRO.LocalStaticMeshSpawned == 0)
        {
            return true;
        }

        if (cell.ValueRO.NavBuildDelayFrames == 0)
        {
            return false;
        }

        cell.ValueRW.NavBuildDelayFrames--;
        return true;
    }

    private void CollectStaticObstacleAabbs(
        ref SystemState state,
        NativeList<Aabb> obstacleAabbs)
    {
        foreach (var (collider, localToWorld) in
                 SystemAPI.Query<RefRO<PhysicsCollider>, RefRO<LocalToWorld>>()
                     .WithAll<StaticObstacleTag>())
        {
            if (!collider.ValueRO.Value.IsCreated)
            {
                continue;
            }

            obstacleAabbs.Add(CalculateWorldAabb(
                collider.ValueRO.Value,
                localToWorld.ValueRO.Value));
        }
    }

    private static int GetNextVersion(ref SystemState state, Entity cellEntity)
    {
        if (!state.EntityManager.HasComponent<MapNavCellData>(cellEntity))
        {
            return 1;
        }

        return state.EntityManager.GetComponentData<MapNavCellData>(cellEntity).Version + 1;
    }

    private static void BuildWalkableTiles(
        MapNavCellData navCell,
        NativeList<Aabb> obstacleAabbs,
        DynamicBuffer<MapNavTile> navTiles)
    {
        var tileCount = MapNavUtility.GetTileCount(navCell.GridSize);

        navTiles.Clear();

        for (var tileIndex = 0; tileIndex < tileCount; tileIndex++)
        {
            navTiles.Add(new MapNavTile
            {
                Walkable = 1,
                Cost = MapNavUtility.UnreachableCost,
                Direction = float2.zero
            });
        }

        for (var tileY = 0; tileY < navCell.GridSize.y; tileY++)
        {
            for (var tileX = 0; tileX < navCell.GridSize.x; tileX++)
            {
                var tile = new int2(tileX, tileY);
                var tileIndex = MapNavUtility.GetTileIndex(tile, navCell.GridSize);
                var tileCenter = MapNavUtility.TileToWorldCenter(
                    navCell.Origin,
                    tile,
                    navCell.TileSize);
                var isWalkable = true;

                for (var obstacleIndex = 0; obstacleIndex < obstacleAabbs.Length; obstacleIndex++)
                {
                    if (!MapNavUtility.DoesTileOverlapAabb(
                            tileCenter,
                            navCell.TileSize,
                            navCell.AgentRadius,
                            obstacleAabbs[obstacleIndex]))
                    {
                        continue;
                    }

                    isWalkable = false;
                    break;
                }

                if (isWalkable)
                {
                    continue;
                }

                var navTile = navTiles[tileIndex];

                navTile.Walkable = 0;
                navTiles[tileIndex] = navTile;
            }
        }
    }

    private static Aabb CalculateWorldAabb(
        BlobAssetReference<Unity.Physics.Collider> collider,
        float4x4 localToWorld)
    {
        var position = localToWorld.c3.xyz;
        var right = localToWorld.c0.xyz;
        var up = localToWorld.c1.xyz;
        var forward = localToWorld.c2.xyz;
        var scale = math.max(
            math.length(right),
            math.max(math.length(up), math.length(forward)));

        if (scale <= 0.0001f)
        {
            scale = 1f;
        }

        var rotation = quaternion.LookRotationSafe(
            math.normalizesafe(forward, new float3(0f, 0f, 1f)),
            math.normalizesafe(up, new float3(0f, 1f, 0f)));

        return collider.Value.CalculateAabb(
            new RigidTransform(rotation, position),
            scale);
    }
}

/// <summary>
/// Player 位置を goal として、各 Map Cell の flow field を更新する。
/// </summary>
[UpdateAfter(typeof(MapNavBuildSystem))]
[UpdateBefore(typeof(MonsterPathFollowSystem))]
public partial struct FlowFieldSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
        state.RequireForUpdate<MapNavCellData>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!TryGetPlayerPosition(ref state, out var playerPosition))
        {
            return;
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (navCell, navTiles, cellEntity) in
                 SystemAPI.Query<RefRO<MapNavCellData>, DynamicBuffer<MapNavTile>>()
                     .WithEntityAccess())
        {
            var rawGoalTile = MapNavUtility.WorldToTile(
                playerPosition,
                navCell.ValueRO.Origin,
                navCell.ValueRO.TileSize);
            var desiredGoalTile = MapNavUtility.ClampTile(
                rawGoalTile,
                navCell.ValueRO.GridSize);

            if (!MapNavUtility.TryFindNearestWalkableTile(
                    navTiles,
                    navCell.ValueRO.GridSize,
                    desiredGoalTile,
                    out var goalTile))
            {
                if (HasCurrentFlowState(
                        ref state,
                        cellEntity,
                        MapNavUtility.NoReachableGoalTile,
                        navCell.ValueRO.Version,
                        hasReachableGoal: 0))
                {
                    continue;
                }

                MapNavUtility.ClearFlowField(navTiles);
                SetFlowState(
                    ref state,
                    ref entityCommandBuffer,
                    cellEntity,
                    MapNavUtility.NoReachableGoalTile,
                    float2.zero,
                    navCell.ValueRO.Version,
                    hasReachableGoal: 0);
                continue;
            }

            var goalDirection = MapNavUtility.IsInsideGrid(rawGoalTile, navCell.ValueRO.GridSize)
                ? float2.zero
                : MapNavUtility.CalculateGoalExitDirection(
                    playerPosition,
                    navCell.ValueRO.Origin,
                    goalTile,
                    navCell.ValueRO.TileSize);

            if (HasCurrentFlowState(
                    ref state,
                    cellEntity,
                    goalTile,
                    navCell.ValueRO.Version,
                    hasReachableGoal: 1))
            {
                MapNavUtility.SetTileDirection(
                    navTiles,
                    navCell.ValueRO.GridSize,
                    goalTile,
                    goalDirection);
                SetFlowState(
                    ref state,
                    ref entityCommandBuffer,
                    cellEntity,
                    goalTile,
                    goalDirection,
                    navCell.ValueRO.Version,
                    hasReachableGoal: 1);
                continue;
            }

            MapNavUtility.RebuildFlowField(
                navTiles,
                navCell.ValueRO.GridSize,
                goalTile);
            MapNavUtility.SetTileDirection(
                navTiles,
                navCell.ValueRO.GridSize,
                goalTile,
                goalDirection);
            SetFlowState(
                ref state,
                ref entityCommandBuffer,
                cellEntity,
                goalTile,
                goalDirection,
                navCell.ValueRO.Version,
                hasReachableGoal: 1);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

    private bool TryGetPlayerPosition(ref SystemState state, out float3 playerPosition)
    {
        foreach (var playerTransform in
                 SystemAPI.Query<RefRO<LocalTransform>>()
                     .WithAll<PlayerTag>())
        {
            playerPosition = playerTransform.ValueRO.Position;
            return true;
        }

        playerPosition = float3.zero;
        return false;
    }

    private bool HasCurrentFlowState(
        ref SystemState state,
        Entity cellEntity,
        int2 goalTile,
        int version,
        byte hasReachableGoal)
    {
        if (!state.EntityManager.HasComponent<MapNavFlowState>(cellEntity))
        {
            return false;
        }

        var flowState = state.EntityManager.GetComponentData<MapNavFlowState>(cellEntity);

        return flowState.Version == version &&
               flowState.HasReachableGoal == hasReachableGoal &&
               math.all(flowState.GoalTile == goalTile);
    }

    private static void SetFlowState(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity cellEntity,
        int2 goalTile,
        float2 goalDirection,
        int version,
        byte hasReachableGoal)
    {
        var flowState = new MapNavFlowState
        {
            GoalTile = goalTile,
            GoalDirection = goalDirection,
            Version = version,
            HasReachableGoal = hasReachableGoal
        };

        if (state.EntityManager.HasComponent<MapNavFlowState>(cellEntity))
        {
            entityCommandBuffer.SetComponent(cellEntity, flowState);
            return;
        }

        entityCommandBuffer.AddComponent(cellEntity, flowState);
    }
}

/// <summary>
/// Flow field を読んで、モンスターの XZ 速度を決める。
/// </summary>
[UpdateAfter(typeof(FlowFieldSystem))]
[UpdateAfter(typeof(MonsterSimpleAiSystem))]
[UpdateBefore(typeof(PhysicsSystem))]
public partial struct MonsterPathFollowSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterTag>();
        state.RequireForUpdate<MapNavCellData>();
    }

    public void OnUpdate(ref SystemState state)
    {
        foreach (var (velocity, transform, moveSpeed, debuffs) in
                 SystemAPI.Query<RefRW<Velocity>, RefRO<LocalTransform>, RefRO<MoveSpeed>, RefRO<DebuffAggregate>>()
                     .WithAll<MonsterTag>()
                     .WithNone<MonsterDestroyVfxState>())
        {
            var currentVelocity = velocity.ValueRO.Value;

            if (!TrySampleFlowDirection(ref state, transform.ValueRO.Position, out var direction))
            {
                velocity.ValueRW.Value = new float3(0f, currentVelocity.y, 0f);
                continue;
            }

            var moveSpeedMultiplier = debuffs.ValueRO.IsMovementLocked != 0
                ? 0f
                : debuffs.ValueRO.MoveSpeedMultiplier;

            velocity.ValueRW.Value = new float3(
                direction.x * moveSpeed.ValueRO.Value * moveSpeedMultiplier,
                currentVelocity.y,
                direction.y * moveSpeed.ValueRO.Value * moveSpeedMultiplier);
        }
    }

    private bool TrySampleFlowDirection(
        ref SystemState state,
        float3 worldPosition,
        out float2 direction)
    {
        foreach (var (navCell, navTiles) in
                 SystemAPI.Query<RefRO<MapNavCellData>, DynamicBuffer<MapNavTile>>())
        {
            if (MapNavUtility.TrySampleDirection(
                    navTiles,
                    navCell.ValueRO,
                    worldPosition,
                    out direction))
            {
                return true;
            }
        }

        direction = float2.zero;
        return false;
    }
}
