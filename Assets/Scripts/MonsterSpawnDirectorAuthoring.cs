using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Player 周辺に通常 monster を維持する runtime spawn director の設定。
/// </summary>
public struct MonsterSpawnDirectorConfig : IComponentData
{
    public float SpawnIntervalSeconds;
    public int SpawnCountPerInterval;
    public int MaxAliveMonsterCount;
    public int NearbyMonsterTargetCount;
    public int NearbyMonsterLowThreshold;
    public float NearbyMonsterRadius;
    public float MinSpawnDistanceFromPlayer;
    public float MaxSpawnDistanceFromPlayer;
    public float ForwardSpawnBias;
    public float RecycleDistanceFromPlayer;
    public float RecycleCheckIntervalSeconds;
    public int MaxRecycleChecksPerFrame;
    public int WorldSeed;
}

/// <summary>
/// ステージ進行度に応じて monster 補充設定を差し替えるデータ。
/// </summary>
[InternalBufferCapacity(8)]
public struct MonsterSpawnStageTuningElement : IBufferElementData
{
    public float MinStageProgress;
    public float SpawnIntervalSeconds;
    public int SpawnCountPerInterval;
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
/// 通常 monster を player 周辺だけに維持する Authoring。
/// </summary>
public sealed class MonsterSpawnDirectorAuthoring : MonoBehaviour
{
    [SerializeField]
    [Tooltip("時間スポーンで生成するモンスタープレハブ候補。重みが 0 以下の要素は無視される。")]
    private MonsterSpawnPrefabEntry[] MonsterPrefabs = new MonsterSpawnPrefabEntry[0];

    [SerializeField]
    [HideInInspector]
    [Tooltip("旧形式の単一モンスタープレハブ。MonsterPrefabs が空の場合、重み 1 の候補として扱う。")]
    private GameObject MonsterPrefab;

    [SerializeField]
    [Min(0f)]
    [Tooltip("時間スポーンのバッチ間隔秒。0 は毎フレーム生成。")]
    private float SpawnIntervalSeconds = 1f;

    [SerializeField]
    [Min(0)]
    [Tooltip("1 回の時間スポーンバッチで生成するモンスター数。0 は時間スポーン無効。")]
    private int SpawnCountPerInterval = 8;

    [SerializeField]
    [Min(0)]
    [Tooltip("このディレクターが制御する通常モンスターの最大生存数。0 は時間スポーン無効。")]
    private int MaxAliveMonsterCount = 400;

    [SerializeField]
    [Min(0)]
    [Tooltip("プレイヤー周辺に維持したい通常モンスター数。プレイヤーが逃げた後の戦闘密度補充に使う。")]
    private int NearbyMonsterTargetCount = 80;

    [SerializeField]
    [Min(0)]
    [Tooltip("周辺モンスター数がこの値を下回ると、遠距離の通常モンスターをプレイヤー近くへ再配置する。")]
    private int NearbyMonsterLowThreshold = 50;

    [SerializeField]
    [Min(0f)]
    [Tooltip("周辺通常モンスター数を数える XZ 半径。最大スポーン距離以上にする。")]
    private float NearbyMonsterRadius = 48f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("プレイヤーからの最小 XZ 距離。画面外スポーン半径として使う。")]
    private float MinSpawnDistanceFromPlayer = 18f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("プレイヤーからの最大 XZ 距離。ロード済みマップセル範囲内に収める。")]
    private float MaxSpawnDistanceFromPlayer = 42f;

    [SerializeField]
    [Range(0f, 1f)]
    [Tooltip("0 はプレイヤー周囲へ完全ランダム生成。1 はプレイヤー前方半空間だけに生成。")]
    private float ForwardSpawnBias = 0.65f;

    [SerializeField]
    [Min(0f)]
    [FormerlySerializedAs("DespawnDistanceFromPlayer")]
    [Tooltip("プレイヤーからこの XZ 距離より遠いモンスターを新しい画面外位置へ再配置する。0 は距離再配置無効。")]
    private float RecycleDistanceFromPlayer = 96f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("遠距離モンスター再配置スキャンの間隔秒。0 は毎フレーム確認。")]
    private float RecycleCheckIntervalSeconds = 0.2f;

    [SerializeField]
    [Min(1)]
    [Tooltip("1 回の再配置スキャンで検査する最大モンスター数。フレーム時間安定のため小さめにする。")]
    private int MaxRecycleChecksPerFrame = 100;

    [SerializeField]
    [Tooltip("決定的な時間スポーン配置を作るためのワールドシード。")]
    private int RandomSeed = 1;

    [SerializeField]
    [Tooltip("ステージ進行度で選ぶ実行時の時間スポーン設定。到達済みの MinStageProgress が最も高い設定を使う。")]
    private MonsterSpawnStageTuningEntry[] StageTunings = new MonsterSpawnStageTuningEntry[0];

    private void OnValidate()
    {
        MigrateLegacyMonsterPrefab();
        NormalizeMonsterPrefabWeights();
        NormalizeStageTunings();
        SpawnIntervalSeconds =
            MonsterSpawnDirectorUtility.NormalizeSpawnInterval(SpawnIntervalSeconds);
        SpawnCountPerInterval = MonsterSpawnDirectorUtility.NormalizeSpawnCount(SpawnCountPerInterval);
        MaxAliveMonsterCount = MonsterSpawnDirectorUtility.NormalizeSpawnCount(MaxAliveMonsterCount);
        MonsterSpawnDirectorUtility.NormalizeSpawnDistanceRange(
            MinSpawnDistanceFromPlayer,
            MaxSpawnDistanceFromPlayer,
            out MinSpawnDistanceFromPlayer,
            out MaxSpawnDistanceFromPlayer);
        NearbyMonsterTargetCount = MonsterSpawnDirectorUtility.NormalizeSpawnCount(NearbyMonsterTargetCount);
        NearbyMonsterLowThreshold = MonsterSpawnDirectorUtility.NormalizeNearbyLowThreshold(
            NearbyMonsterLowThreshold,
            NearbyMonsterTargetCount);
        NearbyMonsterRadius = MonsterSpawnDirectorUtility.NormalizeNearbyMonsterRadius(
            NearbyMonsterRadius,
            MaxSpawnDistanceFromPlayer);
        ForwardSpawnBias = MonsterSpawnDirectorUtility.NormalizeForwardSpawnBias(ForwardSpawnBias);
        RecycleDistanceFromPlayer = MonsterSpawnDirectorUtility.NormalizeRecycleDistance(
            RecycleDistanceFromPlayer,
            MaxSpawnDistanceFromPlayer);
        RecycleCheckIntervalSeconds =
            MonsterSpawnDirectorUtility.NormalizeSpawnInterval(RecycleCheckIntervalSeconds);
        MaxRecycleChecksPerFrame =
            MonsterSpawnDirectorUtility.NormalizeRecycleChecksPerFrame(MaxRecycleChecksPerFrame);
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

    private void NormalizeStageTunings()
    {
        if (StageTunings == null)
        {
            return;
        }

        for (var tuningIndex = 0; tuningIndex < StageTunings.Length; tuningIndex++)
        {
            var tuning = StageTunings[tuningIndex];

            tuning.MinStageProgress = MonsterSpawnDirectorUtility.NormalizeStageProgress(tuning.MinStageProgress);
            tuning.SpawnIntervalSeconds = MonsterSpawnDirectorUtility.NormalizeSpawnInterval(tuning.SpawnIntervalSeconds);
            tuning.SpawnCountPerInterval = MonsterSpawnDirectorUtility.NormalizeSpawnCount(tuning.SpawnCountPerInterval);
            StageTunings[tuningIndex] = tuning;
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

            AddComponent(entity, new MonsterSpawnDirectorConfig
            {
                SpawnIntervalSeconds = authoring.SpawnIntervalSeconds,
                SpawnCountPerInterval = authoring.SpawnCountPerInterval,
                MaxAliveMonsterCount = authoring.MaxAliveMonsterCount,
                NearbyMonsterTargetCount = authoring.NearbyMonsterTargetCount,
                NearbyMonsterLowThreshold = MonsterSpawnDirectorUtility.NormalizeNearbyLowThreshold(
                    authoring.NearbyMonsterLowThreshold,
                    authoring.NearbyMonsterTargetCount),
                NearbyMonsterRadius = MonsterSpawnDirectorUtility.NormalizeNearbyMonsterRadius(
                    authoring.NearbyMonsterRadius,
                    authoring.MaxSpawnDistanceFromPlayer),
                MinSpawnDistanceFromPlayer = authoring.MinSpawnDistanceFromPlayer,
                MaxSpawnDistanceFromPlayer = authoring.MaxSpawnDistanceFromPlayer,
                ForwardSpawnBias = MonsterSpawnDirectorUtility.NormalizeForwardSpawnBias(authoring.ForwardSpawnBias),
                RecycleDistanceFromPlayer = MonsterSpawnDirectorUtility.NormalizeRecycleDistance(
                    authoring.RecycleDistanceFromPlayer,
                    authoring.MaxSpawnDistanceFromPlayer),
                RecycleCheckIntervalSeconds = authoring.RecycleCheckIntervalSeconds,
                MaxRecycleChecksPerFrame = authoring.MaxRecycleChecksPerFrame,
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

            var stageTuningBuffer = AddBuffer<MonsterSpawnStageTuningElement>(entity);

            if (authoring.StageTunings == null)
            {
                return;
            }

            for (var tuningIndex = 0; tuningIndex < authoring.StageTunings.Length; tuningIndex++)
            {
                var tuning = authoring.StageTunings[tuningIndex];

                stageTuningBuffer.Add(new MonsterSpawnStageTuningElement
                {
                    MinStageProgress = MonsterSpawnDirectorUtility.NormalizeStageProgress(tuning.MinStageProgress),
                    SpawnIntervalSeconds = MonsterSpawnDirectorUtility.NormalizeSpawnInterval(tuning.SpawnIntervalSeconds),
                    SpawnCountPerInterval = MonsterSpawnDirectorUtility.NormalizeSpawnCount(tuning.SpawnCountPerInterval)
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
    [Tooltip("モンスタープレハブ候補。MonsterEntity Authoring を持つプレハブを使う。")]
    public GameObject Prefab;

    [Min(0f)]
    [Tooltip("相対的な選択重み。0 にするとこの候補は無効になる。")]
    public float Weight;
}

/// <summary>
/// Inspector で編集するステージ進行度別の timed monster spawn 設定。
/// </summary>
[System.Serializable]
public struct MonsterSpawnStageTuningEntry
{
    [Min(0f)]
    [Tooltip("StageSpawnProgress がこの値以上のとき、この設定を使う。")]
    public float MinStageProgress;

    [Min(0f)]
    [Tooltip("時間スポーンのバッチ間隔秒。")]
    public float SpawnIntervalSeconds;

    [Min(0)]
    [Tooltip("1 回の時間スポーンバッチで生成するモンスター数。")]
    public int SpawnCountPerInterval;
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
    /// Player 周囲の XZ ring 内に、必要なら player の進行方向へ偏らせて 1 点を生成する。
    /// </summary>
    public static float3 CalculateBiasedPlayerRingSpawnPosition(
        float3 playerPosition,
        float groundY,
        float minDistance,
        float maxDistance,
        float2 preferredDirection,
        float forwardBias,
        ref Unity.Mathematics.Random random)
    {
        NormalizeSpawnDistanceRange(
            minDistance,
            maxDistance,
            out var normalizedMinDistance,
            out var normalizedMaxDistance);

        var normalizedBias = NormalizeForwardSpawnBias(forwardBias);
        var direction = math.normalizesafe(preferredDirection);
        var angle = random.NextFloat(0f, math.PI * 2f);

        if (normalizedBias > 0f &&
            math.lengthsq(direction) > 0f &&
            random.NextFloat() < normalizedBias)
        {
            var forwardAngle = math.atan2(direction.y, direction.x);
            angle = forwardAngle + random.NextFloat(-math.PI * 0.5f, math.PI * 0.5f);
        }

        var minDistanceSq = normalizedMinDistance * normalizedMinDistance;
        var maxDistanceSq = normalizedMaxDistance * normalizedMaxDistance;
        var radius = math.sqrt(random.NextFloat(minDistanceSq, maxDistanceSq));
        var spawnDirection = new float2(math.cos(angle), math.sin(angle));

        return new float3(
            playerPosition.x + spawnDirection.x * radius,
            groundY,
            playerPosition.z + spawnDirection.y * radius);
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
    /// ステージ進行度を非負値へ正規化する。
    /// </summary>
    public static float NormalizeStageProgress(float progress)
    {
        return math.max(0f, progress);
    }

    /// <summary>
    /// timed spawn 間隔を非負値へ正規化する。
    /// </summary>
    public static float NormalizeSpawnInterval(float seconds)
    {
        return math.max(0f, math.abs(seconds));
    }

    /// <summary>
    /// timed spawn 数を非負値へ正規化する。
    /// </summary>
    public static int NormalizeSpawnCount(int spawnCount)
    {
        return math.max(0, spawnCount);
    }

    /// <summary>
    /// 1 回の recycle scan で調べる monster 数を 1 以上へ正規化する。
    /// </summary>
    public static int NormalizeRecycleChecksPerFrame(int checkCount)
    {
        return math.max(1, checkCount);
    }

    /// <summary>
    /// 近距離密度の低下判定数を target 以下へ正規化する。
    /// </summary>
    public static int NormalizeNearbyLowThreshold(int lowThreshold, int targetCount)
    {
        var normalizedTargetCount = NormalizeSpawnCount(targetCount);

        if (normalizedTargetCount <= 0)
        {
            return 0;
        }

        return math.clamp(NormalizeSpawnCount(lowThreshold), 0, normalizedTargetCount);
    }

    /// <summary>
    /// 近距離密度の半径を spawn ring を含む値へ正規化する。
    /// </summary>
    public static float NormalizeNearbyMonsterRadius(float nearbyRadius, float maxSpawnDistanceFromPlayer)
    {
        var safeNearbyRadius = math.max(0f, math.abs(nearbyRadius));
        var safeMaxSpawnDistance = math.max(0f, math.abs(maxSpawnDistanceFromPlayer));

        if (safeNearbyRadius <= 0f)
        {
            return 0f;
        }

        return math.max(safeNearbyRadius, safeMaxSpawnDistance);
    }

    /// <summary>
    /// player 進行方向への spawn 偏りを 0..1 に収める。
    /// </summary>
    public static float NormalizeForwardSpawnBias(float forwardBias)
    {
        return math.clamp(forwardBias, 0f, 1f);
    }

    /// <summary>
    /// recycle 距離を spawn ring の外側に保つ。
    /// </summary>
    public static float NormalizeRecycleDistance(float recycleDistance, float maxSpawnDistanceFromPlayer)
    {
        var safeRecycleDistance = math.max(0f, math.abs(recycleDistance));
        var safeMaxSpawnDistance = math.max(0f, math.abs(maxSpawnDistanceFromPlayer));

        if (safeRecycleDistance <= 0f)
        {
            return 0f;
        }

        return math.max(safeRecycleDistance, safeMaxSpawnDistance + 0.01f);
    }

    /// <summary>
    /// 現在の alive 数と上限から、今回実際に spawn してよい数を返す。
    /// </summary>
    public static int CalculateSpawnCountUnderLimit(
        int requestedSpawnCount,
        int maxAliveMonsterCount,
        int aliveMonsterCount)
    {
        var normalizedRequestedSpawnCount = NormalizeSpawnCount(requestedSpawnCount);
        var normalizedMaxAliveMonsterCount = NormalizeSpawnCount(maxAliveMonsterCount);
        var normalizedAliveMonsterCount = NormalizeSpawnCount(aliveMonsterCount);

        if (normalizedRequestedSpawnCount <= 0 || normalizedMaxAliveMonsterCount <= 0)
        {
            return 0;
        }

        return math.min(
            normalizedRequestedSpawnCount,
            math.max(0, normalizedMaxAliveMonsterCount - normalizedAliveMonsterCount));
    }

    /// <summary>
    /// 近距離 monster 密度が低い場合、通常 batch より多く補充する数を返す。
    /// </summary>
    public static int CalculateSpawnCountForNearbyDensity(
        int spawnCountPerInterval,
        int maxAliveMonsterCount,
        int aliveMonsterCount,
        int nearbyMonsterCount,
        int nearbyLowThreshold,
        int nearbyTargetCount)
    {
        var normalizedTargetCount = NormalizeSpawnCount(nearbyTargetCount);
        var normalizedLowThreshold = NormalizeNearbyLowThreshold(
            nearbyLowThreshold,
            normalizedTargetCount);
        var normalizedNearbyCount = NormalizeSpawnCount(nearbyMonsterCount);
        var requestedSpawnCount = NormalizeSpawnCount(spawnCountPerInterval);

        if (normalizedTargetCount > 0 &&
            normalizedLowThreshold > 0 &&
            normalizedNearbyCount < normalizedLowThreshold)
        {
            requestedSpawnCount = math.max(
                requestedSpawnCount,
                normalizedTargetCount - normalizedNearbyCount);
        }

        return CalculateSpawnCountUnderLimit(
            requestedSpawnCount,
            maxAliveMonsterCount,
            aliveMonsterCount);
    }

    /// <summary>
    /// 近距離 density が低い時に recycle で補うべき数を返す。
    /// </summary>
    public static int CalculateNearbyDensityDeficit(
        int nearbyMonsterCount,
        int nearbyLowThreshold,
        int nearbyTargetCount)
    {
        var normalizedTargetCount = NormalizeSpawnCount(nearbyTargetCount);
        var normalizedLowThreshold = NormalizeNearbyLowThreshold(
            nearbyLowThreshold,
            normalizedTargetCount);
        var normalizedNearbyCount = NormalizeSpawnCount(nearbyMonsterCount);

        if (normalizedTargetCount <= 0 ||
            normalizedLowThreshold <= 0 ||
            normalizedNearbyCount >= normalizedLowThreshold)
        {
            return 0;
        }

        return math.max(0, normalizedTargetCount - normalizedNearbyCount);
    }

    /// <summary>
    /// timed spawn を今回実行するかどうかを返す。
    /// </summary>
    public static bool ShouldSpawnByInterval(float elapsedSeconds, float intervalSeconds)
    {
        var normalizedIntervalSeconds = NormalizeSpawnInterval(intervalSeconds);

        return normalizedIntervalSeconds <= 0f || elapsedSeconds >= normalizedIntervalSeconds;
    }

    /// <summary>
    /// Monster が player からの recycle 距離を超えているかどうかを返す。
    /// </summary>
    public static bool ShouldRecycleByDistance(
        float3 monsterPosition,
        float3 playerPosition,
        float recycleDistance)
    {
        var normalizedRecycleDistance = math.max(0f, math.abs(recycleDistance));

        if (normalizedRecycleDistance <= 0f)
        {
            return false;
        }

        var delta = monsterPosition.xz - playerPosition.xz;

        return math.lengthsq(delta) > normalizedRecycleDistance * normalizedRecycleDistance;
    }

    /// <summary>
    /// 現在のステージ進行度で有効な timed monster spawn 設定を選ぶ。
    /// </summary>
    public static void SelectTimedSpawnSettings(
        DynamicBuffer<MonsterSpawnStageTuningElement> stageTunings,
        float stageProgress,
        float baseSpawnIntervalSeconds,
        int baseSpawnCountPerInterval,
        out float spawnIntervalSeconds,
        out int spawnCountPerInterval)
    {
        var normalizedStageProgress = NormalizeStageProgress(stageProgress);
        var selectedMinStageProgress = -1f;

        spawnIntervalSeconds = NormalizeSpawnInterval(baseSpawnIntervalSeconds);
        spawnCountPerInterval = NormalizeSpawnCount(baseSpawnCountPerInterval);

        for (var tuningIndex = 0; tuningIndex < stageTunings.Length; tuningIndex++)
        {
            var tuning = stageTunings[tuningIndex];
            var tuningMinStageProgress = NormalizeStageProgress(tuning.MinStageProgress);

            if (tuningMinStageProgress > normalizedStageProgress ||
                tuningMinStageProgress < selectedMinStageProgress)
            {
                continue;
            }

            selectedMinStageProgress = tuningMinStageProgress;
            spawnIntervalSeconds = NormalizeSpawnInterval(tuning.SpawnIntervalSeconds);
            spawnCountPerInterval = NormalizeSpawnCount(tuning.SpawnCountPerInterval);
        }
    }

    /// <summary>
    /// timed spawn 用の非ゼロ seed を作る。
    /// </summary>
    public static uint CreateTimedSpawnSeed(
        int worldSeed,
        float3 playerPosition,
        uint sequence)
    {
        var hash = math.hash(new uint4(
            (uint)worldSeed,
            (uint)math.floor(playerPosition.x),
            (uint)math.floor(playerPosition.z),
            sequence));

        if (hash == 0u)
        {
            return 1u;
        }

        return hash;
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
/// 一定時間ごとに player 周辺へ通常 monster を配置し、遠すぎる monster を再利用する。
/// </summary>
[UpdateAfter(typeof(MonsterDestroySystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
public partial struct MonsterSpawnDirectorSystem : ISystem
{
    private float spawnElapsedSeconds;
    private float recycleElapsedSeconds;
    private int recycleScanCursor;
    private int densityRecycleScanCursor;
    private bool recycleScanActive;
    private uint spawnSequence;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterSpawnDirectorConfig>();
        state.RequireForUpdate<PlayerTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        if (!TryGetPlayer(ref state, out var playerEntity, out var playerPosition))
        {
            return;
        }

        var stageProgress = GetStageSpawnProgress(ref state);
        var groundY = GetSpawnGroundY(ref state, playerEntity, playerPosition);
        var preferredSpawnDirection = GetPreferredSpawnDirection(ref state, playerEntity);
        var isStageCleared = IsAnyStageCleared(ref state);
        var entityCommandBuffer = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
        var hasSpawnCommands = false;

        foreach (var (config, monsterPrefabs, stageTunings) in
                 SystemAPI.Query<
                     RefRO<MonsterSpawnDirectorConfig>,
                     DynamicBuffer<MonsterSpawnPrefabElement>,
                     DynamicBuffer<MonsterSpawnStageTuningElement>>())
        {
            if (isStageCleared)
            {
                recycleScanActive = false;
                continue;
            }

            if (recycleScanActive ||
                ShouldRunRecycleScan(ref state, config.ValueRO.RecycleCheckIntervalSeconds))
            {
                recycleScanActive = true;
                recycleScanActive = !RecycleFarMonsters(
                    ref state,
                    config.ValueRO,
                    playerPosition,
                    groundY);
            }

            if (monsterPrefabs.Length == 0)
            {
                continue;
            }

            MonsterSpawnDirectorUtility.SelectTimedSpawnSettings(
                stageTunings,
                stageProgress,
                config.ValueRO.SpawnIntervalSeconds,
                config.ValueRO.SpawnCountPerInterval,
                out var spawnIntervalSeconds,
                out var spawnCountPerInterval);

            if (!ShouldRunTimedSpawn(ref state, spawnIntervalSeconds) ||
                spawnCountPerInterval <= 0)
            {
                continue;
            }

            var densityStats = CountAliveMonsterDensity(
                ref state,
                playerPosition,
                config.ValueRO.NearbyMonsterRadius);
            var nearbyDensityDeficit = MonsterSpawnDirectorUtility.CalculateNearbyDensityDeficit(
                densityStats.Nearby,
                config.ValueRO.NearbyMonsterLowThreshold,
                config.ValueRO.NearbyMonsterTargetCount);
            var requestedSpawnCount = spawnCountPerInterval;

            if (nearbyDensityDeficit > 0)
            {
                var recycledCount = RecycleDistantMonstersForNearbyDensity(
                    ref state,
                    config.ValueRO,
                    playerPosition,
                    preferredSpawnDirection,
                    groundY,
                    nearbyDensityDeficit);

                densityStats.Nearby += recycledCount;
                requestedSpawnCount = math.max(0, nearbyDensityDeficit - recycledCount);
            }

            var allowedSpawnCount = MonsterSpawnDirectorUtility.CalculateSpawnCountForNearbyDensity(
                requestedSpawnCount,
                config.ValueRO.MaxAliveMonsterCount,
                densityStats.Alive,
                densityStats.Nearby,
                config.ValueRO.NearbyMonsterLowThreshold,
                config.ValueRO.NearbyMonsterTargetCount);

            if (allowedSpawnCount <= 0)
            {
                continue;
            }

            SpawnMonstersAroundPlayer(
                ref state,
                ref entityCommandBuffer,
                config.ValueRO,
                monsterPrefabs,
                playerPosition,
                preferredSpawnDirection,
                groundY,
                allowedSpawnCount);

            hasSpawnCommands = true;
        }

        if (hasSpawnCommands)
        {
            entityCommandBuffer.Playback(state.EntityManager);
        }

        entityCommandBuffer.Dispose();
    }

    private struct MonsterDensityStats
    {
        public int Alive;
        public int Nearby;
    }

    private bool TryGetPlayer(
        ref SystemState state,
        out Entity playerEntity,
        out float3 playerPosition)
    {
        foreach (var (transform, entity) in
                 SystemAPI.Query<RefRO<LocalTransform>>()
                     .WithAll<PlayerTag>()
                     .WithEntityAccess())
        {
            playerEntity = entity;
            playerPosition = transform.ValueRO.Position;
            return true;
        }

        playerEntity = Entity.Null;
        playerPosition = float3.zero;
        return false;
    }

    private float GetStageSpawnProgress(ref SystemState state)
    {
        foreach (var progress in SystemAPI.Query<RefRO<StageSpawnProgress>>())
        {
            return progress.ValueRO.Value;
        }

        return 0f;
    }

    private bool IsAnyStageCleared(ref SystemState state)
    {
        foreach (var clearState in SystemAPI.Query<RefRO<StageClearState>>())
        {
            if (clearState.ValueRO.IsCleared != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static float GetSpawnGroundY(
        ref SystemState state,
        Entity playerEntity,
        float3 playerPosition)
    {
        if (playerEntity != Entity.Null &&
            state.EntityManager.HasComponent<GroundSnap>(playerEntity))
        {
            return state.EntityManager.GetComponentData<GroundSnap>(playerEntity).GroundY;
        }

        return playerPosition.y;
    }

    private static float2 GetPreferredSpawnDirection(ref SystemState state, Entity playerEntity)
    {
        if (playerEntity != Entity.Null &&
            state.EntityManager.HasComponent<Velocity>(playerEntity))
        {
            var velocity = state.EntityManager.GetComponentData<Velocity>(playerEntity).Value.xz;

            if (math.lengthsq(velocity) > 0.0001f)
            {
                return math.normalize(velocity);
            }
        }

        if (playerEntity != Entity.Null &&
            state.EntityManager.HasComponent<FacingDirection>(playerEntity))
        {
            var direction = state.EntityManager.GetComponentData<FacingDirection>(playerEntity).Value;

            if (math.lengthsq(direction) > 0.0001f)
            {
                return math.normalize(direction);
            }
        }

        return new float2(0f, 1f);
    }

    private bool ShouldRunTimedSpawn(ref SystemState state, float intervalSeconds)
    {
        if (intervalSeconds <= 0f)
        {
            return true;
        }

        spawnElapsedSeconds += SystemAPI.Time.DeltaTime;

        if (!MonsterSpawnDirectorUtility.ShouldSpawnByInterval(spawnElapsedSeconds, intervalSeconds))
        {
            return false;
        }

        spawnElapsedSeconds = math.max(0f, spawnElapsedSeconds - intervalSeconds);
        return true;
    }

    private bool ShouldRunRecycleScan(ref SystemState state, float intervalSeconds)
    {
        if (intervalSeconds <= 0f)
        {
            return true;
        }

        recycleElapsedSeconds += SystemAPI.Time.DeltaTime;

        if (!MonsterSpawnDirectorUtility.ShouldSpawnByInterval(recycleElapsedSeconds, intervalSeconds))
        {
            return false;
        }

        recycleElapsedSeconds = math.max(0f, recycleElapsedSeconds - intervalSeconds);
        return true;
    }

    private void SpawnMonstersAroundPlayer(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        MonsterSpawnDirectorConfig config,
        DynamicBuffer<MonsterSpawnPrefabElement> monsterPrefabs,
        float3 playerPosition,
        float2 preferredSpawnDirection,
        float groundY,
        int spawnCount)
    {
        var random = new Unity.Mathematics.Random(MonsterSpawnDirectorUtility.CreateTimedSpawnSeed(
            config.WorldSeed,
            playerPosition,
            spawnSequence++));

        for (var spawnIndex = 0; spawnIndex < spawnCount; spawnIndex++)
        {
            var monsterPrefab = MonsterSpawnDirectorUtility.SelectMonsterPrefab(
                monsterPrefabs,
                ref random);

            if (monsterPrefab == Entity.Null)
            {
                continue;
            }

            var position = MonsterSpawnDirectorUtility.CalculateBiasedPlayerRingSpawnPosition(
                playerPosition,
                groundY,
                config.MinSpawnDistanceFromPlayer,
                config.MaxSpawnDistanceFromPlayer,
                preferredSpawnDirection,
                config.ForwardSpawnBias,
                ref random);

            SpawnMonster(
                ref state,
                ref entityCommandBuffer,
                monsterPrefab,
                position);
        }
    }

    private MonsterDensityStats CountAliveMonsterDensity(
        ref SystemState state,
        float3 playerPosition,
        float nearbyMonsterRadius)
    {
        var stats = new MonsterDensityStats();
        var safeNearbyRadius = math.max(0f, math.abs(nearbyMonsterRadius));
        var nearbyMonsterRadiusSq = safeNearbyRadius * safeNearbyRadius;

        foreach (var transform in
                 SystemAPI.Query<RefRO<LocalTransform>>()
                     .WithAll<MonsterTag, MonsterRecycleTag>()
                     .WithNone<MonsterDestroyVfxState>())
        {
            stats.Alive++;

            if (safeNearbyRadius <= 0f)
            {
                continue;
            }

            var delta = transform.ValueRO.Position.xz - playerPosition.xz;

            if (math.lengthsq(delta) <= nearbyMonsterRadiusSq)
            {
                stats.Nearby++;
            }
        }

        return stats;
    }

    private int RecycleDistantMonstersForNearbyDensity(
        ref SystemState state,
        MonsterSpawnDirectorConfig config,
        float3 playerPosition,
        float2 preferredSpawnDirection,
        float groundY,
        int maxRecycleCount)
    {
        var normalizedMaxRecycleCount = MonsterSpawnDirectorUtility.NormalizeSpawnCount(maxRecycleCount);
        var safeNearbyRadius = math.max(0f, math.abs(config.NearbyMonsterRadius));

        if (normalizedMaxRecycleCount <= 0 ||
            safeNearbyRadius <= 0f)
        {
            densityRecycleScanCursor = 0;
            return 0;
        }

        var nearbyMonsterRadiusSq = safeNearbyRadius * safeNearbyRadius;
        var maxChecksPerFrame =
            MonsterSpawnDirectorUtility.NormalizeRecycleChecksPerFrame(config.MaxRecycleChecksPerFrame);
        var checkedMonsterCount = 0;
        var seenMonsterCount = 0;
        var recycledMonsterCount = 0;
        var random = new Unity.Mathematics.Random(MonsterSpawnDirectorUtility.CreateTimedSpawnSeed(
            config.WorldSeed,
            playerPosition,
            spawnSequence++));
        var healthLookup = SystemAPI.GetComponentLookup<HealthComponent>(false);
        var velocityLookup = SystemAPI.GetComponentLookup<Velocity>(false);
        var hitVfxLookup = SystemAPI.GetComponentLookup<MonsterHitVfxState>(false);
        var hitVfxConfigLookup = SystemAPI.GetComponentLookup<MonsterHitVfxConfig>(true);
        var groundSnapLookup = SystemAPI.GetComponentLookup<GroundSnap>(false);

        foreach (var (transform, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>>()
                     .WithAll<MonsterTag, MonsterRecycleTag>()
                     .WithNone<MonsterDestroyVfxState>()
                     .WithEntityAccess())
        {
            if (seenMonsterCount < densityRecycleScanCursor)
            {
                seenMonsterCount++;
                continue;
            }

            seenMonsterCount++;
            checkedMonsterCount++;

            var delta = transform.ValueRO.Position.xz - playerPosition.xz;

            if (math.lengthsq(delta) <= nearbyMonsterRadiusSq)
            {
                if (checkedMonsterCount >= maxChecksPerFrame)
                {
                    densityRecycleScanCursor = seenMonsterCount;
                    return recycledMonsterCount;
                }

                continue;
            }

            var recyclePosition = MonsterSpawnDirectorUtility.CalculateBiasedPlayerRingSpawnPosition(
                playerPosition,
                groundY,
                config.MinSpawnDistanceFromPlayer,
                config.MaxSpawnDistanceFromPlayer,
                preferredSpawnDirection,
                config.ForwardSpawnBias,
                ref random);

            transform.ValueRW = CalculateMonsterPlacementTransform(
                ref state,
                entity,
                recyclePosition,
                transform.ValueRO);
            ResetRecycledMonsterRuntimeState(
                ref state,
                entity,
                groundY,
                ref healthLookup,
                ref velocityLookup,
                ref hitVfxLookup,
                ref hitVfxConfigLookup,
                ref groundSnapLookup);

            recycledMonsterCount++;

            if (recycledMonsterCount >= normalizedMaxRecycleCount ||
                checkedMonsterCount >= maxChecksPerFrame)
            {
                densityRecycleScanCursor = seenMonsterCount;
                return recycledMonsterCount;
            }
        }

        densityRecycleScanCursor = 0;
        return recycledMonsterCount;
    }

    private bool RecycleFarMonsters(
        ref SystemState state,
        MonsterSpawnDirectorConfig config,
        float3 playerPosition,
        float groundY)
    {
        if (config.RecycleDistanceFromPlayer <= 0f)
        {
            recycleScanCursor = 0;
            return true;
        }

        var maxChecksPerFrame =
            MonsterSpawnDirectorUtility.NormalizeRecycleChecksPerFrame(config.MaxRecycleChecksPerFrame);
        var checkedMonsterCount = 0;
        var seenMonsterCount = 0;
        var random = new Unity.Mathematics.Random(MonsterSpawnDirectorUtility.CreateTimedSpawnSeed(
            config.WorldSeed,
            playerPosition,
            spawnSequence++));
        var healthLookup = SystemAPI.GetComponentLookup<HealthComponent>(false);
        var velocityLookup = SystemAPI.GetComponentLookup<Velocity>(false);
        var hitVfxLookup = SystemAPI.GetComponentLookup<MonsterHitVfxState>(false);
        var hitVfxConfigLookup = SystemAPI.GetComponentLookup<MonsterHitVfxConfig>(true);
        var groundSnapLookup = SystemAPI.GetComponentLookup<GroundSnap>(false);

        foreach (var (transform, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>>()
                     .WithAll<MonsterTag, MonsterRecycleTag>()
                     .WithNone<MonsterDestroyVfxState>()
                     .WithEntityAccess())
        {
            if (seenMonsterCount < recycleScanCursor)
            {
                seenMonsterCount++;
                continue;
            }

            seenMonsterCount++;
            checkedMonsterCount++;

            if (!MonsterSpawnDirectorUtility.ShouldRecycleByDistance(
                transform.ValueRO.Position,
                playerPosition,
                config.RecycleDistanceFromPlayer))
            {
                if (checkedMonsterCount >= maxChecksPerFrame)
                {
                    recycleScanCursor = seenMonsterCount;
                    return false;
                }

                continue;
            }

            var recyclePosition = MonsterSpawnDirectorUtility.CalculatePlayerRingSpawnPosition(
                playerPosition,
                groundY,
                config.MinSpawnDistanceFromPlayer,
                config.MaxSpawnDistanceFromPlayer,
                ref random);

            transform.ValueRW = CalculateMonsterPlacementTransform(
                ref state,
                entity,
                recyclePosition,
                transform.ValueRO);
            ResetRecycledMonsterRuntimeState(
                ref state,
                entity,
                groundY,
                ref healthLookup,
                ref velocityLookup,
                ref hitVfxLookup,
                ref hitVfxConfigLookup,
                ref groundSnapLookup);

            if (checkedMonsterCount >= maxChecksPerFrame)
            {
                recycleScanCursor = seenMonsterCount;
                return false;
            }
        }

        recycleScanCursor = 0;
        return true;
    }

    private static void SpawnMonster(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity monsterPrefab,
        float3 groundPosition)
    {
        if (monsterPrefab == Entity.Null)
        {
            return;
        }

        var monster = entityCommandBuffer.Instantiate(monsterPrefab);
        var transform = CreateMonsterPlacementTransform(
            ref state,
            monsterPrefab,
            groundPosition);

        entityCommandBuffer.SetComponent(
            monster,
            transform);
    }

    private static void ResetRecycledMonsterRuntimeState(
        ref SystemState state,
        Entity monsterEntity,
        float groundY,
        ref ComponentLookup<HealthComponent> healthLookup,
        ref ComponentLookup<Velocity> velocityLookup,
        ref ComponentLookup<MonsterHitVfxState> hitVfxLookup,
        ref ComponentLookup<MonsterHitVfxConfig> hitVfxConfigLookup,
        ref ComponentLookup<GroundSnap> groundSnapLookup)
    {
        if (healthLookup.HasComponent(monsterEntity))
        {
            var health = healthLookup[monsterEntity];

            health.CurrentHp = health.MaxHp;
            healthLookup[monsterEntity] = health;
        }

        if (velocityLookup.HasComponent(monsterEntity))
        {
            velocityLookup[monsterEntity] = new Velocity
            {
                Value = float3.zero
            };
        }

        if (hitVfxLookup.HasComponent(monsterEntity))
        {
            hitVfxLookup[monsterEntity] = new MonsterHitVfxState
            {
                ElapsedTime = 0f,
                IsPlaying = 0
            };
        }

        if (groundSnapLookup.HasComponent(monsterEntity))
        {
            groundSnapLookup[monsterEntity] = new GroundSnap
            {
                GroundY = groundY,
                IsGrounded = 1
            };
        }

        if (hitVfxConfigLookup.HasComponent(monsterEntity))
        {
            ResetMonsterMaterialColorIfPresent(
                ref state,
                monsterEntity,
                hitVfxConfigLookup[monsterEntity].RestBaseColor);
        }
    }

    private static void ResetMonsterMaterialColorIfPresent(
        ref SystemState state,
        Entity rootEntity,
        float4 restColor)
    {
        if (!state.EntityManager.HasBuffer<LinkedEntityGroup>(rootEntity))
        {
            SetMonsterMaterialColorIfPresent(ref state, rootEntity, restColor);
            return;
        }

        var linkedEntities = state.EntityManager.GetBuffer<LinkedEntityGroup>(rootEntity);

        for (var linkedEntityIndex = 0; linkedEntityIndex < linkedEntities.Length; linkedEntityIndex++)
        {
            SetMonsterMaterialColorIfPresent(
                ref state,
                linkedEntities[linkedEntityIndex].Value,
                restColor);
        }
    }

    private static void SetMonsterMaterialColorIfPresent(
        ref SystemState state,
        Entity entity,
        float4 restColor)
    {
        if (!state.EntityManager.HasComponent<URPMaterialPropertyBaseColor>(entity))
        {
            return;
        }

        state.EntityManager.SetComponentData(entity, new URPMaterialPropertyBaseColor
        {
            Value = restColor
        });
    }

    private static LocalTransform CreateMonsterPlacementTransform(
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

    private static LocalTransform CalculateMonsterPlacementTransform(
        ref SystemState state,
        Entity monsterEntity,
        float3 groundPosition,
        LocalTransform currentTransform)
    {
        var position = groundPosition;

        if (state.EntityManager.HasComponent<GroundSensor>(monsterEntity))
        {
            position = MonsterSpawnDirectorUtility.CalculateGroundedSpawnPosition(
                groundPosition,
                state.EntityManager.GetComponentData<GroundSensor>(monsterEntity),
                currentTransform.Rotation,
                currentTransform.Scale);
        }

        return LocalTransform.FromPositionRotationScale(
            position,
            currentTransform.Rotation,
            currentTransform.Scale);
    }
}
