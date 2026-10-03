using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// 経路探索の設定を Inspector から行うための Authoring。
///
/// 【経路探索の仕組み：フローフィールド】
/// 1. 各セルを格子状のタイルに分け、障害物と重なるタイルを「通れない」とする（MapNavBuildSystem）
/// 2. プレイヤーのいるタイルをゴールとして、全タイルに「ゴールへ進む方向」を書き込む（FlowFieldSystem）
/// 3. モンスターは自分のいるタイルの方向を読むだけで移動できる（MonsterPathFollowSystem）
/// モンスター 1 体ごとに経路を探すと、敵の数に比例して計算が増えるが、
/// フローフィールドなら経路の計算はセルごとに 1 回で済み、何体いても全員で共有できる。
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

    /// <summary>
    /// Inspector で入力された値を、有効な範囲に収める。
    /// </summary>
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
/// 経路探索の設定（セル内のタイルの分け方）。
/// </summary>
public struct MapNavConfig : IComponentData
{
    /// <summary>1 セルあたりのタイル数（横 × 縦）。</summary>
    public int2 GridSize;

    /// <summary>タイル 1 枚の大きさ（ワールド座標の単位）。</summary>
    public float TileSize;

    /// <summary>モンスターの半径。障害物をこの分だけ大きく見なし、壁際でひっかからないようにする。</summary>
    public float AgentRadius;
}

/// <summary>
/// セルごとの経路探索用データの情報（セルの Entity に付く）。
/// </summary>
public struct MapNavCellData : IComponentData
{
    /// <summary>経路探索の設定の Entity。</summary>
    public Entity ConfigEntity;

    /// <summary>セルの座標。</summary>
    public int2 Coord;

    /// <summary>タイル数（横 × 縦）。</summary>
    public int2 GridSize;

    /// <summary>タイル 1 枚の大きさ。</summary>
    public float TileSize;

    /// <summary>モンスターの半径。</summary>
    public float AgentRadius;

    /// <summary>タイルの格子の原点（XZ が最小になる角のワールド座標）。</summary>
    public float3 Origin;

    /// <summary>データを作り直した回数。変わっていればフローフィールドも作り直す。</summary>
    public int Version;
}

/// <summary>
/// タイル 1 枚分のデータ（通れるか・ゴールまでの距離・進む方向）。
/// </summary>
[InternalBufferCapacity(0)]
public struct MapNavTile : IBufferElementData
{
    /// <summary>
    /// 1 なら通れる、0 なら障害物があって通れない。
    /// </summary>
    public byte Walkable;

    /// <summary>ゴールまでの距離（タイル数）。たどり着けない場合は UnreachableCost。</summary>
    public int Cost;

    /// <summary>このタイルからゴールへ向かうために進む方向（正規化済み）。</summary>
    public float2 Direction;
}

/// <summary>
/// フローフィールドが今どのタイルをゴールにしているか。
/// ゴールが前回と同じなら、フローフィールドを作り直さずに済む。
/// </summary>
public struct MapNavFlowState : IComponentData
{
    /// <summary>ゴールのタイル。</summary>
    public int2 GoalTile;

    /// <summary>プレイヤーがセルの外にいる場合に、ゴールのタイルから進む方向（セルの外へ出る方向）。</summary>
    public float2 GoalDirection;

    /// <summary>このフローフィールドを作ったときの MapNavCellData.Version。</summary>
    public int Version;

    /// <summary>
    /// 1 ならたどり着けるゴールがある、0 ならない（セル内がすべて通れない場合など）。
    /// </summary>
    public byte HasReachableGoal;
}

/// <summary>
/// 経路探索（タイルの格子とフローフィールド）の計算処理。
/// </summary>
public static class MapNavUtility
{
    /// <summary>ゴールにたどり着けないタイルの距離。</summary>
    public const int UnreachableCost = int.MaxValue;

    /// <summary>たどり着けるゴールがないことを表すタイル座標。</summary>
    public static readonly int2 NoReachableGoalTile = new int2(-1, -1);

    /// <summary>
    /// タイル数を 1 以上に補正する。
    /// </summary>
    public static int2 NormalizeGridSize(int2 gridSize)
    {
        return new int2(
            math.max(1, gridSize.x),
            math.max(1, gridSize.y));
    }

    /// <summary>
    /// タイルの大きさを 0.1 以上に補正する。
    /// </summary>
    public static float NormalizeTileSize(float tileSize)
    {
        return math.max(0.1f, math.abs(tileSize));
    }

    /// <summary>
    /// モンスターの半径を 0 以上に補正する。
    /// </summary>
    public static float NormalizeAgentRadius(float agentRadius)
    {
        return math.max(0f, math.abs(agentRadius));
    }

    /// <summary>
    /// セル内のタイルの総数を返す。
    /// </summary>
    public static int GetTileCount(int2 gridSize)
    {
        var safeGridSize = NormalizeGridSize(gridSize);

        return safeGridSize.x * safeGridSize.y;
    }

    /// <summary>
    /// セルの中心から、タイルの格子の原点（XZ が最小になる角）のワールド座標を求める。
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

    /// <summary>
    /// タイル座標が格子の範囲内かを返す。
    /// </summary>
    public static bool IsInsideGrid(int2 tile, int2 gridSize)
    {
        return tile.x >= 0 &&
               tile.y >= 0 &&
               tile.x < gridSize.x &&
               tile.y < gridSize.y;
    }

    /// <summary>
    /// 2 次元のタイル座標を、1 次元の配列の番号に変換する。
    /// </summary>
    public static int GetTileIndex(int2 tile, int2 gridSize)
    {
        return tile.y * gridSize.x + tile.x;
    }

    /// <summary>
    /// タイル座標を格子の範囲内に収める。
    /// </summary>
    public static int2 ClampTile(int2 tile, int2 gridSize)
    {
        return new int2(
            math.clamp(tile.x, 0, gridSize.x - 1),
            math.clamp(tile.y, 0, gridSize.y - 1));
    }

    /// <summary>
    /// ワールド座標を、その位置を含むタイル座標に変換する。
    /// </summary>
    public static int2 WorldToTile(float3 position, float3 origin, float tileSize)
    {
        var safeTileSize = NormalizeTileSize(tileSize);

        return new int2(
            (int)math.floor((position.x - origin.x) / safeTileSize),
            (int)math.floor((position.z - origin.z) / safeTileSize));
    }

    /// <summary>
    /// タイル座標を、タイルの中心のワールド座標に変換する。
    /// </summary>
    public static float3 TileToWorldCenter(float3 origin, int2 tile, float tileSize)
    {
        var safeTileSize = NormalizeTileSize(tileSize);

        return new float3(
            origin.x + (tile.x + 0.5f) * safeTileSize,
            origin.y,
            origin.z + (tile.y + 0.5f) * safeTileSize);
    }

    /// <summary>
    /// プレイヤーがセルの外にいるとき、ゴールのタイル（セルの端）からプレイヤーへ向かう方向を返す。
    /// これにより、モンスターはセルの端で止まらず、隣のセルへ進んでいける。
    /// </summary>
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
    /// タイルをモンスターの半径の分だけ広げた範囲が、障害物の AABB（境界ボックス）と重なるかを XZ 平面で判定する。
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

    /// <summary>
    /// 指定したタイルが通れなければ、最も近い通れるタイルを探す（プレイヤーが障害物に接しているときのため）。
    /// </summary>
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

    /// <summary>
    /// すべてのタイルの距離と方向をリセットする。
    /// </summary>
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
    /// ゴールへ向かうフローフィールドをセル内に作る。
    /// 1. ゴールから幅優先探索（BFS）で広げ、各タイルにゴールまでの距離を書き込む
    /// 2. 各タイルで、隣のタイルのうち最も距離が小さいものへの方向を書き込む
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

        // 幅優先探索のキュー。各タイルは最大 1 回しか入らないため、タイル数の配列で足りる。
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

            // 周囲 8 方向のタイルへ広げる。
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

    /// <summary>
    /// ワールド座標にあるタイルの「進む方向」を読み取る。
    /// 通れないタイルの上にいる場合（障害物に押し付けられたときなど）は、近くの通れるタイルの方向を使う。
    /// </summary>
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

        // ゴールのタイルに着いている場合は、方向なし。
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

    /// <summary>
    /// 通れないタイルの上にいるとき、近くの通れるタイルを何タイル先まで探すかを返す。
    /// </summary>
    public static int CalculateSampleRecoveryRadius(float agentRadius, float tileSize)
    {
        return math.max(1, (int)math.ceil(NormalizeAgentRadius(agentRadius) / NormalizeTileSize(tileSize)) + 1);
    }

    /// <summary>
    /// 指定したタイルの「進む方向」を書き換える。
    /// </summary>
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

    /// <summary>
    /// 中心のタイルから近い順（内側の輪から外側へ）に探し、使える方向を持つタイルを見つける。
    /// 同じ近さなら、ゴールに近い（距離が小さい）タイルを優先する。
    /// </summary>
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

                    // 輪の上にあるタイルだけを調べる（内側はすでに調べ済み）。
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

    /// <summary>
    /// 通れて、ゴールにたどり着けて、進む方向があるタイルかを返す。
    /// </summary>
    private static bool IsUsableFlowTile(MapNavTile navTile)
    {
        return navTile.Walkable != 0 &&
               navTile.Cost != UnreachableCost &&
               math.lengthsq(navTile.Direction) > 0.000001f;
    }

    /// <summary>
    /// 隣のタイルへ移動できて、今より短い距離で到達できる場合は、距離を更新してキューに追加する。
    /// </summary>
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

    /// <summary>
    /// 隣のタイルへ移動できるかを返す。
    /// 斜め移動は、間にある縦と横の両方のタイルが通れるときだけ許可する（障害物の角をすり抜けないようにするため）。
    /// </summary>
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

    /// <summary>
    /// すべてのタイルに、ゴールへ向かう方向を書き込む。
    /// </summary>
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

    /// <summary>
    /// 周囲 8 方向のタイルのうち、ゴールまでの距離が最も小さいタイルへの方向を返す。
    /// </summary>
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
/// セルごとに、タイルが通れるかどうか（障害物と重なっていないか）を調べて経路探索用データを作る。
/// セル内の木や岩の生成が終わってから作り、1 フレームに作るセルの数は上限で制限する。
/// </summary>
[UpdateAfter(typeof(PCGStaticMeshLocalSpawnSystem))]
[UpdateBefore(typeof(FlowFieldSystem))]
public partial struct MapNavBuildSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MapCell>();
        state.RequireForUpdate<MapNavConfig>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var pcgLocalInstanceLookup = SystemAPI.GetBufferLookup<PCGStaticMeshLocalInstance>(true);
        var navConfigLookup = SystemAPI.GetComponentLookup<MapNavConfig>(true);
        var mapCellConfigLookup = SystemAPI.GetComponentLookup<MapCellConfig>(true);
        var navTileLookup = SystemAPI.GetBufferLookup<MapNavTile>(true);
        var navCellDataLookup = SystemAPI.GetComponentLookup<MapNavCellData>(true);

        BuildMissingNavCells(
            ref state,
            ref entityCommandBuffer,
            pcgLocalInstanceLookup,
            navConfigLookup,
            mapCellConfigLookup,
            navTileLookup,
            navCellDataLookup);

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

    /// <summary>
    /// まだ経路探索用データがないセルについて、データを作る。
    /// </summary>
    private void BuildMissingNavCells(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        BufferLookup<PCGStaticMeshLocalInstance> pcgLocalInstanceLookup,
        ComponentLookup<MapNavConfig> navConfigLookup,
        ComponentLookup<MapCellConfig> mapCellConfigLookup,
        BufferLookup<MapNavTile> navTileLookup,
        ComponentLookup<MapNavCellData> navCellDataLookup)
    {
        var obstacleAabbs = new NativeList<Aabb>(Allocator.Temp);
        var builtCellCount = 0;
        var maxNavBuildCells = 1;

        CollectStaticObstacleAabbs(ref state, obstacleAabbs);

        foreach (var (cell, cellTransform, cellEntity) in
                 SystemAPI.Query<RefRW<MapCell>, RefRO<LocalTransform>>()
                     .WithNone<MapNavCellData>()
                     .WithEntityAccess())
        {
            if (ShouldDelayNavBuild(pcgLocalInstanceLookup, cellEntity, cell))
            {
                continue;
            }

            if (!navConfigLookup.HasComponent(cell.ValueRO.ConfigEntity))
            {
                continue;
            }

            var config = navConfigLookup[cell.ValueRO.ConfigEntity];
            maxNavBuildCells = MapStreamingUtility.NormalizeFrameBudget(
                mapCellConfigLookup.HasComponent(cell.ValueRO.ConfigEntity)
                    ? mapCellConfigLookup[cell.ValueRO.ConfigEntity].MaxNavBuildCellsPerFrame
                    : 1);

            if (builtCellCount >= maxNavBuildCells)
            {
                break;
            }

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
                Version = GetNextVersion(navCellDataLookup, cellEntity)
            };
            var navTiles = navTileLookup.HasBuffer(cellEntity)
                ? entityCommandBuffer.SetBuffer<MapNavTile>(cellEntity)
                : entityCommandBuffer.AddBuffer<MapNavTile>(cellEntity);

            BuildWalkableTiles(navCell, obstacleAabbs, navTiles);

            if (navCellDataLookup.HasComponent(cellEntity))
            {
                entityCommandBuffer.SetComponent(cellEntity, navCell);
            }
            else
            {
                entityCommandBuffer.AddComponent(cellEntity, navCell);
            }

            builtCellCount++;
        }

        obstacleAabbs.Dispose();
    }

    /// <summary>
    /// データ作成を後回しにするかを返す。セル内の木や岩がまだ生成されていない場合や、待ちフレーム数が残っている場合は後回しにする。
    /// </summary>
    private static bool ShouldDelayNavBuild(
        BufferLookup<PCGStaticMeshLocalInstance> pcgLocalInstanceLookup,
        Entity cellEntity,
        RefRW<MapCell> cell)
    {
        if (pcgLocalInstanceLookup.HasBuffer(cellEntity) &&
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

    /// <summary>
    /// すべての障害物の AABB（境界ボックス）を集める。
    /// </summary>
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

    /// <summary>
    /// データの次のバージョン番号を返す。
    /// </summary>
    private static int GetNextVersion(
        ComponentLookup<MapNavCellData> navCellDataLookup,
        Entity cellEntity)
    {
        if (!navCellDataLookup.HasComponent(cellEntity))
        {
            return 1;
        }

        return navCellDataLookup[cellEntity].Version + 1;
    }

    /// <summary>
    /// すべてのタイルを「通れる」で初期化し、障害物と重なるタイルを「通れない」にする。
    /// </summary>
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

    /// <summary>
    /// Collider のワールド座標での AABB を計算する。
    /// </summary>
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
/// プレイヤーの位置をゴールとして、各セルのフローフィールドを更新する。
/// ゴールのタイルとデータのバージョンが前回と同じなら作り直さず、プレイヤーが別のタイルに移ったときだけ作り直す。
/// </summary>
[UpdateAfter(typeof(MapNavBuildSystem))]
[UpdateBefore(typeof(MonsterPathFollowSystem))]
public partial struct FlowFieldSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
        state.RequireForUpdate<MapNavCellData>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (!TryGetPlayerPosition(ref state, out var playerPosition))
        {
            return;
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var flowStateLookup = SystemAPI.GetComponentLookup<MapNavFlowState>(true);

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

            // プレイヤーがセルの外にいる場合は、セルの端のタイルをゴールにする。
            // ゴールのタイルが通れなければ、最も近い通れるタイルをゴールにする。
            if (!MapNavUtility.TryFindNearestWalkableTile(
                    navTiles,
                    navCell.ValueRO.GridSize,
                    desiredGoalTile,
                    out var goalTile))
            {
                if (HasCurrentFlowState(
                        flowStateLookup,
                        cellEntity,
                        MapNavUtility.NoReachableGoalTile,
                        navCell.ValueRO.Version,
                        hasReachableGoal: 0))
                {
                    continue;
                }

                MapNavUtility.ClearFlowField(navTiles);
                SetFlowState(
                    ref entityCommandBuffer,
                    flowStateLookup,
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

            // ゴールが前回と同じなら、フローフィールドは作り直さず、ゴールのタイルの方向だけ更新する。
            if (HasCurrentFlowState(
                    flowStateLookup,
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
                    ref entityCommandBuffer,
                    flowStateLookup,
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
                ref entityCommandBuffer,
                flowStateLookup,
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

    /// <summary>
    /// 今のフローフィールドが、指定したゴールとバージョンで作られたものかを返す。
    /// </summary>
    private bool HasCurrentFlowState(
        ComponentLookup<MapNavFlowState> flowStateLookup,
        Entity cellEntity,
        int2 goalTile,
        int version,
        byte hasReachableGoal)
    {
        if (!flowStateLookup.HasComponent(cellEntity))
        {
            return false;
        }

        var flowState = flowStateLookup[cellEntity];

        return flowState.Version == version &&
               flowState.HasReachableGoal == hasReachableGoal &&
               math.all(flowState.GoalTile == goalTile);
    }

    /// <summary>
    /// フローフィールドのゴールとバージョンを記録する。
    /// </summary>
    private static void SetFlowState(
        ref EntityCommandBuffer entityCommandBuffer,
        ComponentLookup<MapNavFlowState> flowStateLookup,
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

        if (flowStateLookup.HasComponent(cellEntity))
        {
            entityCommandBuffer.SetComponent(cellEntity, flowState);
            return;
        }

        entityCommandBuffer.AddComponent(cellEntity, flowState);
    }
}

/// <summary>
/// フローフィールドの方向を読み取り、モンスターの水平方向の速度を決める。
/// MonsterSimpleAiSystem の後に実行され、経路探索用データがある間は、まっすぐ進む AI が決めた速度をこの結果で上書きする。
/// </summary>
[UpdateAfter(typeof(FlowFieldSystem))]
[UpdateAfter(typeof(MonsterSimpleAiSystem))]
[UpdateBefore(typeof(PhysicsSystem))]
public partial struct MonsterPathFollowSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterTag>();
        state.RequireForUpdate<MapNavCellData>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (!StageRuntimeUtility.IsGameplayPhase(runtimeState.ValueRO.Phase))
            {
                return;
            }
        }

        foreach (var (velocity, transform, moveSpeed, debuffs) in
                 SystemAPI.Query<RefRW<Velocity>, RefRO<LocalTransform>, RefRO<MoveSpeed>, RefRO<DebuffAggregate>>()
                     .WithAll<MonsterTag>()
                     .WithNone<MonsterDestroyVfxState>())
        {
            var currentVelocity = velocity.ValueRO.Value;

            // 方向が取れない（データがない場所・ゴールに到着した）場合は止まる。
            if (!TrySampleFlowDirection(ref state, transform.ValueRO.Position, out var direction))
            {
                velocity.ValueRW.Value = new float3(0f, currentVelocity.y, 0f);
                continue;
            }

            var moveSpeedMultiplier = debuffs.ValueRO.MoveSpeedMultiplier;

            velocity.ValueRW.Value = new float3(
                direction.x * moveSpeed.ValueRO.Value * moveSpeedMultiplier,
                currentVelocity.y,
                direction.y * moveSpeed.ValueRO.Value * moveSpeedMultiplier);
        }
    }

    /// <summary>
    /// モンスターがいるセルを探し、そのセルのフローフィールドから進む方向を読み取る。
    /// </summary>
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
