using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// Player 周辺に維持する Map Cell prefab の設定。
/// </summary>
public struct MapCellConfig : IComponentData
{
    /// <summary>
    /// 座標ごとの prefab 選択を決定する seed。
    /// </summary>
    public int WorldSeed;

    /// <summary>
    /// XZ 平面上の cell 一辺の長さ。
    /// </summary>
    public float CellSize;

    /// <summary>
    /// player がいる cell から Chebyshev 距離で維持する半径。
    /// </summary>
    public int LoadRadiusInCells;

    /// <summary>
    /// gameplay が依存してよい完成済み cell 半径。
    /// </summary>
    public int ActiveRadiusInCells;

    /// <summary>
    /// gameplay 中に先回りして生成する cell 半径。
    /// </summary>
    public int PreloadRadiusInCells;

    /// <summary>
    /// この半径より外側の cell を削除候補にする。
    /// </summary>
    public int UnloadRadiusInCells;

    /// <summary>
    /// 1 frame で作成する cell root 数の上限。
    /// </summary>
    public int MaxCellCreatesPerFrame;

    /// <summary>
    /// 1 frame で削除する cell owned entity root 数の上限。
    /// </summary>
    public int MaxOwnedEntityDestroysPerFrame;

    /// <summary>
    /// 1 frame で生成する cell 内 static mesh 数の上限。
    /// </summary>
    public int MaxStaticMeshSpawnsPerFrame;

    /// <summary>
    /// 画面に入る可能性がある cell で、1 frame に生成する static mesh 数の上限。
    /// </summary>
    public int MaxVisibleStaticMeshSpawnsPerFrame;

    /// <summary>
    /// Camera の地面表示範囲に足す world 単位の余白。
    /// </summary>
    public float StaticMeshVisiblePadding;

    /// <summary>
    /// 1 frame で navigation build する cell 数の上限。
    /// </summary>
    public int MaxNavBuildCellsPerFrame;

    /// <summary>
    /// 0 = preload では後方 cell を省略できる, 1 = preload 半径内を全方向生成する。
    /// </summary>
    public byte PreloadBehindCells;

    /// <summary>
    /// 後方省略時、移動方向との dot がこの値以上なら preload 対象にする。
    /// </summary>
    public float ForwardPreloadDotThreshold;

    /// <summary>
    /// cell root を置く world Y。
    /// </summary>
    public float GroundY;
}

/// <summary>
/// Runtime で選択する Map Cell prefab 候補。
/// </summary>
[InternalBufferCapacity(8)]
public struct MapCellPrefabElement : IBufferElementData
{
    /// <summary>
    /// Instantiate する cell prefab entity。
    /// </summary>
    public Entity Prefab;

    /// <summary>
    /// 相対的な抽選重み。0 以下は無効。
    /// </summary>
    public float Weight;
}

/// <summary>
/// Runtime に存在する Map Cell root。
/// </summary>
public struct MapCell : IComponentData
{
    /// <summary>
    /// この cell を作った MapCellConfig entity。
    /// </summary>
    public Entity ConfigEntity;

    /// <summary>
    /// 無限マップ上の整数 cell 座標。
    /// </summary>
    public int2 Coord;

    /// <summary>
    /// 0 = local static mesh 未生成, 1 = 生成済み。
    /// </summary>
    public byte LocalStaticMeshSpawned;

    /// <summary>
    /// Nav build 開始まで待つ frame 数。これは flag ではない。
    /// </summary>
    public byte NavBuildDelayFrames;

    /// <summary>
    /// 0 = cell 初期 monster 未生成, 1 = 生成済み。
    /// </summary>
    public byte MonstersSpawned;

    /// <summary>
    /// Cell 内 PCG static mesh の次に展開する buffer index。
    /// </summary>
    public int LocalStaticMeshSpawnCursor;
}

/// <summary>
/// Map Cell root が所有する runtime entity root。
/// </summary>
[InternalBufferCapacity(64)]
public struct MapCellOwnedEntityElement : IBufferElementData
{
    /// <summary>
    /// Cell unload 時に cell と同じライフタイムで破棄する entity root。
    /// </summary>
    public Entity Value;
}

/// <summary>
/// Cell unload 中の root に付ける状態。
///
/// Owned entity を frame budget で少しずつ破棄し、全部消えた後で cell root を破棄する。
/// </summary>
public struct MapCellUnloadState : IComponentData
{
    /// <summary>
    /// 0 = unload 予約済み。将来、段階的な unload phase が必要になった場合に使う。
    /// </summary>
    public byte Phase;

    /// <summary>
    /// 次に破棄する MapCellOwnedEntityElement の index。
    /// 所有 entity の破棄を frame budget で分割し、全体走査を避ける。
    /// </summary>
    public int NextOwnedEntityIndex;
}

/// <summary>
/// Player 周辺に Cell prefab を生成する Authoring。
///
/// PCG は cell prefab 内にローカル設計として持たせる。この Authoring は cell の active set と
/// prefab 選択だけを ECS に渡す。
/// </summary>
public sealed class MapCellAuthoring : MonoBehaviour
{
    [SerializeField]
    [Tooltip("プレイヤー周辺に生成するセルプレハブ候補。重みが 0 以下の要素は無視される。")]
    private MapCellPrefabEntry[] CellPrefabs = new MapCellPrefabEntry[0];

    [SerializeField]
    [HideInInspector]
    [Tooltip("旧形式の単一セルプレハブ。CellPrefabs が空の場合、重み 1 の候補として扱う。")]
    private GameObject CellPrefab;

    [SerializeField]
    [Tooltip("座標ごとに決定的なセルプレハブを選ぶためのワールドシード。")]
    private int RandomSeed = 1;

    [SerializeField]
    [Min(0.1f)]
    [Tooltip("XZ 平面上のセル一辺の長さ。スケール 10 の Unity Plane なら 100 を使う。")]
    private float CellSize = 100f;

    [SerializeField]
    [HideInInspector]
    [Min(0)]
    [Tooltip("旧形式のロード半径。現在は PreloadRadiusInCells から自動設定する。")]
    private int LoadRadiusInCells = 1;

    [SerializeField]
    [Min(0)]
    [Tooltip("ゲームロジックが依存してよい完成済みセル半径。現在は中心セルと周囲 8 セルの 3x3 固定。")]
    private int ActiveRadiusInCells = 1;

    [SerializeField]
    [Min(0)]
    [Tooltip("プレイヤー到達前に先回りして作るセル半径。現在は 3x3 固定のため ActiveRadius と同じ値に丸める。")]
    private int PreloadRadiusInCells = 1;

    [SerializeField]
    [Min(0)]
    [Tooltip("この半径より外側のセルを削除候補にする。現在は 3x3 固定のため ActiveRadius と同じ値に丸める。")]
    private int UnloadRadiusInCells = 1;

    [SerializeField]
    [Min(1)]
    [Tooltip("1 フレームで作成するセル root 数。Warmup と gameplay streaming の負荷を制限する。")]
    private int MaxCellCreatesPerFrame = 2;

    [SerializeField]
    [Min(1)]
    [Tooltip("1 フレームで削除するセル所有オブジェクト数。木や岩が多いセルの削除負荷を分散する。")]
    private int MaxOwnedEntityDestroysPerFrame = 150;

    [SerializeField]
    [Min(1)]
    [Tooltip("1 フレームで展開する PCG static mesh 数。初期値は 10。重いセルでは小さくして生成負荷を分散する。")]
    private int MaxStaticMeshSpawnsPerFrame = 10;

    [SerializeField]
    [Min(1)]
    [Tooltip("画面に入る可能性があるセルで、1 フレームに優先展開する PCG static mesh 数。ポップインが見える場合は大きくする。")]
    private int MaxVisibleStaticMeshSpawnsPerFrame = 200;

    [SerializeField]
    [Min(0f)]
    [Tooltip("カメラで見えている地面範囲に足す余白。移動直後に画面へ入るセルを先に完成させる。")]
    private float StaticMeshVisiblePadding = 12f;

    [SerializeField]
    [Min(1)]
    [Tooltip("1 フレームで navigation build するセル数。重い場合は 1 のままにする。")]
    private int MaxNavBuildCellsPerFrame = 1;

    [SerializeField]
    [Tooltip("有効なら PreloadRadius 内の後方セルも生成する。無効なら移動方向の後方外周セルを省略する。")]
    private bool PreloadBehindCells = false;

    [SerializeField]
    [Range(-1f, 1f)]
    [Tooltip("後方セル省略時の方向判定。dot がこの値以上の外周セルだけを先読みする。")]
    private float ForwardPreloadDotThreshold = -0.25f;

    [SerializeField]
    [Tooltip("各セルプレハブのルートに使う Y 座標。")]
    private float GroundY = 0f;

    private void OnValidate()
    {
        MigrateLegacyCellPrefab();
        NormalizeCellPrefabWeights();

        RandomSeed = (int)MapCellUtility.NormalizeSeed(RandomSeed);
        CellSize = math.max(0.1f, math.abs(CellSize));
        ActiveRadiusInCells = 1;
        PreloadRadiusInCells = ActiveRadiusInCells;
        UnloadRadiusInCells = ActiveRadiusInCells;
        LoadRadiusInCells = PreloadRadiusInCells;
        MaxCellCreatesPerFrame = MapStreamingUtility.NormalizeFrameBudget(MaxCellCreatesPerFrame);
        MaxOwnedEntityDestroysPerFrame = MapStreamingUtility.NormalizeFrameBudget(MaxOwnedEntityDestroysPerFrame);
        MaxStaticMeshSpawnsPerFrame = MapStreamingUtility.NormalizeFrameBudget(MaxStaticMeshSpawnsPerFrame);
        MaxVisibleStaticMeshSpawnsPerFrame =
            MapStreamingUtility.NormalizeFrameBudget(MaxVisibleStaticMeshSpawnsPerFrame);
        StaticMeshVisiblePadding = math.max(0f, StaticMeshVisiblePadding);
        MaxNavBuildCellsPerFrame = MapStreamingUtility.NormalizeFrameBudget(MaxNavBuildCellsPerFrame);
        ForwardPreloadDotThreshold = math.clamp(ForwardPreloadDotThreshold, -1f, 1f);
    }

    private void MigrateLegacyCellPrefab()
    {
        if ((CellPrefabs != null && CellPrefabs.Length > 0) || CellPrefab == null)
        {
            return;
        }

        CellPrefabs = new[]
        {
            new MapCellPrefabEntry
            {
                Prefab = CellPrefab,
                Weight = 1f
            }
        };
    }

    private void NormalizeCellPrefabWeights()
    {
        if (CellPrefabs == null)
        {
            return;
        }

        for (var prefabIndex = 0; prefabIndex < CellPrefabs.Length; prefabIndex++)
        {
            var entry = CellPrefabs[prefabIndex];

            entry.Weight = MapCellUtility.NormalizeWeight(entry.Weight);
            CellPrefabs[prefabIndex] = entry;
        }
    }

    private MapCellPrefabEntry[] GetEffectiveCellPrefabs()
    {
        if (CellPrefabs != null && CellPrefabs.Length > 0)
        {
            return CellPrefabs;
        }

        if (CellPrefab == null)
        {
            return CellPrefabs;
        }

        return new[]
        {
            new MapCellPrefabEntry
            {
                Prefab = CellPrefab,
                Weight = 1f
            }
        };
    }

    private sealed class Baker : Baker<MapCellAuthoring>
    {
        public override void Bake(MapCellAuthoring authoring)
        {
            var entries = authoring.GetEffectiveCellPrefabs();

            if (entries == null || entries.Length == 0)
            {
                return;
            }

            var entity = GetEntity(TransformUsageFlags.None);
            var activeRadius = 1;

            AddComponent(entity, new MapCellConfig
            {
                WorldSeed = authoring.RandomSeed,
                CellSize = authoring.CellSize,
                LoadRadiusInCells = activeRadius,
                ActiveRadiusInCells = activeRadius,
                PreloadRadiusInCells = activeRadius,
                UnloadRadiusInCells = activeRadius,
                MaxCellCreatesPerFrame = authoring.MaxCellCreatesPerFrame,
                MaxOwnedEntityDestroysPerFrame = authoring.MaxOwnedEntityDestroysPerFrame,
                MaxStaticMeshSpawnsPerFrame = authoring.MaxStaticMeshSpawnsPerFrame,
                MaxVisibleStaticMeshSpawnsPerFrame = authoring.MaxVisibleStaticMeshSpawnsPerFrame,
                StaticMeshVisiblePadding = authoring.StaticMeshVisiblePadding,
                MaxNavBuildCellsPerFrame = authoring.MaxNavBuildCellsPerFrame,
                PreloadBehindCells = authoring.PreloadBehindCells ? (byte)1 : (byte)0,
                ForwardPreloadDotThreshold = authoring.ForwardPreloadDotThreshold,
                GroundY = authoring.GroundY
            });
            AddComponent(entity, new StageRuntimeState
            {
                Phase = StageRuntimePhase.Warmup
            });

            var prefabBuffer = AddBuffer<MapCellPrefabElement>(entity);

            for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                var entry = entries[entryIndex];
                var weight = MapCellUtility.NormalizeWeight(entry.Weight);

                if (entry.Prefab == null || weight <= 0f)
                {
                    continue;
                }

                prefabBuffer.Add(new MapCellPrefabElement
                {
                    Prefab = GetEntity(entry.Prefab, TransformUsageFlags.Dynamic),
                    Weight = weight
                });
            }
        }
    }
}

/// <summary>
/// Inspector で編集する Map Cell prefab と抽選重み。
/// </summary>
[System.Serializable]
public struct MapCellPrefabEntry
{
    [Tooltip("セルプレハブ候補。ルート Transform はセル中心に配置される。")]
    public GameObject Prefab;

    [Min(0f)]
    [Tooltip("相対的な選択重み。0 にするとこの候補は無効になる。")]
    public float Weight;
}

/// <summary>
/// Map Cell の座標計算。
/// </summary>
public static class MapCellUtility
{
    /// <summary>
    /// Unity.Mathematics.Random が受け付ける非ゼロ seed に正規化する。
    /// </summary>
    public static uint NormalizeSeed(int seed)
    {
        if (seed == 0)
        {
            return 1u;
        }

        return (uint)seed;
    }

    /// <summary>
    /// Prefab 抽選 weight を非負値へ正規化する。
    /// </summary>
    public static float NormalizeWeight(float weight)
    {
        return math.max(0f, weight);
    }

    /// <summary>
    /// cell 座標と world seed から、非ゼロの deterministic seed を作る。
    /// </summary>
    public static uint CreateCellSeed(int worldSeed, int2 coord)
    {
        var hash = math.hash(new uint3(
            (uint)worldSeed,
            (uint)coord.x,
            (uint)coord.y));

        if (hash == 0u)
        {
            return 1u;
        }

        return hash;
    }

    /// <summary>
    /// 0 以上 totalWeight 未満の roll から、正の weight を持つ index を選ぶ。
    /// </summary>
    public static int SelectWeightedIndex(float[] weights, float roll)
    {
        if (weights == null)
        {
            return -1;
        }

        var totalWeight = 0f;

        for (var weightIndex = 0; weightIndex < weights.Length; weightIndex++)
        {
            totalWeight += NormalizeWeight(weights[weightIndex]);
        }

        if (totalWeight <= 0f)
        {
            return -1;
        }

        var clampedRoll = math.clamp(roll, 0f, totalWeight);
        var cumulativeWeight = 0f;
        var lastPositiveIndex = -1;

        for (var weightIndex = 0; weightIndex < weights.Length; weightIndex++)
        {
            var weight = NormalizeWeight(weights[weightIndex]);

            if (weight <= 0f)
            {
                continue;
            }

            cumulativeWeight += weight;
            lastPositiveIndex = weightIndex;

            if (clampedRoll < cumulativeWeight)
            {
                return weightIndex;
            }
        }

        return lastPositiveIndex;
    }

    /// <summary>
    /// world 位置から、中心 pivot の Cell 座標を返す。
    /// </summary>
    public static int2 CalculateCellCoord(float3 position, float cellSize)
    {
        var safeCellSize = math.max(0.0001f, math.abs(cellSize));
        var halfCellSize = safeCellSize * 0.5f;

        return new int2(
            (int)math.floor((position.x + halfCellSize) / safeCellSize),
            (int)math.floor((position.z + halfCellSize) / safeCellSize));
    }

    /// <summary>
    /// Cell 座標から、prefab root を置く world 位置を返す。
    /// </summary>
    public static float3 CalculateCellWorldPosition(int2 coord, float cellSize, float groundY)
    {
        var safeCellSize = math.max(0.0001f, math.abs(cellSize));

        return new float3(coord.x * safeCellSize, groundY, coord.y * safeCellSize);
    }

    /// <summary>
    /// Chebyshev 距離で Cell 半径内かどうかを返す。
    /// </summary>
    public static bool IsInsideCellRadius(int2 center, int2 coord, int radius)
    {
        var safeRadius = math.max(0, radius);
        var delta = math.abs(coord - center);

        return math.max(delta.x, delta.y) <= safeRadius;
    }

    /// <summary>
    /// Cell prefab の rotation / scale を保ち、root 位置だけを Cell 中心へ移す。
    /// </summary>
    public static LocalTransform CreateCellRootTransform(
        int2 coord,
        float cellSize,
        float groundY,
        LocalTransform prefabTransform)
    {
        return LocalTransform.FromPositionRotationScale(
            CalculateCellWorldPosition(coord, cellSize, groundY),
            prefabTransform.Rotation,
            prefabTransform.Scale);
    }
}

/// <summary>
/// Map streaming の半径、予算、優先度を計算する。
/// </summary>
public static class MapStreamingUtility
{
    /// <summary>
    /// Gameplay が依存する半径を非負値へ丸める。
    /// </summary>
    public static int NormalizeActiveRadius(int activeRadius)
    {
        return math.max(0, activeRadius);
    }

    /// <summary>
    /// Preload 半径を Active 半径以上へ丸める。
    /// </summary>
    public static int NormalizePreloadRadius(int activeRadius, int preloadRadius)
    {
        return math.max(NormalizeActiveRadius(activeRadius), math.max(0, preloadRadius));
    }

    /// <summary>
    /// Unload 半径を Preload 半径以上へ丸める。
    /// </summary>
    public static int NormalizeUnloadRadius(int preloadRadius, int unloadRadius)
    {
        return math.max(math.max(0, preloadRadius), math.max(0, unloadRadius));
    }

    /// <summary>
    /// 1 frame の処理予算を 1 以上へ丸める。
    /// </summary>
    public static int NormalizeFrameBudget(int budget)
    {
        return math.max(1, budget);
    }

    /// <summary>
    /// ActiveRadius が欠けた場合だけ、通常予算を超えて必要 cell 数まで引き上げる。
    /// </summary>
    public static int CalculateEmergencyCreateBudget(int normalBudget, int missingActiveCellCount)
    {
        return math.max(NormalizeFrameBudget(normalBudget), math.max(0, missingActiveCellCount));
    }

    /// <summary>
    /// ActiveRadius 内が不足しているため、cell root 作成予算を引き上げるべきかを返す。
    /// </summary>
    public static bool NeedsEmergencyActiveCellCreation(
        int missingActiveCellCount,
        bool hasUnreadyActiveCell)
    {
        return missingActiveCellCount > 0 || hasUnreadyActiveCell;
    }

    /// <summary>
    /// 既存 cell を同じ座標の有効 cell として再利用してよいかを返す。
    /// </summary>
    public static bool CanReuseExistingCellForCoordLookup(bool hasUnloadState)
    {
        return !hasUnloadState;
    }

    /// <summary>
    /// Chebyshev 半径内に含まれる cell 数を返す。
    /// </summary>
    public static int CalculateCellCountInRadius(int radius)
    {
        var safeRadius = math.max(0, radius);
        var sideLength = safeRadius * 2 + 1;

        return sideLength * sideLength;
    }

    /// <summary>
    /// Cell unload で今回進められる owned entity destroy cursor を返す。
    /// </summary>
    public static int CalculateNextOwnedEntityDestroyIndex(
        int currentIndex,
        int ownedEntityCount,
        int frameBudget)
    {
        var safeOwnedEntityCount = math.max(0, ownedEntityCount);
        var safeCurrentIndex = math.clamp(currentIndex, 0, safeOwnedEntityCount);
        var safeFrameBudget = NormalizeFrameBudget(frameBudget);

        return math.min(safeOwnedEntityCount, safeCurrentIndex + safeFrameBudget);
    }

    /// <summary>
    /// Camera 表示範囲と交差する Cell を、static mesh の優先展開対象として判定する。
    /// </summary>
    public static bool ShouldPrioritizeStaticMeshSpawn(
        float2 playerPosition,
        int2 cellCoord,
        float cellSize,
        float visibleGroundRadius,
        float visiblePadding)
    {
        var safeCellSize = math.max(0.0001f, math.abs(cellSize));
        var halfCellSize = safeCellSize * 0.5f;
        var cellCenter = new float2(
            cellCoord.x * safeCellSize,
            cellCoord.y * safeCellSize);
        var distanceToCellAabb = CalculateDistanceToCellAabb(
            playerPosition,
            cellCenter,
            halfCellSize);
        var priorityRadius = math.max(0f, visibleGroundRadius) + math.max(0f, visiblePadding);

        return distanceToCellAabb <= priorityRadius;
    }

    private static float CalculateDistanceToCellAabb(
        float2 point,
        float2 cellCenter,
        float halfCellSize)
    {
        var outside = math.max(math.abs(point - cellCenter) - halfCellSize, float2.zero);

        return math.length(outside);
    }

    /// <summary>
    /// Cell が ActiveRadius 内かを返す。
    /// </summary>
    public static bool IsActiveCell(int2 center, int2 coord, int activeRadius)
    {
        return MapCellUtility.IsInsideCellRadius(center, coord, NormalizeActiveRadius(activeRadius));
    }

    /// <summary>
    /// Cell を load / preload 対象にするかを返す。
    /// </summary>
    /// <remarks>
    /// ActiveRadius 内は方向に関係なく必須。Preload 外周だけ、設定により後方を省略する。
    /// </remarks>
    public static bool ShouldLoadCell(
        int2 center,
        int2 coord,
        int activeRadius,
        int preloadRadius,
        float2 preferredDirection,
        byte preloadBehindCells,
        float forwardPreloadDotThreshold)
    {
        if (IsActiveCell(center, coord, activeRadius))
        {
            return true;
        }

        var safePreloadRadius = NormalizePreloadRadius(activeRadius, preloadRadius);

        if (!MapCellUtility.IsInsideCellRadius(center, coord, safePreloadRadius))
        {
            return false;
        }

        if (preloadBehindCells != 0 || math.lengthsq(preferredDirection) <= 0.0001f)
        {
            return true;
        }

        var delta = new float2(coord.x - center.x, coord.y - center.y);

        if (math.lengthsq(delta) <= 0.0001f)
        {
            return true;
        }

        var normalizedDirection = math.normalize(preferredDirection);
        var normalizedDelta = math.normalize(delta);
        var threshold = math.clamp(forwardPreloadDotThreshold, -1f, 1f);

        return math.dot(normalizedDirection, normalizedDelta) >= threshold;
    }

    /// <summary>
    /// Cell を unload 対象にするかを返す。
    /// </summary>
    public static bool ShouldUnloadCell(int2 center, int2 coord, int unloadRadius)
    {
        return !MapCellUtility.IsInsideCellRadius(center, coord, math.max(0, unloadRadius));
    }

    /// <summary>
    /// 小さい値ほど先に load する。ActiveRadius 内を外周 preload より優先する。
    /// </summary>
    public static int CalculateLoadPriority(int2 center, int2 coord, int activeRadius)
    {
        var distance = math.cmax(math.abs(coord - center));

        if (distance <= NormalizeActiveRadius(activeRadius))
        {
            return distance;
        }

        return 1000 + distance;
    }
}

/// <summary>
/// Player を中心に Map Cell prefab の active set を維持する。
///
/// player が別 cell へ移動すると、新しい周辺 cell を instantiate し、範囲外 cell と所有 entity を破棄する。
/// </summary>
[UpdateBefore(typeof(StaticObstacleCollisionSystem))]
public partial struct MapCellSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
        state.RequireForUpdate<MapCellConfig>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var velocityLookup = SystemAPI.GetComponentLookup<Velocity>(true);
        var facingLookup = SystemAPI.GetComponentLookup<FacingDirection>(true);
        var prefabTransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true);
        var unloadStateLookup = SystemAPI.GetComponentLookup<MapCellUnloadState>(true);
        var mapCellLookup = SystemAPI.GetComponentLookup<MapCell>(true);
        var pcgLocalInstanceLookup = SystemAPI.GetBufferLookup<PCGStaticMeshLocalInstance>(true);
        var navConfigLookup = SystemAPI.GetComponentLookup<MapNavConfig>(true);
        var navCellDataLookup = SystemAPI.GetComponentLookup<MapNavCellData>(true);
        var linkedEntityLookup = SystemAPI.GetBufferLookup<LinkedEntityGroup>(true);
        var ownedEntityLookup = SystemAPI.GetBufferLookup<MapCellOwnedEntityElement>(true);

        if (!TryGetPlayerState(
                ref state,
                velocityLookup,
                facingLookup,
                out var playerPosition,
                out var preferredDirection))
        {
            return;
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var isWarmupActive = IsWarmupActive(ref state);

        foreach (var (config, cellPrefabs, configEntity) in
                 SystemAPI.Query<RefRO<MapCellConfig>, DynamicBuffer<MapCellPrefabElement>>()
                     .WithEntityAccess())
        {
            if (config.ValueRO.CellSize <= 0f ||
                cellPrefabs.Length == 0)
            {
                continue;
            }

            var centerCoord = MapCellUtility.CalculateCellCoord(
                playerPosition,
                config.ValueRO.CellSize);
            var ownedDestroyBudget =
                MapStreamingUtility.NormalizeFrameBudget(config.ValueRO.MaxOwnedEntityDestroysPerFrame);
            var createBudget = MapStreamingUtility.NormalizeFrameBudget(config.ValueRO.MaxCellCreatesPerFrame);
            var activeRadius = MapStreamingUtility.NormalizeActiveRadius(config.ValueRO.ActiveRadiusInCells);
            var missingActiveCellCount = CountMissingActiveCells(
                ref state,
                configEntity,
                centerCoord,
                activeRadius,
                unloadStateLookup);
            var hasUnreadyActiveCell = HasUnreadyActiveCells(
                ref state,
                configEntity,
                centerCoord,
                activeRadius,
                mapCellLookup,
                unloadStateLookup,
                pcgLocalInstanceLookup,
                navConfigLookup,
                navCellDataLookup);

            if (MapStreamingUtility.NeedsEmergencyActiveCellCreation(
                    missingActiveCellCount,
                    hasUnreadyActiveCell))
            {
                createBudget = MapStreamingUtility.CalculateEmergencyCreateBudget(
                    createBudget,
                    missingActiveCellCount);
            }

            BeginUnloadFarCells(
                ref state,
                ref entityCommandBuffer,
                unloadStateLookup,
                configEntity,
                centerCoord,
                config.ValueRO.UnloadRadiusInCells);
            ProcessUnloadingCells(
                ref state,
                ref entityCommandBuffer,
                linkedEntityLookup,
                ownedEntityLookup,
                configEntity,
                ownedDestroyBudget);
            LoadMissingCells(
                ref state,
                ref entityCommandBuffer,
                configEntity,
                config.ValueRO,
                cellPrefabs,
                prefabTransformLookup,
                unloadStateLookup,
                centerCoord,
                isWarmupActive ? float2.zero : preferredDirection,
                isWarmupActive ? (byte)1 : config.ValueRO.PreloadBehindCells,
                createBudget);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

    private bool TryGetPlayerState(
        ref SystemState state,
        ComponentLookup<Velocity> velocityLookup,
        ComponentLookup<FacingDirection> facingLookup,
        out float3 playerPosition,
        out float2 preferredDirection)
    {
        foreach (var (transform, entity) in
                 SystemAPI.Query<RefRO<LocalTransform>>()
                     .WithAll<PlayerTag>()
                     .WithEntityAccess())
        {
            playerPosition = transform.ValueRO.Position;
            preferredDirection = GetPreferredDirection(
                velocityLookup,
                facingLookup,
                entity);
            return true;
        }

        playerPosition = float3.zero;
        preferredDirection = float2.zero;
        return false;
    }

    private static float2 GetPreferredDirection(
        ComponentLookup<Velocity> velocityLookup,
        ComponentLookup<FacingDirection> facingLookup,
        Entity playerEntity)
    {
        if (velocityLookup.HasComponent(playerEntity))
        {
            var velocity = velocityLookup[playerEntity].Value.xz;

            if (math.lengthsq(velocity) > 0.0001f)
            {
                return math.normalize(velocity);
            }
        }

        if (facingLookup.HasComponent(playerEntity))
        {
            var facing = facingLookup[playerEntity].Value;

            if (math.lengthsq(facing) > 0.0001f)
            {
                return math.normalize(facing);
            }
        }

        return float2.zero;
    }

    private bool IsWarmupActive(ref SystemState state)
    {
        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (runtimeState.ValueRO.Phase == StageRuntimePhase.Warmup)
            {
                return true;
            }
        }

        return false;
    }

    private static LocalTransform GetPrefabTransform(
        ComponentLookup<LocalTransform> prefabTransformLookup,
        Entity prefabEntity)
    {
        if (prefabTransformLookup.HasComponent(prefabEntity))
        {
            return prefabTransformLookup[prefabEntity];
        }

        return LocalTransform.Identity;
    }

    private void BeginUnloadFarCells(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        ComponentLookup<MapCellUnloadState> unloadStateLookup,
        Entity configEntity,
        int2 centerCoord,
        int unloadRadiusInCells)
    {
        foreach (var (cell, cellEntity) in
                 SystemAPI.Query<RefRO<MapCell>>()
                     .WithEntityAccess())
        {
            if (cell.ValueRO.ConfigEntity != configEntity ||
                unloadStateLookup.HasComponent(cellEntity) ||
                !MapStreamingUtility.ShouldUnloadCell(centerCoord, cell.ValueRO.Coord, unloadRadiusInCells))
            {
                continue;
            }

            entityCommandBuffer.AddComponent(cellEntity, new MapCellUnloadState
            {
                Phase = 0,
                NextOwnedEntityIndex = 0
            });
        }
    }

    private void ProcessUnloadingCells(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        BufferLookup<LinkedEntityGroup> linkedEntityLookup,
        BufferLookup<MapCellOwnedEntityElement> ownedEntityLookup,
        Entity configEntity,
        int maxOwnedEntityDestroys)
    {
        var remainingOwnedEntityDestroys = MapStreamingUtility.NormalizeFrameBudget(maxOwnedEntityDestroys);

        foreach (var (cell, unloadState, cellEntity) in
                 SystemAPI.Query<RefRO<MapCell>, RefRW<MapCellUnloadState>>()
                     .WithEntityAccess())
        {
            if (cell.ValueRO.ConfigEntity != configEntity)
            {
                continue;
            }

            if (!ownedEntityLookup.HasBuffer(cellEntity))
            {
                DestroyLinkedEntityGroup(
                    ref entityCommandBuffer,
                    linkedEntityLookup,
                    cellEntity);
                continue;
            }

            var ownedEntities = ownedEntityLookup[cellEntity];
            var ownedEntityIndex = math.clamp(
                unloadState.ValueRO.NextOwnedEntityIndex,
                0,
                ownedEntities.Length);
            var nextOwnedEntityIndex = MapStreamingUtility.CalculateNextOwnedEntityDestroyIndex(
                ownedEntityIndex,
                ownedEntities.Length,
                remainingOwnedEntityDestroys);

            while (ownedEntityIndex < nextOwnedEntityIndex)
            {
                DestroyLinkedEntityGroup(
                    ref entityCommandBuffer,
                    linkedEntityLookup,
                    ownedEntities[ownedEntityIndex].Value);
                ownedEntityIndex++;
                remainingOwnedEntityDestroys--;
            }

            if (ownedEntityIndex >= ownedEntities.Length)
            {
                DestroyLinkedEntityGroup(
                    ref entityCommandBuffer,
                    linkedEntityLookup,
                    cellEntity);
            }
            else
            {
                unloadState.ValueRW.NextOwnedEntityIndex = ownedEntityIndex;
            }

            if (remainingOwnedEntityDestroys <= 0)
            {
                return;
            }
        }
    }

    private int CountMissingActiveCells(
        ref SystemState state,
        Entity configEntity,
        int2 centerCoord,
        int activeRadius,
        ComponentLookup<MapCellUnloadState> unloadStateLookup)
    {
        var missingCellCount = 0;

        for (var cellZ = centerCoord.y - activeRadius; cellZ <= centerCoord.y + activeRadius; cellZ++)
        {
            for (var cellX = centerCoord.x - activeRadius; cellX <= centerCoord.x + activeRadius; cellX++)
            {
                if (!TryGetCellEntity(
                        ref state,
                        configEntity,
                        new int2(cellX, cellZ),
                        unloadStateLookup,
                        out _))
                {
                    missingCellCount++;
                }
            }
        }

        return missingCellCount;
    }

    private bool HasUnreadyActiveCells(
        ref SystemState state,
        Entity configEntity,
        int2 centerCoord,
        int activeRadius,
        ComponentLookup<MapCell> mapCellLookup,
        ComponentLookup<MapCellUnloadState> unloadStateLookup,
        BufferLookup<PCGStaticMeshLocalInstance> pcgLocalInstanceLookup,
        ComponentLookup<MapNavConfig> navConfigLookup,
        ComponentLookup<MapNavCellData> navCellDataLookup)
    {
        for (var cellZ = centerCoord.y - activeRadius; cellZ <= centerCoord.y + activeRadius; cellZ++)
        {
            for (var cellX = centerCoord.x - activeRadius; cellX <= centerCoord.x + activeRadius; cellX++)
            {
                if (!TryGetCellEntity(
                        ref state,
                        configEntity,
                        new int2(cellX, cellZ),
                        unloadStateLookup,
                        out var cellEntity) ||
                    !IsCellReady(
                        configEntity,
                        cellEntity,
                        mapCellLookup,
                        unloadStateLookup,
                        pcgLocalInstanceLookup,
                        navConfigLookup,
                        navCellDataLookup))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryGetCellEntity(
        ref SystemState state,
        Entity configEntity,
        int2 coord,
        ComponentLookup<MapCellUnloadState> unloadStateLookup,
        out Entity cellEntity)
    {
        foreach (var (cell, entity) in
                 SystemAPI.Query<RefRO<MapCell>>()
                     .WithEntityAccess())
        {
            if (cell.ValueRO.ConfigEntity == configEntity &&
                math.all(cell.ValueRO.Coord == coord) &&
                MapStreamingUtility.CanReuseExistingCellForCoordLookup(
                    unloadStateLookup.HasComponent(entity)))
            {
                cellEntity = entity;
                return true;
            }
        }

        cellEntity = Entity.Null;
        return false;
    }

    private bool IsCellReady(
        Entity configEntity,
        Entity cellEntity,
        ComponentLookup<MapCell> mapCellLookup,
        ComponentLookup<MapCellUnloadState> unloadStateLookup,
        BufferLookup<PCGStaticMeshLocalInstance> pcgLocalInstanceLookup,
        ComponentLookup<MapNavConfig> navConfigLookup,
        ComponentLookup<MapNavCellData> navCellDataLookup)
    {
        if (cellEntity == Entity.Null ||
            unloadStateLookup.HasComponent(cellEntity))
        {
            return false;
        }

        if (mapCellLookup.HasComponent(cellEntity))
        {
            var cell = mapCellLookup[cellEntity];

            if (pcgLocalInstanceLookup.HasBuffer(cellEntity) &&
                cell.LocalStaticMeshSpawned == 0)
            {
                return false;
            }
        }

        return !navConfigLookup.HasComponent(configEntity) ||
               navCellDataLookup.HasComponent(cellEntity);
    }

    private void LoadMissingCells(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity configEntity,
        MapCellConfig config,
        DynamicBuffer<MapCellPrefabElement> cellPrefabs,
        ComponentLookup<LocalTransform> prefabTransformLookup,
        ComponentLookup<MapCellUnloadState> unloadStateLookup,
        int2 centerCoord,
        float2 preferredDirection,
        byte preloadBehindCells,
        int maxCellCreates)
    {
        var remainingCreates = MapStreamingUtility.NormalizeFrameBudget(maxCellCreates);
        var activeRadius = MapStreamingUtility.NormalizeActiveRadius(config.ActiveRadiusInCells);
        var preloadRadius = MapStreamingUtility.NormalizePreloadRadius(
            activeRadius,
            config.PreloadRadiusInCells);

        remainingCreates -= LoadMissingCellsByPriority(
            ref state,
            ref entityCommandBuffer,
            configEntity,
            config,
            cellPrefabs,
            prefabTransformLookup,
            unloadStateLookup,
            centerCoord,
            float2.zero,
            activeRadius,
            activeRadius,
            preloadBehindCells: 1,
            includeActiveCells: true,
            maxCellCreates: remainingCreates);

        if (remainingCreates <= 0 || preloadRadius <= activeRadius)
        {
            return;
        }

        LoadMissingCellsByPriority(
            ref state,
            ref entityCommandBuffer,
            configEntity,
            config,
            cellPrefabs,
            prefabTransformLookup,
            unloadStateLookup,
            centerCoord,
            preferredDirection,
            activeRadius,
            preloadRadius,
            preloadBehindCells,
            includeActiveCells: false,
            maxCellCreates: remainingCreates);
    }

    private int LoadMissingCellsByPriority(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity configEntity,
        MapCellConfig config,
        DynamicBuffer<MapCellPrefabElement> cellPrefabs,
        ComponentLookup<LocalTransform> prefabTransformLookup,
        ComponentLookup<MapCellUnloadState> unloadStateLookup,
        int2 centerCoord,
        float2 preferredDirection,
        int activeRadius,
        int preloadRadius,
        byte preloadBehindCells,
        bool includeActiveCells,
        int maxCellCreates)
    {
        var createdCellCount = 0;
        var maxPriority = 1000 + preloadRadius;
        var firstPriority = includeActiveCells ? 0 : 1000 + activeRadius + 1;

        for (var priority = firstPriority; priority <= maxPriority; priority++)
        {
            if (priority > activeRadius && priority < 1000)
            {
                priority = 1000 + activeRadius + 1;
            }

            for (var cellZ = centerCoord.y - preloadRadius;
                 cellZ <= centerCoord.y + preloadRadius;
                 cellZ++)
            {
                for (var cellX = centerCoord.x - preloadRadius;
                     cellX <= centerCoord.x + preloadRadius;
                     cellX++)
                {
                    var cellCoord = new int2(cellX, cellZ);

                    if (MapStreamingUtility.CalculateLoadPriority(centerCoord, cellCoord, activeRadius) != priority ||
                        !MapStreamingUtility.ShouldLoadCell(
                            centerCoord,
                            cellCoord,
                            activeRadius,
                            preloadRadius,
                            preferredDirection,
                            preloadBehindCells,
                            config.ForwardPreloadDotThreshold) ||
                        HasAvailableCell(ref state, configEntity, cellCoord, unloadStateLookup))
                    {
                        continue;
                    }

                    CreateCell(
                        ref entityCommandBuffer,
                        configEntity,
                        config,
                        cellPrefabs,
                        prefabTransformLookup,
                        cellCoord);
                    createdCellCount++;

                    if (createdCellCount >= maxCellCreates)
                    {
                        return createdCellCount;
                    }
                }
            }
        }

        return createdCellCount;
    }

    private bool HasAvailableCell(
        ref SystemState state,
        Entity configEntity,
        int2 coord,
        ComponentLookup<MapCellUnloadState> unloadStateLookup)
    {
        foreach (var (cell, entity) in
                 SystemAPI.Query<RefRO<MapCell>>()
                     .WithEntityAccess())
        {
            if (cell.ValueRO.ConfigEntity == configEntity &&
                math.all(cell.ValueRO.Coord == coord) &&
                MapStreamingUtility.CanReuseExistingCellForCoordLookup(
                    unloadStateLookup.HasComponent(entity)))
            {
                return true;
            }
        }

        return false;
    }

    private void DestroyLinkedEntityGroup(
        ref EntityCommandBuffer entityCommandBuffer,
        BufferLookup<LinkedEntityGroup> linkedEntityLookup,
        Entity rootEntity)
    {
        if (!linkedEntityLookup.HasBuffer(rootEntity))
        {
            entityCommandBuffer.DestroyEntity(rootEntity);
            return;
        }

        var linkedEntities = linkedEntityLookup[rootEntity];

        for (var linkedEntityIndex = 0; linkedEntityIndex < linkedEntities.Length; linkedEntityIndex++)
        {
            entityCommandBuffer.DestroyEntity(linkedEntities[linkedEntityIndex].Value);
        }
    }

    private static void CreateCell(
        ref EntityCommandBuffer entityCommandBuffer,
        Entity configEntity,
        MapCellConfig config,
        DynamicBuffer<MapCellPrefabElement> cellPrefabs,
        ComponentLookup<LocalTransform> prefabTransformLookup,
        int2 cellCoord)
    {
        var cellPrefab = SelectCellPrefab(cellPrefabs, config.WorldSeed, cellCoord);

        if (cellPrefab == Entity.Null)
        {
            return;
        }

        var prefabTransform = GetPrefabTransform(prefabTransformLookup, cellPrefab);
        var cellEntity = entityCommandBuffer.Instantiate(cellPrefab);

        entityCommandBuffer.SetComponent(
            cellEntity,
            MapCellUtility.CreateCellRootTransform(
                cellCoord,
                config.CellSize,
                config.GroundY,
                prefabTransform));
        entityCommandBuffer.AddComponent(cellEntity, new MapCell
        {
            ConfigEntity = configEntity,
            Coord = cellCoord,
            LocalStaticMeshSpawned = 0,
            NavBuildDelayFrames = 0,
            MonstersSpawned = 0,
            LocalStaticMeshSpawnCursor = 0
        });
        entityCommandBuffer.AddBuffer<MapCellOwnedEntityElement>(cellEntity);
    }

    private static Entity SelectCellPrefab(
        DynamicBuffer<MapCellPrefabElement> cellPrefabs,
        int worldSeed,
        int2 cellCoord)
    {
        var totalWeight = 0f;

        for (var prefabIndex = 0; prefabIndex < cellPrefabs.Length; prefabIndex++)
        {
            totalWeight += MapCellUtility.NormalizeWeight(cellPrefabs[prefabIndex].Weight);
        }

        if (totalWeight <= 0f)
        {
            return Entity.Null;
        }

        var random = new Unity.Mathematics.Random(MapCellUtility.CreateCellSeed(worldSeed, cellCoord));
        var roll = random.NextFloat(totalWeight);
        var cumulativeWeight = 0f;
        var lastPositivePrefab = Entity.Null;

        for (var prefabIndex = 0; prefabIndex < cellPrefabs.Length; prefabIndex++)
        {
            var cellPrefab = cellPrefabs[prefabIndex];
            var weight = MapCellUtility.NormalizeWeight(cellPrefab.Weight);

            if (weight <= 0f)
            {
                continue;
            }

            cumulativeWeight += weight;
            lastPositivePrefab = cellPrefab.Prefab;

            if (roll < cumulativeWeight)
            {
                return cellPrefab.Prefab;
            }
        }

        return lastPositivePrefab;
    }
}

/// <summary>
/// Stage 開始時に、必須 map streaming 範囲が完成するまで gameplay phase への遷移を遅らせる。
/// </summary>
[UpdateAfter(typeof(MapNavBuildSystem))]
[UpdateBefore(typeof(TimedSurvivalStageSystem))]
public partial struct MapStreamingWarmupSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
        state.RequireForUpdate<MapCellConfig>();
        state.RequireForUpdate<StageRuntimeState>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (!TryGetPlayerPosition(ref state, out var playerPosition))
        {
            return;
        }

        var navConfigLookup = SystemAPI.GetComponentLookup<MapNavConfig>(true);
        var pcgLocalInstanceLookup = SystemAPI.GetBufferLookup<PCGStaticMeshLocalInstance>(true);
        var navCellDataLookup = SystemAPI.GetComponentLookup<MapNavCellData>(true);
        var unloadStateLookup = SystemAPI.GetComponentLookup<MapCellUnloadState>(true);

        foreach (var (config, runtimeState, configEntity) in
                 SystemAPI.Query<RefRO<MapCellConfig>, RefRW<StageRuntimeState>>()
                     .WithEntityAccess())
        {
            if (runtimeState.ValueRO.Phase != StageRuntimePhase.Warmup)
            {
                continue;
            }

            var centerCoord = MapCellUtility.CalculateCellCoord(
                playerPosition,
                config.ValueRO.CellSize);

            if (!IsStreamingReady(
                    ref state,
                    configEntity,
                    config.ValueRO,
                    centerCoord,
                    navConfigLookup,
                    pcgLocalInstanceLookup,
                    navCellDataLookup,
                    unloadStateLookup))
            {
                continue;
            }

            runtimeState.ValueRW.Phase = StageRuntimePhase.Gameplay;
        }
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

    private bool IsStreamingReady(
        ref SystemState state,
        Entity configEntity,
        MapCellConfig config,
        int2 centerCoord,
        ComponentLookup<MapNavConfig> navConfigLookup,
        BufferLookup<PCGStaticMeshLocalInstance> pcgLocalInstanceLookup,
        ComponentLookup<MapNavCellData> navCellDataLookup,
        ComponentLookup<MapCellUnloadState> unloadStateLookup)
    {
        var activeRadius = MapStreamingUtility.NormalizeActiveRadius(config.ActiveRadiusInCells);
        var preloadRadius = MapStreamingUtility.NormalizePreloadRadius(
            activeRadius,
            config.PreloadRadiusInCells);

        for (var cellZ = centerCoord.y - preloadRadius; cellZ <= centerCoord.y + preloadRadius; cellZ++)
        {
            for (var cellX = centerCoord.x - preloadRadius; cellX <= centerCoord.x + preloadRadius; cellX++)
            {
                var cellCoord = new int2(cellX, cellZ);

                if (!MapStreamingUtility.ShouldLoadCell(
                        centerCoord,
                        cellCoord,
                        activeRadius,
                        preloadRadius,
                        preferredDirection: float2.zero,
                        preloadBehindCells: 1,
                        forwardPreloadDotThreshold: config.ForwardPreloadDotThreshold))
                {
                    continue;
                }

                if (!TryGetReadyCell(
                        ref state,
                        configEntity,
                        cellCoord,
                        navConfigLookup,
                        pcgLocalInstanceLookup,
                        navCellDataLookup,
                        unloadStateLookup))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool TryGetReadyCell(
        ref SystemState state,
        Entity configEntity,
        int2 cellCoord,
        ComponentLookup<MapNavConfig> navConfigLookup,
        BufferLookup<PCGStaticMeshLocalInstance> pcgLocalInstanceLookup,
        ComponentLookup<MapNavCellData> navCellDataLookup,
        ComponentLookup<MapCellUnloadState> unloadStateLookup)
    {
        var needsNavBuild = navConfigLookup.HasComponent(configEntity);

        foreach (var (cell, cellEntity) in
                 SystemAPI.Query<RefRO<MapCell>>()
                     .WithEntityAccess())
        {
            if (cell.ValueRO.ConfigEntity != configEntity ||
                !math.all(cell.ValueRO.Coord == cellCoord) ||
                !MapStreamingUtility.CanReuseExistingCellForCoordLookup(
                    unloadStateLookup.HasComponent(cellEntity)))
            {
                continue;
            }

            if (pcgLocalInstanceLookup.HasBuffer(cellEntity) &&
                cell.ValueRO.LocalStaticMeshSpawned == 0)
            {
                return false;
            }

            if (needsNavBuild &&
                !navCellDataLookup.HasComponent(cellEntity))
            {
                return false;
            }

            return true;
        }

        return false;
    }
}
