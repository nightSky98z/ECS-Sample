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
    public int WorldSeed;
    public float CellSize;
    public int LoadRadiusInCells;
    public float GroundY;
}

/// <summary>
/// Runtime で選択する Map Cell prefab 候補。
/// </summary>
[InternalBufferCapacity(8)]
public struct MapCellPrefabElement : IBufferElementData
{
    public Entity Prefab;
    public float Weight;
}

/// <summary>
/// Runtime に存在する Map Cell root。
/// </summary>
public struct MapCell : IComponentData
{
    public Entity ConfigEntity;
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
}

/// <summary>
/// Map Cell が所有し、Cell unload 時に一緒に破棄される Entity root。
/// </summary>
public struct MapCellOwnedEntity : IComponentData
{
    public Entity CellEntity;
}

/// <summary>
/// Player 周辺に Cell prefab を生成する Authoring。
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
    [Min(0)]
    [Tooltip("プレイヤー周辺でロード維持する半径。1 は現在セルと周囲 8 セルを意味する。")]
    private int LoadRadiusInCells = 1;

    [SerializeField]
    [Tooltip("各セルプレハブのルートに使う Y 座標。")]
    private float GroundY = 0f;

    private void OnValidate()
    {
        MigrateLegacyCellPrefab();
        NormalizeCellPrefabWeights();

        RandomSeed = (int)MapCellUtility.NormalizeSeed(RandomSeed);
        CellSize = math.max(0.1f, math.abs(CellSize));
        LoadRadiusInCells = math.max(0, LoadRadiusInCells);
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

            AddComponent(entity, new MapCellConfig
            {
                WorldSeed = authoring.RandomSeed,
                CellSize = authoring.CellSize,
                LoadRadiusInCells = authoring.LoadRadiusInCells,
                GroundY = authoring.GroundY
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
/// Player を中心に Map Cell prefab の active set を維持する。
/// </summary>
[UpdateBefore(typeof(StaticObstacleCollisionSystem))]
public partial struct MapCellSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
        state.RequireForUpdate<MapCellConfig>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!TryGetPlayerPosition(ref state, out var playerPosition))
        {
            return;
        }

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

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

            UnloadFarCells(
                ref state,
                ref entityCommandBuffer,
                configEntity,
                centerCoord,
                config.ValueRO.LoadRadiusInCells);
            LoadMissingCells(
                ref state,
                ref entityCommandBuffer,
                configEntity,
                config.ValueRO,
                cellPrefabs,
                centerCoord);
        }

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

    private static LocalTransform GetPrefabTransform(ref SystemState state, Entity prefabEntity)
    {
        if (state.EntityManager.HasComponent<LocalTransform>(prefabEntity))
        {
            return state.EntityManager.GetComponentData<LocalTransform>(prefabEntity);
        }

        return LocalTransform.Identity;
    }

    private void UnloadFarCells(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity configEntity,
        int2 centerCoord,
        int loadRadiusInCells)
    {
        foreach (var (cell, cellEntity) in
                 SystemAPI.Query<RefRO<MapCell>>()
                     .WithEntityAccess())
        {
            if (cell.ValueRO.ConfigEntity != configEntity ||
                MapCellUtility.IsInsideCellRadius(centerCoord, cell.ValueRO.Coord, loadRadiusInCells))
            {
                continue;
            }

            DestroyOwnedEntities(ref state, ref entityCommandBuffer, cellEntity);
            DestroyLinkedEntityGroup(ref state, ref entityCommandBuffer, cellEntity);
        }
    }

    private void DestroyOwnedEntities(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity cellEntity)
    {
        foreach (var (ownedEntity, entity) in
                 SystemAPI.Query<RefRO<MapCellOwnedEntity>>()
                     .WithEntityAccess())
        {
            if (ownedEntity.ValueRO.CellEntity != cellEntity)
            {
                continue;
            }

            DestroyLinkedEntityGroup(ref state, ref entityCommandBuffer, entity);
        }
    }

    private void LoadMissingCells(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity configEntity,
        MapCellConfig config,
        DynamicBuffer<MapCellPrefabElement> cellPrefabs,
        int2 centerCoord)
    {
        var loadRadiusInCells = math.max(0, config.LoadRadiusInCells);

        for (var cellZ = centerCoord.y - loadRadiusInCells;
             cellZ <= centerCoord.y + loadRadiusInCells;
             cellZ++)
        {
            for (var cellX = centerCoord.x - loadRadiusInCells;
                 cellX <= centerCoord.x + loadRadiusInCells;
                 cellX++)
            {
                var cellCoord = new int2(cellX, cellZ);

                if (HasCell(ref state, configEntity, cellCoord))
                {
                    continue;
                }

                CreateCell(
                    ref state,
                    ref entityCommandBuffer,
                    configEntity,
                    config,
                    cellPrefabs,
                    cellCoord);
            }
        }
    }

    private bool HasCell(ref SystemState state, Entity configEntity, int2 coord)
    {
        foreach (var cell in SystemAPI.Query<RefRO<MapCell>>())
        {
            if (cell.ValueRO.ConfigEntity == configEntity &&
                math.all(cell.ValueRO.Coord == coord))
            {
                return true;
            }
        }

        return false;
    }

    private void DestroyLinkedEntityGroup(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity rootEntity)
    {
        if (!SystemAPI.HasBuffer<LinkedEntityGroup>(rootEntity))
        {
            entityCommandBuffer.DestroyEntity(rootEntity);
            return;
        }

        var linkedEntities = SystemAPI.GetBuffer<LinkedEntityGroup>(rootEntity);

        for (var linkedEntityIndex = 0; linkedEntityIndex < linkedEntities.Length; linkedEntityIndex++)
        {
            entityCommandBuffer.DestroyEntity(linkedEntities[linkedEntityIndex].Value);
        }
    }

    private static void CreateCell(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity configEntity,
        MapCellConfig config,
        DynamicBuffer<MapCellPrefabElement> cellPrefabs,
        int2 cellCoord)
    {
        var cellPrefab = SelectCellPrefab(cellPrefabs, config.WorldSeed, cellCoord);

        if (cellPrefab == Entity.Null)
        {
            return;
        }

        var prefabTransform = GetPrefabTransform(ref state, cellPrefab);
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
                MonstersSpawned = 0
            });
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
