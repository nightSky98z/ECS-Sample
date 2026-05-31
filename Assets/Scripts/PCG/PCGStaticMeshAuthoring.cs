using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// Cell prefab 内に、壊れない装飾用 static mesh の local 配置を seed 付きで作る Authoring。
/// </summary>
public partial class PCGStaticMeshAuthoring : MonoBehaviour
{
    [SerializeField]
    [Tooltip("スタティックメッシュのプレハブ候補と相対的な重み。重みが 0 以下の要素は無視される。")]
    private PCGStaticMeshPrefabEntry[] StaticMeshEntries = new PCGStaticMeshPrefabEntry[0];

    [SerializeField]
    [HideInInspector]
    [Tooltip("旧形式のプレハブ配列。StaticMeshEntries が空の場合、重み 1 の候補として扱う。")]
    private GameObject[] StaticMeshPrefabs = new GameObject[0];

    [SerializeField]
    [Min(0)]
    [Tooltip("このセルプレハブ内に生成するスタティックメッシュ数。0 は生成無効。")]
    private int InstanceCount = 32;

    [SerializeField]
    [Tooltip("このセルプレハブ用のローカル PCG シード。")]
    private int RandomSeed = 1;

    [SerializeField]
    [Tooltip("生成するスタティックメッシュのルートに使うローカル XZ 範囲。")]
    private Vector2 AreaSize = new Vector2(100f, 100f);

    [SerializeField]
    [Tooltip("生成するスタティックメッシュのルートに使うローカル Y 座標。")]
    private float GroundY = 0f;

    [SerializeField]
    [Tooltip("配置ごとの均一スケール範囲。負の値は絶対値に正規化される。")]
    private Vector2 ScaleRange = new Vector2(1f, 1f);

    [SerializeField]
    [Tooltip("有効にすると各配置にランダムなヨー回転を加える。")]
    private bool RandomizeYaw = true;

    private void OnValidate()
    {
        MigrateLegacyStaticMeshPrefabs();
        NormalizeStaticMeshEntryWeights();

        InstanceCount = math.max(0, InstanceCount);
        RandomSeed = (int)PCGStaticMeshUtility.NormalizeSeed(RandomSeed);
        AreaSize = new Vector2(math.max(0f, math.abs(AreaSize.x)), math.max(0f, math.abs(AreaSize.y)));

        var scaleRange = PCGStaticMeshUtility.NormalizeScaleRange(new float2(ScaleRange.x, ScaleRange.y));
        ScaleRange = new Vector2(scaleRange.x, scaleRange.y);
    }

    private void MigrateLegacyStaticMeshPrefabs()
    {
        if ((StaticMeshEntries != null && StaticMeshEntries.Length > 0) ||
            StaticMeshPrefabs == null ||
            StaticMeshPrefabs.Length == 0)
        {
            return;
        }

        StaticMeshEntries = CreateLegacyEntries(StaticMeshPrefabs);
    }

    private void NormalizeStaticMeshEntryWeights()
    {
        if (StaticMeshEntries == null)
        {
            return;
        }

        for (var entryIndex = 0; entryIndex < StaticMeshEntries.Length; entryIndex++)
        {
            var entry = StaticMeshEntries[entryIndex];

            entry.Weight = PCGStaticMeshUtility.NormalizeWeight(entry.Weight);
            StaticMeshEntries[entryIndex] = entry;
        }
    }

    private PCGStaticMeshPrefabEntry[] GetEffectiveStaticMeshEntries()
    {
        if (StaticMeshEntries != null && StaticMeshEntries.Length > 0)
        {
            return StaticMeshEntries;
        }

        if (StaticMeshPrefabs == null || StaticMeshPrefabs.Length == 0)
        {
            return StaticMeshEntries;
        }

        return CreateLegacyEntries(StaticMeshPrefabs);
    }

    private static PCGStaticMeshPrefabEntry[] CreateLegacyEntries(GameObject[] prefabs)
    {
        var entries = new PCGStaticMeshPrefabEntry[prefabs.Length];

        for (var prefabIndex = 0; prefabIndex < prefabs.Length; prefabIndex++)
        {
            entries[prefabIndex] = new PCGStaticMeshPrefabEntry
            {
                Prefab = prefabs[prefabIndex],
                Weight = 1f
            };
        }

        return entries;
    }

    private static GameObject SelectWeightedPrefab(
        List<PCGStaticMeshPrefabCandidate> prefabs,
        ref Unity.Mathematics.Random random)
    {
        var totalWeight = 0f;

        for (var prefabIndex = 0; prefabIndex < prefabs.Count; prefabIndex++)
        {
            totalWeight += PCGStaticMeshUtility.NormalizeWeight(prefabs[prefabIndex].Weight);
        }

        if (totalWeight <= 0f)
        {
            return null;
        }

        var roll = random.NextFloat(totalWeight);
        var cumulativeWeight = 0f;
        GameObject lastPositivePrefab = null;

        for (var prefabIndex = 0; prefabIndex < prefabs.Count; prefabIndex++)
        {
            var prefab = prefabs[prefabIndex];
            var weight = PCGStaticMeshUtility.NormalizeWeight(prefab.Weight);

            if (weight <= 0f)
            {
                continue;
            }

            cumulativeWeight += weight;
            lastPositivePrefab = prefab.Prefab;

            if (roll < cumulativeWeight)
            {
                return prefab.Prefab;
            }
        }

        return lastPositivePrefab;
    }

    /// <summary>
    /// PCGStaticMeshAuthoring の設定を、Cell instance が展開する local 配置バッファへ変換する。
    /// </summary>
    private sealed class PCGStaticMeshBaker : Baker<PCGStaticMeshAuthoring>
    {
        public override void Bake(PCGStaticMeshAuthoring authoring)
        {
            var entries = authoring.GetEffectiveStaticMeshEntries();
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            var validPrefabs = new List<Entity>();
            var weights = new List<float>();

            if (entries == null)
            {
                return;
            }

            for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                var entry = entries[entryIndex];
                var weight = PCGStaticMeshUtility.NormalizeWeight(entry.Weight);

                if (entry.Prefab == null || weight <= 0f)
                {
                    continue;
                }

                validPrefabs.Add(GetEntity(entry.Prefab, TransformUsageFlags.Dynamic));
                weights.Add(weight);
            }

            if (validPrefabs.Count == 0)
            {
                return;
            }

            var instanceBuffer = AddBuffer<PCGStaticMeshLocalInstance>(entity);
            var random = new Unity.Mathematics.Random(
                PCGStaticMeshUtility.NormalizeSeed(authoring.RandomSeed));
            var settings = PCGStaticMeshUtility.CreatePlacementSettings(
                new float3(0f, authoring.GroundY, 0f),
                quaternion.identity,
                new float2(authoring.AreaSize.x, authoring.AreaSize.y),
                new float2(authoring.ScaleRange.x, authoring.ScaleRange.y),
                authoring.RandomizeYaw);
            var weightArray = weights.ToArray();
            var instanceCount = PCGStaticMeshUtility.GetBakeInstanceCount(authoring.InstanceCount);

            for (var instanceIndex = 0; instanceIndex < instanceCount; instanceIndex++)
            {
                var prefabIndex = PCGStaticMeshUtility.SelectWeightedIndex(weightArray, ref random);

                if (prefabIndex < 0)
                {
                    continue;
                }

                var placement = PCGStaticMeshUtility.CreatePlacement(settings, ref random);

                instanceBuffer.Add(new PCGStaticMeshLocalInstance
                {
                    Prefab = validPrefabs[prefabIndex],
                    LocalPosition = placement.Position,
                    LocalRotation = placement.Rotation,
                    Scale = placement.Scale
                });
            }
        }
    }
}

/// <summary>
/// Inspector で編集する PCG static mesh prefab と抽選重み。
/// </summary>
[System.Serializable]
public struct PCGStaticMeshPrefabEntry
{
    [Tooltip("配置するプレハブ候補。描画可能なメッシュを持つプレハブを使う。")]
    public GameObject Prefab;

    [Min(0f)]
    [Tooltip("相対的な選択重み。0 にするとこの候補は無効になる。")]
    public float Weight;
}

/// <summary>
/// Bake / preview 内部で使う有効 prefab 候補。
/// </summary>
public struct PCGStaticMeshPrefabCandidate
{
    public GameObject Prefab;
    public float Weight;
}

/// <summary>
/// Runtime で Cell instance が展開する local static mesh 配置。
/// </summary>
[InternalBufferCapacity(32)]
public struct PCGStaticMeshLocalInstance : IBufferElementData
{
    public Entity Prefab;
    public float3 LocalPosition;
    public quaternion LocalRotation;
    public float Scale;
}

/// <summary>
/// PCG static mesh の 1 配置分の入力。
/// </summary>
public struct PCGStaticMeshPlacementSettings
{
    public float3 Center;
    public quaternion Rotation;
    public float2 AreaSize;
    public float2 ScaleRange;
    public bool RandomizeYaw;
}

/// <summary>
/// PCG static mesh の 1 配置分の結果。
/// </summary>
public struct PCGStaticMeshPlacement
{
    public float3 Position;
    public quaternion Rotation;
    public float Scale;
}

/// <summary>
/// Unity Physics の static body に渡す rigid transform と uniform scale。
/// </summary>
public struct PCGStaticMeshPhysicsTransform
{
    public float3 Position;
    public quaternion Rotation;
    public float Scale;
}

/// <summary>
/// PCG static mesh の Bake と preview が共有する配置計算。
/// </summary>
public static class PCGStaticMeshUtility
{
    private const float MinPhysicsScale = 0.000001f;

    /// <summary>
    /// 生成数を bake に使える非負値へ正規化する。
    /// </summary>
    public static int GetBakeInstanceCount(int instanceCount)
    {
        return math.max(0, instanceCount);
    }

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
    /// scale 範囲を昇順の非負値へ正規化する。
    /// </summary>
    public static float2 NormalizeScaleRange(float2 scaleRange)
    {
        var min = math.abs(scaleRange.x);
        var max = math.abs(scaleRange.y);

        return new float2(math.min(min, max), math.max(min, max));
    }

    /// <summary>
    /// Prefab 抽選 weight を非負値へ正規化する。
    /// </summary>
    public static float NormalizeWeight(float weight)
    {
        return math.max(0f, weight);
    }

    /// <summary>
    /// 重み付き抽選に使う正の weight 合計を返す。
    /// </summary>
    public static float CalculateTotalWeight(float[] weights)
    {
        if (weights == null)
        {
            return 0f;
        }

        var totalWeight = 0f;

        for (var weightIndex = 0; weightIndex < weights.Length; weightIndex++)
        {
            totalWeight += NormalizeWeight(weights[weightIndex]);
        }

        return totalWeight;
    }

    /// <summary>
    /// 0 以上 totalWeight 未満の roll から、正の weight を持つ index を選ぶ。
    /// </summary>
    public static int SelectWeightedIndex(float[] weights, float roll)
    {
        var totalWeight = CalculateTotalWeight(weights);

        if (weights == null || totalWeight <= 0f)
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
    /// random state を進めて、正の weight を持つ index を選ぶ。
    /// </summary>
    public static int SelectWeightedIndex(
        float[] weights,
        ref Unity.Mathematics.Random random)
    {
        var totalWeight = CalculateTotalWeight(weights);

        if (totalWeight <= 0f)
        {
            return -1;
        }

        return SelectWeightedIndex(weights, random.NextFloat(totalWeight));
    }

    /// <summary>
    /// Authoring 値から配置計算用の設定を作る。
    /// </summary>
    public static PCGStaticMeshPlacementSettings CreatePlacementSettings(
        float3 center,
        quaternion rotation,
        float2 areaSize,
        float2 scaleRange,
        bool randomizeYaw)
    {
        return new PCGStaticMeshPlacementSettings
        {
            Center = center,
            Rotation = rotation,
            AreaSize = math.abs(areaSize),
            ScaleRange = NormalizeScaleRange(scaleRange),
            RandomizeYaw = randomizeYaw
        };
    }

    /// <summary>
    /// world 位置から、所属する XZ cell 座標を返す。
    /// </summary>
    public static int2 CalculateCellCoord(float3 position, float cellSize)
    {
        var safeCellSize = math.max(0.0001f, math.abs(cellSize));

        return new int2(
            (int)math.floor(position.x / safeCellSize),
            (int)math.floor(position.z / safeCellSize));
    }

    /// <summary>
    /// Chebyshev 距離で cell 半径内かどうかを返す。
    /// </summary>
    public static bool IsInsideCellRadius(int2 center, int2 coord, int radius)
    {
        var safeRadius = math.max(0, radius);
        var delta = math.abs(coord - center);

        return math.max(delta.x, delta.y) <= safeRadius;
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
    /// 1 cell 内の配置計算設定を作る。
    /// </summary>
    public static PCGStaticMeshPlacementSettings CreateCellPlacementSettings(
        int2 coord,
        float cellSize,
        float groundY,
        float2 scaleRange,
        bool randomizeYaw)
    {
        var safeCellSize = math.max(0.0001f, math.abs(cellSize));
        var origin = new float3(coord.x * safeCellSize, groundY, coord.y * safeCellSize);
        var center = origin + new float3(safeCellSize * 0.5f, 0f, safeCellSize * 0.5f);

        return CreatePlacementSettings(
            center,
            quaternion.identity,
            new float2(safeCellSize, safeCellSize),
            scaleRange,
            randomizeYaw);
    }

    /// <summary>
    /// seed 付き random から 1 配置分の transform を作る。
    /// </summary>
    /// <param name="settings">Authoring から作った配置設定。</param>
    /// <param name="random">呼び出し側が所有する random state。呼び出しごとに前進する。</param>
    /// <returns>配置位置、回転、uniform scale。</returns>
    public static PCGStaticMeshPlacement CreatePlacement(
        PCGStaticMeshPlacementSettings settings,
        ref Unity.Mathematics.Random random)
    {
        var halfAreaSize = settings.AreaSize * 0.5f;
        var offset = random.NextFloat2(-halfAreaSize, halfAreaSize);
        var localOffset = new float3(offset.x, 0f, offset.y);
        var position = settings.Center + math.rotate(settings.Rotation, localOffset);
        var scaleRange = NormalizeScaleRange(settings.ScaleRange);
        var scale = scaleRange.x == scaleRange.y
            ? scaleRange.x
            : random.NextFloat(scaleRange.x, scaleRange.y);
        var rotation = settings.Rotation;

        if (settings.RandomizeYaw)
        {
            rotation = math.mul(rotation, quaternion.RotateY(random.NextFloat(0f, math.PI * 2f)));
        }

        return new PCGStaticMeshPlacement
        {
            Position = position,
            Rotation = rotation,
            Scale = scale
        };
    }

    /// <summary>
    /// Cell root の world 位置と local PCG 配置から、spawn する static mesh の world transform を作る。
    /// </summary>
    public static LocalTransform CreateWorldPlacementTransform(
        LocalTransform cellTransform,
        PCGStaticMeshLocalInstance localInstance)
    {
        return LocalTransform.FromPositionRotationScale(
            cellTransform.Position + math.rotate(cellTransform.Rotation, localInstance.LocalPosition),
            math.mul(cellTransform.Rotation, localInstance.LocalRotation),
            localInstance.Scale);
    }

    /// <summary>
    /// LODGroup の screenRelativeTransitionHeight から Entities Graphics 用の距離を作る。
    /// </summary>
    public static float CalculateLodDistance(
        float worldSpaceSize,
        float screenRelativeTransitionHeight)
    {
        if (worldSpaceSize <= 0f || screenRelativeTransitionHeight <= 0f)
        {
            return float.PositiveInfinity;
        }

        return worldSpaceSize / screenRelativeTransitionHeight;
    }

    /// <summary>
    /// TRS 行列の各軸スケールから、Unity Physics が扱える uniform scale を選ぶ。
    /// </summary>
    public static float GetMaxAbsAxisScale(Matrix4x4 matrix)
    {
        var xAxis = new float3(matrix.m00, matrix.m10, matrix.m20);
        var yAxis = new float3(matrix.m01, matrix.m11, matrix.m21);
        var zAxis = new float3(matrix.m02, matrix.m12, matrix.m22);

        return math.max(math.length(xAxis), math.max(math.length(yAxis), math.length(zAxis)));
    }

    /// <summary>
    /// MeshCollider 用の LocalTransform へ、配置後の行列を分解する。
    /// </summary>
    public static PCGStaticMeshPhysicsTransform DecomposePhysicsTransform(Matrix4x4 matrix)
    {
        var position = new float3(matrix.m03, matrix.m13, matrix.m23);
        var scale = GetMaxAbsAxisScale(matrix);

        if (scale <= MinPhysicsScale)
        {
            scale = 1f;
        }

        var forward = new Vector3(matrix.m02, matrix.m12, matrix.m22);
        var up = new Vector3(matrix.m01, matrix.m11, matrix.m21);
        var rotation = Quaternion.identity;

        if (forward.sqrMagnitude > MinPhysicsScale && up.sqrMagnitude > MinPhysicsScale)
        {
            rotation = Quaternion.LookRotation(forward.normalized, up.normalized);
        }

        return new PCGStaticMeshPhysicsTransform
        {
            Position = position,
            Rotation = new quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
            Scale = scale
        };
    }

    /// <summary>
    /// Mesh bounds から non-readable mesh 用の conservative box collider geometry を作る。
    /// </summary>
    public static Unity.Physics.BoxGeometry CreateBoxGeometry(Bounds bounds)
    {
        return new Unity.Physics.BoxGeometry
        {
            Center = bounds.center,
            Orientation = quaternion.identity,
            Size = math.max((float3)bounds.size, new float3(MinPhysicsScale)),
            BevelRadius = 0f
        };
    }

    /// <summary>
    /// UnityEngine.CapsuleCollider の center/radius/height/direction から Unity Physics の capsule geometry を作る。
    /// </summary>
    public static Unity.Physics.CapsuleGeometry CreateCapsuleGeometry(
        float3 center,
        float radius,
        float height,
        int direction)
    {
        var safeRadius = math.abs(radius);
        var safeHeight = math.max(math.abs(height), safeRadius * 2f);
        var halfSegmentLength = math.max(0f, safeHeight * 0.5f - safeRadius);
        var axis = GetCapsuleAxis(direction);

        return new Unity.Physics.CapsuleGeometry
        {
            Vertex0 = center - axis * halfSegmentLength,
            Vertex1 = center + axis * halfSegmentLength,
            Radius = safeRadius
        };
    }

    private static float3 GetCapsuleAxis(int direction)
    {
        if (direction == 0)
        {
            return new float3(1f, 0f, 0f);
        }

        if (direction == 2)
        {
            return new float3(0f, 0f, 1f);
        }

        return new float3(0f, 1f, 0f);
    }
}
