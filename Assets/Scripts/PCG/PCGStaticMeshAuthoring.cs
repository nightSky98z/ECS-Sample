using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// セルの Prefab 内に、木や岩などの静的メッシュを手続き的に（PCG で）配置する Authoring。
/// 配置は seed から決まるため、同じ設定なら毎回同じ配置になる。
/// 配置の計算は Bake 時に済ませて Buffer に保存しておき、ゲーム中は生成するだけにしている（実行時の計算コストを減らすため）。
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

    /// <summary>
    /// Inspector で入力された値を、有効な範囲に収める。
    /// </summary>
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

    /// <summary>
    /// 古い形式（Prefab の配列のみ）の設定を、新しい形式（候補と重み）に移す。
    /// </summary>
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

    /// <summary>
    /// 実際に使う候補を返す（古い形式の設定しかない場合も考慮する）。
    /// </summary>
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

    /// <summary>
    /// 重みに比例した確率で Prefab を 1 つ選ぶ（エディタのプレビュー用）。
    /// </summary>
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
    /// 配置を計算し、セル内のローカル座標の一覧（PCGStaticMeshLocalInstance の Buffer）として保存する。
    /// ゲーム中は PCGStaticMeshLocalSpawnSystem がこの一覧をもとに生成する。
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
/// Inspector で編集する、配置する Prefab と選ばれやすさ（重み）。
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
/// Bake とプレビューで使う、有効な候補（Prefab と重み）。
/// </summary>
public struct PCGStaticMeshPrefabCandidate
{
    /// <summary>配置する Prefab。</summary>
    public GameObject Prefab;

    /// <summary>選ばれやすさ（重み）。</summary>
    public float Weight;
}

/// <summary>
/// セル内に配置する静的メッシュ 1 つ分の情報（セルからの相対位置）。Bake 時に作られ、セルの Entity に保存される。
/// </summary>
[InternalBufferCapacity(32)]
public struct PCGStaticMeshLocalInstance : IBufferElementData
{
    /// <summary>生成する Prefab。</summary>
    public Entity Prefab;

    /// <summary>セルから見た位置。</summary>
    public float3 LocalPosition;

    /// <summary>セルから見た回転。</summary>
    public quaternion LocalRotation;

    /// <summary>大きさ（全軸共通）。</summary>
    public float Scale;
}

/// <summary>
/// 配置を計算するための設定（配置する範囲と、大きさ・回転のばらつき）。
/// </summary>
public struct PCGStaticMeshPlacementSettings
{
    /// <summary>配置する範囲の中心。</summary>
    public float3 Center;

    /// <summary>配置する範囲の回転。</summary>
    public quaternion Rotation;

    /// <summary>配置する範囲の大きさ（XZ）。</summary>
    public float2 AreaSize;

    /// <summary>大きさの範囲（最小, 最大）。</summary>
    public float2 ScaleRange;

    /// <summary>true なら Y 軸回りにランダムに回転させる。</summary>
    public bool RandomizeYaw;
}

/// <summary>
/// 1 つ分の配置の計算結果。
/// </summary>
public struct PCGStaticMeshPlacement
{
    /// <summary>位置。</summary>
    public float3 Position;

    /// <summary>回転。</summary>
    public quaternion Rotation;

    /// <summary>大きさ（全軸共通）。</summary>
    public float Scale;
}

/// <summary>
/// Unity Physics の Collider に渡す位置・回転・大きさ（Unity Physics は全軸共通の大きさしか扱えない）。
/// </summary>
public struct PCGStaticMeshPhysicsTransform
{
    /// <summary>位置。</summary>
    public float3 Position;

    /// <summary>回転。</summary>
    public quaternion Rotation;

    /// <summary>大きさ（全軸共通）。</summary>
    public float Scale;
}

/// <summary>
/// 配置の計算処理。Bake とエディタのプレビューで同じ計算を使い、プレビューと実際の配置が一致するようにしている。
/// </summary>
public static class PCGStaticMeshUtility
{
    private const float MinPhysicsScale = 0.000001f;

    /// <summary>
    /// 生成数を 0 以上に補正する。
    /// </summary>
    public static int GetBakeInstanceCount(int instanceCount)
    {
        return math.max(0, instanceCount);
    }

    /// <summary>
    /// seed を 0 以外の値にする（Unity.Mathematics.Random は 0 を受け付けないため）。
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
    /// 大きさの範囲を、0 以上かつ「最小 ≦ 最大」になるように補正する。
    /// </summary>
    public static float2 NormalizeScaleRange(float2 scaleRange)
    {
        var min = math.abs(scaleRange.x);
        var max = math.abs(scaleRange.y);

        return new float2(math.min(min, max), math.max(min, max));
    }

    /// <summary>
    /// 重みを 0 以上に補正する。
    /// </summary>
    public static float NormalizeWeight(float weight)
    {
        return math.max(0f, weight);
    }

    /// <summary>
    /// 重みの合計を返す（0 以下の重みは無視する）。
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
    /// 0 以上・重みの合計未満の乱数値から、対応する候補の番号を選ぶ。
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
    /// 乱数を使って、重みに比例した確率で候補の番号を選ぶ。
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
    /// Inspector の設定値から、配置計算用の設定を作る。
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
    /// ワールド座標から、その位置を含むセルの座標を返す。
    /// </summary>
    public static int2 CalculateCellCoord(float3 position, float cellSize)
    {
        var safeCellSize = math.max(0.0001f, math.abs(cellSize));

        return new int2(
            (int)math.floor(position.x / safeCellSize),
            (int)math.floor(position.z / safeCellSize));
    }

    /// <summary>
    /// セルが指定した半径内にあるかを返す（チェビシェフ距離）。
    /// </summary>
    public static bool IsInsideCellRadius(int2 center, int2 coord, int radius)
    {
        var safeRadius = math.max(0, radius);
        var delta = math.abs(coord - center);

        return math.max(delta.x, delta.y) <= safeRadius;
    }

    /// <summary>
    /// セルの座標とワールドの seed から、セルごとの seed を作る（同じ座標なら常に同じ値）。
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
    /// セル 1 つ分の範囲を配置範囲とする設定を作る。
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
    /// 乱数を使って、1 つ分の配置（位置・回転・大きさ）を決める。
    /// </summary>
    /// <param name="settings">配置の設定。</param>
    /// <param name="random">乱数。呼び出すたびに状態が進む。</param>
    /// <returns>位置・回転・大きさ。</returns>
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
    /// セルのワールド座標と、セルから見た相対的な配置から、生成する静的メッシュのワールド座標での Transform を作る。
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
    /// LODGroup の「画面に対する高さの割合」から、Entities Graphics で使う切り替え距離を計算する。
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
    /// 変換行列の各軸の大きさのうち、最も大きいものを返す（Unity Physics は全軸共通の大きさしか扱えないため）。
    /// </summary>
    public static float GetMaxAbsAxisScale(Matrix4x4 matrix)
    {
        var xAxis = new float3(matrix.m00, matrix.m10, matrix.m20);
        var yAxis = new float3(matrix.m01, matrix.m11, matrix.m21);
        var zAxis = new float3(matrix.m02, matrix.m12, matrix.m22);

        return math.max(math.length(xAxis), math.max(math.length(yAxis), math.length(zAxis)));
    }

    /// <summary>
    /// 変換行列を、Collider 用の位置・回転・大きさに分解する。
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
    /// メッシュの頂点が読み取れない場合に使う、メッシュ全体を囲む箱型の Collider の形を作る。
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
    /// CapsuleCollider の設定（中心・半径・高さ・向き）から、Unity Physics のカプセル型の形を作る。
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

    /// <summary>
    /// CapsuleCollider.direction（0 = X, 1 = Y, 2 = Z）を軸のベクトルに変換する。
    /// </summary>
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
