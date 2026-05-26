using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// Player 周辺に通常 monster を維持する runtime spawn director の設定。
/// </summary>
public struct MonsterSpawnDirectorConfig : IComponentData
{
    public int MinSpawnCountPerCell;
    public int MaxSpawnCountPerCell;
    public float MinSpawnDistanceFromPlayer;
    public float MaxSpawnDistanceFromPlayer;
    public float CellSpawnPadding;
    public int WorldSeed;
}

/// <summary>
/// Runtime で選択する monster prefab 候補。
/// </summary>
[InternalBufferCapacity(8)]
public struct MonsterSpawnPrefabElement : IBufferElementData
{
    public Entity Prefab;
    public float Weight;
}

/// <summary>
/// Map Cell に対する monster spawn が完了済みであることを示すタグ。
/// </summary>
public struct MapCellMonsterSpawned : IComponentData
{

}

/// <summary>
/// 通常 monster を player 周辺だけに維持する Authoring。
/// </summary>
public sealed class MonsterSpawnDirectorAuthoring : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Monster prefab candidates instantiated for newly loaded map cells. Entries with weight 0 or less are ignored.")]
    private MonsterSpawnPrefabEntry[] MonsterPrefabs = new MonsterSpawnPrefabEntry[0];

    [SerializeField]
    [HideInInspector]
    [Tooltip("Legacy single monster prefab. Migrated as weight 1 when MonsterPrefabs is empty.")]
    private GameObject MonsterPrefab;

    [SerializeField]
    [HideInInspector]
    [Tooltip("Legacy fixed spawn count. Use MinSpawnCountPerCell and MaxSpawnCountPerCell instead.")]
    private int SpawnCountPerCell = -1;

    [SerializeField]
    [Min(0)]
    [Tooltip("Minimum monster count spawned once for each newly loaded map cell.")]
    private int MinSpawnCountPerCell = 4;

    [SerializeField]
    [Min(0)]
    [Tooltip("Maximum monster count spawned once for each newly loaded map cell. 0 disables cell monster spawning.")]
    private int MaxSpawnCountPerCell = 8;

    [SerializeField]
    [Min(0f)]
    [Tooltip("Minimum XZ distance from the player. Use this as the off-screen spawn radius.")]
    private float MinSpawnDistanceFromPlayer = 18f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("Maximum XZ distance from the player. Keep this inside the loaded map cell area.")]
    private float MaxSpawnDistanceFromPlayer = 42f;

    [SerializeField]
    [Tooltip("Distance from each cell edge where monsters are not spawned.")]
    private float CellSpawnPadding = 8f;

    [SerializeField]
    [Tooltip("World seed used to create deterministic monster placements per cell.")]
    private int RandomSeed = 1;

    private void OnValidate()
    {
        MigrateLegacyMonsterPrefab();
        MigrateLegacySpawnCount();
        NormalizeMonsterPrefabWeights();
        MonsterSpawnDirectorUtility.NormalizeCellSpawnCountRange(
            MinSpawnCountPerCell,
            MaxSpawnCountPerCell,
            out MinSpawnCountPerCell,
            out MaxSpawnCountPerCell);
        MonsterSpawnDirectorUtility.NormalizeSpawnDistanceRange(
            MinSpawnDistanceFromPlayer,
            MaxSpawnDistanceFromPlayer,
            out MinSpawnDistanceFromPlayer,
            out MaxSpawnDistanceFromPlayer);
        CellSpawnPadding = math.max(0f, math.abs(CellSpawnPadding));
        RandomSeed = (int)PCGStaticMeshUtility.NormalizeSeed(RandomSeed);
    }

    private void MigrateLegacyMonsterPrefab()
    {
        if ((MonsterPrefabs != null && MonsterPrefabs.Length > 0) || MonsterPrefab == null)
        {
            return;
        }

        MonsterPrefabs = new[]
        {
            new MonsterSpawnPrefabEntry
            {
                Prefab = MonsterPrefab,
                Weight = 1f
            }
        };
    }

    private void MigrateLegacySpawnCount()
    {
        if (SpawnCountPerCell < 0)
        {
            return;
        }

        MinSpawnCountPerCell = SpawnCountPerCell;
        MaxSpawnCountPerCell = SpawnCountPerCell;
        SpawnCountPerCell = -1;
    }

    private void NormalizeMonsterPrefabWeights()
    {
        if (MonsterPrefabs == null)
        {
            return;
        }

        for (var prefabIndex = 0; prefabIndex < MonsterPrefabs.Length; prefabIndex++)
        {
            var entry = MonsterPrefabs[prefabIndex];

            entry.Weight = MapCellUtility.NormalizeWeight(entry.Weight);
            MonsterPrefabs[prefabIndex] = entry;
        }
    }

    private MonsterSpawnPrefabEntry[] GetEffectiveMonsterPrefabs()
    {
        if (MonsterPrefabs != null && MonsterPrefabs.Length > 0)
        {
            return MonsterPrefabs;
        }

        if (MonsterPrefab == null)
        {
            return MonsterPrefabs;
        }

        return new[]
        {
            new MonsterSpawnPrefabEntry
            {
                Prefab = MonsterPrefab,
                Weight = 1f
            }
        };
    }

    private void GetEffectiveSpawnCountRange(out int minSpawnCount, out int maxSpawnCount)
    {
        if (SpawnCountPerCell >= 0)
        {
            MonsterSpawnDirectorUtility.NormalizeCellSpawnCountRange(
                SpawnCountPerCell,
                SpawnCountPerCell,
                out minSpawnCount,
                out maxSpawnCount);
            return;
        }

        MonsterSpawnDirectorUtility.NormalizeCellSpawnCountRange(
            MinSpawnCountPerCell,
            MaxSpawnCountPerCell,
            out minSpawnCount,
            out maxSpawnCount);
    }

    private sealed class Baker : Baker<MonsterSpawnDirectorAuthoring>
    {
        public override void Bake(MonsterSpawnDirectorAuthoring authoring)
        {
            var monsterPrefabs = authoring.GetEffectiveMonsterPrefabs();

            if (monsterPrefabs == null || monsterPrefabs.Length == 0)
            {
                return;
            }

            var entity = GetEntity(TransformUsageFlags.None);

            authoring.GetEffectiveSpawnCountRange(
                out var minSpawnCount,
                out var maxSpawnCount);

            AddComponent(entity, new MonsterSpawnDirectorConfig
            {
                MinSpawnCountPerCell = minSpawnCount,
                MaxSpawnCountPerCell = maxSpawnCount,
                MinSpawnDistanceFromPlayer = authoring.MinSpawnDistanceFromPlayer,
                MaxSpawnDistanceFromPlayer = authoring.MaxSpawnDistanceFromPlayer,
                CellSpawnPadding = authoring.CellSpawnPadding,
                WorldSeed = authoring.RandomSeed
            });

            var monsterPrefabBuffer = AddBuffer<MonsterSpawnPrefabElement>(entity);

            for (var prefabIndex = 0; prefabIndex < monsterPrefabs.Length; prefabIndex++)
            {
                var entry = monsterPrefabs[prefabIndex];
                var weight = MapCellUtility.NormalizeWeight(entry.Weight);

                if (entry.Prefab == null || weight <= 0f)
                {
                    continue;
                }

                monsterPrefabBuffer.Add(new MonsterSpawnPrefabElement
                {
                    Prefab = GetEntity(entry.Prefab, TransformUsageFlags.Dynamic),
                    Weight = weight
                });
            }
        }
    }
}

/// <summary>
/// Inspector で編集する monster prefab と抽選重み。
/// </summary>
[System.Serializable]
public struct MonsterSpawnPrefabEntry
{
    [Tooltip("Monster prefab candidate. Use a prefab with MonsterEntity authoring.")]
    public GameObject Prefab;

    [Min(0f)]
    [Tooltip("Relative selection weight. 0 disables this candidate.")]
    public float Weight;
}

/// <summary>
/// Monster spawn director が使う位置計算。
/// </summary>
public static class MonsterSpawnDirectorUtility
{
    /// <summary>
    /// Player 周辺の spawn ring 内に 1 点を生成する。
    /// </summary>
    public static float3 CalculateSpawnPosition(
        float3 playerPosition,
        float spawnRadius,
        ref Unity.Mathematics.Random random)
    {
        var safeSpawnRadius = math.max(0.1f, math.abs(spawnRadius));
        var angle = random.NextFloat(0f, math.PI * 2f);
        var radius = random.NextFloat(safeSpawnRadius * 0.7f, safeSpawnRadius);
        var direction = new float2(math.cos(angle), math.sin(angle));

        return new float3(
            playerPosition.x + direction.x * radius,
            playerPosition.y,
            playerPosition.z + direction.y * radius);
    }

    /// <summary>
    /// Map Cell 内の spawn 可能範囲に 1 点を生成する。
    /// </summary>
    public static float3 CalculateCellSpawnPosition(
        float3 cellCenter,
        float cellSize,
        float padding,
        ref Unity.Mathematics.Random random)
    {
        var safeCellSize = math.max(0.1f, math.abs(cellSize));
        var safePadding = math.max(0f, math.abs(padding));
        var halfExtent = math.max(0f, safeCellSize * 0.5f - safePadding);
        var offset = random.NextFloat2(
            new float2(-halfExtent, -halfExtent),
            new float2(halfExtent, halfExtent));

        return cellCenter + new float3(offset.x, 0f, offset.y);
    }

    /// <summary>
    /// Player からの spawn 距離範囲を非負かつ min <= max に正規化する。
    /// </summary>
    public static void NormalizeSpawnDistanceRange(
        float minDistance,
        float maxDistance,
        out float normalizedMinDistance,
        out float normalizedMaxDistance)
    {
        var safeMinDistance = math.max(0f, math.abs(minDistance));
        var safeMaxDistance = math.max(0f, math.abs(maxDistance));

        normalizedMinDistance = math.min(safeMinDistance, safeMaxDistance);
        normalizedMaxDistance = math.max(safeMinDistance, safeMaxDistance);
    }

    /// <summary>
    /// Player 周囲の XZ ring 内に 1 点を生成する。
    /// </summary>
    public static float3 CalculatePlayerRingSpawnPosition(
        float3 playerPosition,
        float groundY,
        float minDistance,
        float maxDistance,
        ref Unity.Mathematics.Random random)
    {
        NormalizeSpawnDistanceRange(
            minDistance,
            maxDistance,
            out var normalizedMinDistance,
            out var normalizedMaxDistance);

        var angle = random.NextFloat(0f, math.PI * 2f);
        var minDistanceSq = normalizedMinDistance * normalizedMinDistance;
        var maxDistanceSq = normalizedMaxDistance * normalizedMaxDistance;
        var radius = math.sqrt(random.NextFloat(minDistanceSq, maxDistanceSq));
        var direction = new float2(math.cos(angle), math.sin(angle));

        return new float3(
            playerPosition.x + direction.x * radius,
            groundY,
            playerPosition.z + direction.y * radius);
    }

    /// <summary>
    /// GroundSensor の下端が地面に触れる root 位置を返す。
    /// </summary>
    public static float3 CalculateGroundedSpawnPosition(
        float3 groundPosition,
        GroundSensor groundSensor,
        quaternion rotation,
        float scale)
    {
        var safeScale = math.abs(scale);
        var sensorOffset = math.rotate(rotation, groundSensor.LocalCenter * scale);
        var sensorRadius = groundSensor.Radius * safeScale;

        groundPosition.y += sensorRadius - sensorOffset.y;

        return groundPosition;
    }

    /// <summary>
    /// Cell ごとの spawn count 範囲を非負かつ min <= max に正規化する。
    /// </summary>
    public static void NormalizeCellSpawnCountRange(
        int minCount,
        int maxCount,
        out int normalizedMinCount,
        out int normalizedMaxCount)
    {
        var safeMinCount = math.max(0, minCount);
        var safeMaxCount = math.max(0, maxCount);

        normalizedMinCount = math.min(safeMinCount, safeMaxCount);
        normalizedMaxCount = math.max(safeMinCount, safeMaxCount);
    }

    /// <summary>
    /// Cell ごとの spawn count を inclusive range から 1 つ選ぶ。
    /// </summary>
    public static int CalculateCellSpawnCount(
        int minCount,
        int maxCount,
        ref Unity.Mathematics.Random random)
    {
        NormalizeCellSpawnCountRange(
            minCount,
            maxCount,
            out var normalizedMinCount,
            out var normalizedMaxCount);

        if (normalizedMaxCount <= 0 || normalizedMinCount == normalizedMaxCount)
        {
            return normalizedMinCount;
        }

        return random.NextInt(normalizedMinCount, normalizedMaxCount + 1);
    }

    /// <summary>
    /// 正の weight を持つ monster prefab の合計 weight を返す。
    /// </summary>
    public static float CalculateTotalMonsterPrefabWeight(
        DynamicBuffer<MonsterSpawnPrefabElement> monsterPrefabs)
    {
        var totalWeight = 0f;

        for (var prefabIndex = 0; prefabIndex < monsterPrefabs.Length; prefabIndex++)
        {
            var entry = monsterPrefabs[prefabIndex];

            if (entry.Prefab == Entity.Null)
            {
                continue;
            }

            totalWeight += MapCellUtility.NormalizeWeight(entry.Weight);
        }

        return totalWeight;
    }

    /// <summary>
    /// 0 以上 totalWeight 未満の roll から monster prefab を選ぶ。
    /// </summary>
    public static Entity SelectMonsterPrefab(
        DynamicBuffer<MonsterSpawnPrefabElement> monsterPrefabs,
        float roll)
    {
        var totalWeight = CalculateTotalMonsterPrefabWeight(monsterPrefabs);

        if (totalWeight <= 0f)
        {
            return Entity.Null;
        }

        var clampedRoll = math.clamp(roll, 0f, totalWeight);
        var cumulativeWeight = 0f;
        var lastPositivePrefab = Entity.Null;

        for (var prefabIndex = 0; prefabIndex < monsterPrefabs.Length; prefabIndex++)
        {
            var entry = monsterPrefabs[prefabIndex];
            var weight = MapCellUtility.NormalizeWeight(entry.Weight);

            if (entry.Prefab == Entity.Null || weight <= 0f)
            {
                continue;
            }

            cumulativeWeight += weight;
            lastPositivePrefab = entry.Prefab;

            if (clampedRoll < cumulativeWeight)
            {
                return entry.Prefab;
            }
        }

        return lastPositivePrefab;
    }

    /// <summary>
    /// Monster prefab 候補から weight に従って 1 つ選ぶ。
    /// </summary>
    public static Entity SelectMonsterPrefab(
        DynamicBuffer<MonsterSpawnPrefabElement> monsterPrefabs,
        ref Unity.Mathematics.Random random)
    {
        var totalWeight = CalculateTotalMonsterPrefabWeight(monsterPrefabs);

        if (totalWeight <= 0f)
        {
            return Entity.Null;
        }

        return SelectMonsterPrefab(monsterPrefabs, random.NextFloat(totalWeight));
    }
}

/// <summary>
/// 新しく load された Map Cell に通常 monster を配置する。
/// </summary>
[UpdateAfter(typeof(MapCellSystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
public partial struct MonsterSpawnDirectorSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterSpawnDirectorConfig>();
        state.RequireForUpdate<MapCell>();
        state.RequireForUpdate<PlayerTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!TryGetPlayerPosition(ref state, out var playerPosition))
        {
            return;
        }

        var entityCommandBuffer = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

        foreach (var (config, monsterPrefabs) in
                 SystemAPI.Query<RefRO<MonsterSpawnDirectorConfig>, DynamicBuffer<MonsterSpawnPrefabElement>>())
        {
            if (config.ValueRO.MaxSpawnCountPerCell <= 0 ||
                monsterPrefabs.Length == 0)
            {
                continue;
            }

            SpawnMonstersForNewCells(
                ref state,
                ref entityCommandBuffer,
                config.ValueRO,
                monsterPrefabs,
                playerPosition);
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

    private void SpawnMonstersForNewCells(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        MonsterSpawnDirectorConfig config,
        DynamicBuffer<MonsterSpawnPrefabElement> monsterPrefabs,
        float3 playerPosition)
    {
        foreach (var (cell, cellTransform, cellEntity) in
                 SystemAPI.Query<RefRO<MapCell>, RefRO<LocalTransform>>()
                     .WithNone<MapCellMonsterSpawned>()
                     .WithEntityAccess())
        {
            if (!state.EntityManager.HasComponent<MapCellConfig>(cell.ValueRO.ConfigEntity))
            {
                continue;
            }

            var seed = MapCellUtility.CreateCellSeed(
                config.WorldSeed,
                cell.ValueRO.Coord);
            var random = new Unity.Mathematics.Random(seed);
            var spawnCount = MonsterSpawnDirectorUtility.CalculateCellSpawnCount(
                config.MinSpawnCountPerCell,
                config.MaxSpawnCountPerCell,
                ref random);

            for (var spawnIndex = 0; spawnIndex < spawnCount; spawnIndex++)
            {
                var monsterPrefab = MonsterSpawnDirectorUtility.SelectMonsterPrefab(
                    monsterPrefabs,
                    ref random);

                if (monsterPrefab == Entity.Null)
                {
                    continue;
                }

                var position = MonsterSpawnDirectorUtility.CalculatePlayerRingSpawnPosition(
                    playerPosition,
                    cellTransform.ValueRO.Position.y,
                    config.MinSpawnDistanceFromPlayer,
                    config.MaxSpawnDistanceFromPlayer,
                    ref random);
                var monster = entityCommandBuffer.Instantiate(monsterPrefab);
                var transform = CreateMonsterSpawnTransform(
                    ref state,
                    monsterPrefab,
                    position);

                entityCommandBuffer.SetComponent(
                    monster,
                    transform);
                entityCommandBuffer.AddComponent(monster, new MapCellOwnedEntity
                {
                    CellEntity = cellEntity
                });
            }

            entityCommandBuffer.AddComponent<MapCellMonsterSpawned>(cellEntity);
        }
    }

    private static LocalTransform CreateMonsterSpawnTransform(
        ref SystemState state,
        Entity monsterPrefab,
        float3 groundPosition)
    {
        var prefabTransform = LocalTransform.Identity;

        if (state.EntityManager.HasComponent<LocalTransform>(monsterPrefab))
        {
            prefabTransform = state.EntityManager.GetComponentData<LocalTransform>(monsterPrefab);
        }

        var position = groundPosition;

        if (state.EntityManager.HasComponent<GroundSensor>(monsterPrefab))
        {
            position = MonsterSpawnDirectorUtility.CalculateGroundedSpawnPosition(
                groundPosition,
                state.EntityManager.GetComponentData<GroundSensor>(monsterPrefab),
                prefabTransform.Rotation,
                prefabTransform.Scale);
        }

        return LocalTransform.FromPositionRotationScale(
            position,
            prefabTransform.Rotation,
            prefabTransform.Scale);
    }
}
