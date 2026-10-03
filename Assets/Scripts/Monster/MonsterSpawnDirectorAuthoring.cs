using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// プレイヤーの周囲にモンスターを出現させ続けるための設定。
/// </summary>
public struct MonsterSpawnDirectorConfig : IComponentData
{
    /// <summary>
    /// 定期出現の間隔（秒）。
    /// </summary>
    public float SpawnIntervalSeconds;

    /// <summary>
    /// 定期出現 1 回あたりの出現数。
    /// </summary>
    public int SpawnCountPerInterval;

    /// <summary>
    /// 同時に存在できるモンスターの最大数。
    /// </summary>
    public int MaxAliveMonsterCount;

    /// <summary>
    /// プレイヤーの近くに保ちたいモンスターの数。
    /// </summary>
    public int NearbyMonsterTargetCount;

    /// <summary>
    /// 近くのモンスターがこの数を下回ったら、遠くにいるモンスターを優先して近くに再配置する。
    /// </summary>
    public int NearbyMonsterLowThreshold;

    /// <summary>
    /// 近くのモンスターの数を確認する間隔（秒）。定期出現とは別に実行する。
    /// </summary>
    public float NearbyDensityCheckIntervalSeconds;

    /// <summary>
    /// 「近くのモンスター」として数える範囲の半径（XZ 平面）。
    /// </summary>
    public float NearbyMonsterRadius;

    /// <summary>
    /// 出現・再配置する位置の、プレイヤーからの最小距離。
    /// </summary>
    public float MinSpawnDistanceFromPlayer;

    /// <summary>
    /// 出現・再配置する位置の、プレイヤーからの最大距離。
    /// </summary>
    public float MaxSpawnDistanceFromPlayer;

    /// <summary>
    /// プレイヤーの進行方向に出現位置を寄せる度合い（0〜1）。
    /// </summary>
    public float ForwardSpawnBias;

    /// <summary>
    /// プレイヤーからこの距離より遠いモンスターは、近くに再配置する。
    /// </summary>
    public float RecycleDistanceFromPlayer;

    /// <summary>
    /// 遠くのモンスターを探す間隔（秒）。
    /// </summary>
    public float RecycleCheckIntervalSeconds;

    /// <summary>
    /// 1 フレームで調べるモンスターの数の上限（負荷を複数フレームに分散させるため）。
    /// </summary>
    public int MaxRecycleChecksPerFrame;

    /// <summary>
    /// 出現位置を決める乱数の seed（同じ seed なら同じ結果になる）。
    /// </summary>
    public int WorldSeed;
}

/// <summary>
/// ステージの進み具合に応じて、出現の間隔と数を切り替えるための設定。
/// </summary>
[InternalBufferCapacity(8)]
public struct MonsterSpawnStageTuningElement : IBufferElementData
{
    /// <summary>
    /// ステージの進み具合がこの値以上になったら、この設定を使う。
    /// </summary>
    public float MinStageProgress;

    /// <summary>
    /// この設定を使うときの出現間隔（秒）。
    /// </summary>
    public float SpawnIntervalSeconds;

    /// <summary>
    /// この設定を使うときの 1 回あたりの出現数。
    /// </summary>
    public int SpawnCountPerInterval;
}

/// <summary>
/// 出現させるモンスターの Prefab の候補。
/// </summary>
[InternalBufferCapacity(8)]
public struct MonsterSpawnPrefabElement : IBufferElementData
{
    /// <summary>
    /// モンスターの Prefab（Entity）。
    /// </summary>
    public Entity Prefab;

    /// <summary>
    /// 選ばれやすさ（重み）。0 以下なら選ばれない。
    /// </summary>
    public float Weight;
}

/// <summary>
/// プレイヤーの周囲にモンスターを出現させ続ける仕組みの設定を、Inspector から行うための Authoring。
/// マップは無限に続くため、モンスターはプレイヤーの周囲にだけ存在させ、遠く離れたものは近くに再配置する。
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

    [Header("Density")]
    [SerializeField]
    [Min(0)]
    [Tooltip("このディレクターが制御する通常モンスターの最大生存数。0 は時間スポーン無効。")]
    private int MaxAliveMonsterCount = 600;

    [SerializeField]
    [Min(0)]
    [Tooltip("プレイヤー周辺に維持したい通常モンスター数。プレイヤーが逃げた後の戦闘密度補充に使う。")]
    private int NearbyMonsterTargetCount = 240;

    [SerializeField]
    [Min(0)]
    [Tooltip("周辺モンスター数がこの値を下回ると、遠距離の通常モンスターをプレイヤー近くへ再配置する。")]
    private int NearbyMonsterLowThreshold = 200;

    [SerializeField]
    [Min(0f)]
    [Tooltip("周辺モンスター密度を確認する間隔秒。時間スポーンと分離し、セル移動後の密度不足を早く補う。")]
    private float NearbyDensityCheckIntervalSeconds = 0.1f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("周辺通常モンスター数を数える XZ 半径。最大スポーン距離以上にする。")]
    private float NearbyMonsterRadius = 60f;

    [Header("Spawn Ring")]
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
    private int MaxRecycleChecksPerFrame = 200;

    [SerializeField]
    [Tooltip("決定的な時間スポーン配置を作るためのワールドシード。")]
    private int RandomSeed = 1;

    [SerializeField]
    [Tooltip("ステージ進行度で選ぶ実行時の時間スポーン設定。到達済みの MinStageProgress が最も高い設定を使う。")]
    private MonsterSpawnStageTuningEntry[] StageTunings = new MonsterSpawnStageTuningEntry[0];

    /// <summary>
    /// Inspector で入力された値を、有効な範囲に収める。
    /// </summary>
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
        NearbyDensityCheckIntervalSeconds =
            MonsterSpawnDirectorUtility.NormalizeSpawnInterval(NearbyDensityCheckIntervalSeconds);
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

    /// <summary>
    /// 古い形式（Prefab 1 つだけ）の設定を、新しい形式（複数の候補と重み）に移す。
    /// </summary>
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

    /// <summary>
    /// 実際に使うモンスターの候補を返す（古い形式の設定しかない場合も考慮する）。
    /// </summary>
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
        /// <summary>
        /// 出現の設定、モンスターの候補、ステージごとの設定を Entity に変換する。
        /// </summary>
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
                NearbyDensityCheckIntervalSeconds =
                    MonsterSpawnDirectorUtility.NormalizeSpawnInterval(authoring.NearbyDensityCheckIntervalSeconds),
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
/// Inspector で編集する、モンスターの Prefab と選ばれやすさ（重み）。
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
/// Inspector で編集する、ステージの進み具合ごとの出現設定。
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
/// モンスターの出現処理で使う計算（出現位置・出現数・重み付き抽選など）。
/// System から切り離した static 関数にまとめ、単体テストできるようにしている。
/// </summary>
public static class MonsterSpawnDirectorUtility
{
    /// <summary>
    /// プレイヤーを囲むリング状の範囲から、出現位置を 1 つ選ぶ。
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
    /// 出現距離の範囲を、0 以上かつ「最小 ≦ 最大」になるように補正する。
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
    /// カメラに映る地面の半径とモンスターの移動速度から、「画面外に出現し、数秒後に画面に入ってくる」距離の範囲を計算する。
    /// </summary>
    public static void CalculateCameraOutsideSpawnDistanceRange(
        float visibleGroundRadius,
        float expectedMoveSpeed,
        float minEnterViewSeconds,
        float maxEnterViewSeconds,
        out float minDistance,
        out float maxDistance)
    {
        var safeVisibleGroundRadius = math.max(0f, math.abs(visibleGroundRadius));
        var safeMoveSpeed = math.max(0.1f, math.abs(expectedMoveSpeed));
        var safeMinEnterViewSeconds = math.max(0f, math.abs(minEnterViewSeconds));
        var safeMaxEnterViewSeconds = math.max(0f, math.abs(maxEnterViewSeconds));

        NormalizeSpawnDistanceRange(
            safeVisibleGroundRadius + safeMoveSpeed * safeMinEnterViewSeconds,
            safeVisibleGroundRadius + safeMoveSpeed * safeMaxEnterViewSeconds,
            out minDistance,
            out maxDistance);

        if (maxDistance <= minDistance)
        {
            maxDistance = minDistance + 0.1f;
        }
    }

    /// <summary>
    /// 指定した方向について、画面の端より外側に出現させるための距離の範囲を計算する。
    /// </summary>
    public static void CalculateCameraOutsideSpawnDistanceRange(
        MonsterSpawnCameraBounds cameraBounds,
        float2 spawnDirection,
        out float minDistance,
        out float maxDistance)
    {
        var safeVisibleGroundRadius = math.max(0f, math.abs(cameraBounds.VisibleGroundRadius));
        var minOutsideDistance = math.max(
            0f,
            cameraBounds.MinSpawnDistanceFromPlayer - safeVisibleGroundRadius);
        var maxOutsideDistance = math.max(
            minOutsideDistance,
            cameraBounds.MaxSpawnDistanceFromPlayer - safeVisibleGroundRadius);
        var direction = math.normalizesafe(spawnDirection, new float2(0f, 1f));
        var visibleDistance = CalculateVisibleGroundDistanceAlongDirection(cameraBounds, direction);

        NormalizeSpawnDistanceRange(
            visibleDistance + minOutsideDistance,
            visibleDistance + maxOutsideDistance,
            out minDistance,
            out maxDistance);

        if (maxDistance <= minDistance)
        {
            maxDistance = minDistance + 0.1f;
        }
    }

    /// <summary>
    /// 画面の端を地面に投影した点から、指定した方向に見えている最も遠い距離を返す。
    /// </summary>
    public static float CalculateVisibleGroundDistanceAlongDirection(
        MonsterSpawnCameraBounds cameraBounds,
        float2 spawnDirection)
    {
        var direction = math.normalizesafe(spawnDirection, new float2(0f, 1f));
        var visibleDistance = 0f;

        visibleDistance = math.max(visibleDistance, math.dot(cameraBounds.GroundOffset0, direction));
        visibleDistance = math.max(visibleDistance, math.dot(cameraBounds.GroundOffset1, direction));
        visibleDistance = math.max(visibleDistance, math.dot(cameraBounds.GroundOffset2, direction));
        visibleDistance = math.max(visibleDistance, math.dot(cameraBounds.GroundOffset3, direction));
        visibleDistance = math.max(visibleDistance, math.dot(cameraBounds.GroundOffset4, direction));
        visibleDistance = math.max(visibleDistance, math.dot(cameraBounds.GroundOffset5, direction));
        visibleDistance = math.max(visibleDistance, math.dot(cameraBounds.GroundOffset6, direction));
        visibleDistance = math.max(visibleDistance, math.dot(cameraBounds.GroundOffset7, direction));

        return math.max(0f, visibleDistance);
    }

    /// <summary>
    /// 実際に画面に映っている範囲をもとに、選んだ方向の「画面のすぐ外側」に出現位置を決める。
    /// </summary>
    public static float3 CalculateBiasedCameraOutsideSpawnPosition(
        float3 playerPosition,
        float groundY,
        MonsterSpawnCameraBounds cameraBounds,
        float2 preferredDirection,
        float forwardBias,
        ref Unity.Mathematics.Random random)
    {
        var spawnDirection = CalculateBiasedSpawnDirection(
            preferredDirection,
            forwardBias,
            ref random);
        CalculateCameraOutsideSpawnDistanceRange(
            cameraBounds,
            spawnDirection,
            out var minDistance,
            out var maxDistance);

        var minDistanceSq = minDistance * minDistance;
        var maxDistanceSq = maxDistance * maxDistance;
        var radius = math.sqrt(random.NextFloat(minDistanceSq, maxDistanceSq));

        return new float3(
            playerPosition.x + spawnDirection.x * radius,
            groundY,
            playerPosition.z + spawnDirection.y * radius);
    }

    /// <summary>
    /// プレイヤーを囲むリング状の範囲（XZ 平面）から、ランダムな位置を 1 つ選ぶ。
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
        // 距離の 2 乗で乱数を取ってから平方根をとると、リング内に面積あたり均等に分布する（内側に偏らない）。
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
    /// プレイヤーを囲むリング状の範囲から、進行方向に寄せてランダムな位置を 1 つ選ぶ（進む先に敵が出るようにするため）。
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

        var spawnDirection = CalculateBiasedSpawnDirection(
            preferredDirection,
            forwardBias,
            ref random);

        var minDistanceSq = normalizedMinDistance * normalizedMinDistance;
        var maxDistanceSq = normalizedMaxDistance * normalizedMaxDistance;
        var radius = math.sqrt(random.NextFloat(minDistanceSq, maxDistanceSq));

        return new float3(
            playerPosition.x + spawnDirection.x * radius,
            groundY,
            playerPosition.z + spawnDirection.y * radius);
    }

    /// <summary>
    /// 進行方向への寄せ具合を考慮して、出現させる方向を決める。
    /// </summary>
    public static float2 CalculateBiasedSpawnDirection(
        float2 preferredDirection,
        float forwardBias,
        ref Unity.Mathematics.Random random)
    {
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

        return new float2(math.cos(angle), math.sin(angle));
    }

    /// <summary>
    /// 接地判定用の球の下端がちょうど地面に触れるような、Entity の位置を返す（地面に埋まったり浮いたりしないようにする）。
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
    /// ステージの進み具合を 0 以上に補正する。
    /// </summary>
    public static float NormalizeStageProgress(float progress)
    {
        return math.max(0f, progress);
    }

    /// <summary>
    /// 出現間隔を 0 以上に補正する。
    /// </summary>
    public static float NormalizeSpawnInterval(float seconds)
    {
        return math.max(0f, math.abs(seconds));
    }

    /// <summary>
    /// 出現数を 0 以上に補正する。
    /// </summary>
    public static int NormalizeSpawnCount(int spawnCount)
    {
        return math.max(0, spawnCount);
    }

    /// <summary>
    /// 1 フレームで調べるモンスターの数を 1 以上に補正する。
    /// </summary>
    public static int NormalizeRecycleChecksPerFrame(int checkCount)
    {
        return math.max(1, checkCount);
    }

    /// <summary>
    /// 「近くのモンスターが少ない」と判定する数を、目標の数以下に補正する。
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
    /// 「近く」とみなす半径を、出現範囲を含む大きさに補正する（出現した直後に「遠い」と判定されないようにするため）。
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
    /// 進行方向への寄せ具合を 0〜1 に収める。
    /// </summary>
    public static float NormalizeForwardSpawnBias(float forwardBias)
    {
        return math.clamp(forwardBias, 0f, 1f);
    }

    /// <summary>
    /// 再配置する距離を、出現範囲より外側に補正する（出現した直後に再配置されないようにするため）。
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
    /// 現在のモンスターの数と上限から、今回実際に出現させてよい数を返す。
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
    /// 近くのモンスターが少ない場合は、通常より多めに補充する数を返す。
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
    /// 近くのモンスターが少ないときに、再配置で補うべき数を返す。
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
    /// 経過時間が出現間隔に達したかどうかを返す。
    /// </summary>
    public static bool ShouldSpawnByInterval(float elapsedSeconds, float intervalSeconds)
    {
        var normalizedIntervalSeconds = NormalizeSpawnInterval(intervalSeconds);

        return normalizedIntervalSeconds <= 0f || elapsedSeconds >= normalizedIntervalSeconds;
    }

    /// <summary>
    /// モンスターがプレイヤーから離れすぎていて、再配置が必要かどうかを返す。
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
    /// 現在のステージの進み具合に合った出現設定（間隔と数）を選ぶ。
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
    /// 出現位置を決める乱数の seed を作る（Unity.Mathematics.Random は 0 を受け付けないため、0 以外にする）。
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
    /// 候補の重みの合計を返す（0 以下の重みは無視する）。
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
    /// 0 以上・重みの合計未満の乱数値から、対応する候補を選ぶ。
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
    /// 重みに比例した確率で、モンスターの候補を 1 つ選ぶ。
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
/// プレイヤーの周囲にモンスターを出現させ続ける System。主に次の 3 つを行う。
/// 1. 定期出現：一定時間ごとに、画面のすぐ外側へモンスターを出現させる
/// 2. 遠距離の再配置：プレイヤーから離れすぎたモンスターを、近くに移動させて使い回す
/// 3. 密度の維持：近くのモンスターが少なくなったら、遠くのモンスターの再配置や追加の出現で補う
///
/// Entity の生成・削除（構造変更）は重いため、モンスターはできるだけ位置と状態をリセットして再利用する。
/// また、全モンスターを 1 フレームで調べると負荷が大きいため、調べる数に上限を設け、続きは次のフレームで調べる。
/// </summary>
[UpdateAfter(typeof(MonsterDestroySystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
public partial struct MonsterSpawnDirectorSystem : ISystem
{
    // 各処理の経過時間（間隔に達したら実行する）。
    private float spawnElapsedSeconds;
    private float recycleElapsedSeconds;
    private float nearbyDensityElapsedSeconds;

    // 複数フレームに分けて調べるときの「どこまで調べたか」の位置。
    private int recycleScanCursor;
    private int densityRecycleScanCursor;
    private bool recycleScanActive;

    // 乱数 seed を毎回変えるための通し番号。
    private uint spawnSequence;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterSpawnDirectorConfig>();
        state.RequireForUpdate<PlayerTag>();
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

        if (!TryGetPlayer(ref state, out var playerEntity, out var playerPosition))
        {
            return;
        }

        var groundSnapLookup = SystemAPI.GetComponentLookup<GroundSnap>(false);
        var velocityLookup = SystemAPI.GetComponentLookup<Velocity>(false);
        var facingLookup = SystemAPI.GetComponentLookup<FacingDirection>(true);
        var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true);
        var groundSensorLookup = SystemAPI.GetComponentLookup<GroundSensor>(true);
        var linkedEntityLookup = SystemAPI.GetBufferLookup<LinkedEntityGroup>(true);
        var baseColorLookup = SystemAPI.GetComponentLookup<URPMaterialPropertyBaseColor>(false);
        var stageProgress = GetStageSpawnProgress(ref state);
        var groundY = GetSpawnGroundY(groundSnapLookup, playerEntity, playerPosition);
        var preferredSpawnDirection = GetPreferredSpawnDirection(
            velocityLookup,
            facingLookup,
            playerEntity);
        var hasCameraSpawnBounds = TryGetCameraSpawnBounds(
            ref state,
            out var cameraSpawnBounds);
        var isStageCleared = IsAnyStageCleared(ref state);
        // 新しい Entity の生成は EntityCommandBuffer に積み、最後にまとめて反映する。
        var entityCommandBuffer = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);
        var hasSpawnCommands = false;

        foreach (var (config, monsterPrefabs, stageTunings) in
                 SystemAPI.Query<
                     RefRO<MonsterSpawnDirectorConfig>,
                     DynamicBuffer<MonsterSpawnPrefabElement>,
                     DynamicBuffer<MonsterSpawnStageTuningElement>>())
        {
            // カメラから計算した出現距離があれば、Inspector の設定値より優先して使う。
            var runtimeConfig = hasCameraSpawnBounds
                ? ApplyCameraSpawnBounds(config.ValueRO, cameraSpawnBounds)
                : config.ValueRO;

            // ステージクリア後は出現させない。
            if (isStageCleared)
            {
                recycleScanActive = false;
                densityRecycleScanCursor = 0;
                continue;
            }

            // --- 1. 遠くのモンスターを近くに再配置する（前のフレームの続きがあれば続きから）---
            if (recycleScanActive ||
                ShouldRunRecycleScan(ref state, runtimeConfig.RecycleCheckIntervalSeconds))
            {
                recycleScanActive = true;
                recycleScanActive = !RecycleFarMonsters(
                    ref state,
                    runtimeConfig,
                    playerPosition,
                    groundY,
                    groundSensorLookup,
                    linkedEntityLookup,
                    baseColorLookup,
                    hasCameraSpawnBounds,
                    cameraSpawnBounds);
            }

            if (monsterPrefabs.Length == 0)
            {
                continue;
            }

            MonsterSpawnDirectorUtility.SelectTimedSpawnSettings(
                stageTunings,
                stageProgress,
                runtimeConfig.SpawnIntervalSeconds,
                runtimeConfig.SpawnCountPerInterval,
                out var spawnIntervalSeconds,
                out var spawnCountPerInterval);

            // --- 2. 近くのモンスターの数を保つ ---
            var shouldMaintainNearbyDensity =
                densityRecycleScanCursor > 0 ||
                ShouldRunNearbyDensityMaintenance(
                    ref state,
                    runtimeConfig.NearbyDensityCheckIntervalSeconds);
            var shouldRunTimedSpawn = ShouldRunTimedSpawn(ref state, spawnIntervalSeconds);

            if (!shouldMaintainNearbyDensity &&
                (!shouldRunTimedSpawn || spawnCountPerInterval <= 0))
            {
                continue;
            }

            var densityStats = CountAliveMonsterDensity(
                ref state,
                playerPosition,
                runtimeConfig.NearbyMonsterRadius);

            if (shouldMaintainNearbyDensity)
            {
                MaintainNearbyMonsterDensity(
                    ref state,
                    ref entityCommandBuffer,
                    runtimeConfig,
                    monsterPrefabs,
                    transformLookup,
                    groundSensorLookup,
                    linkedEntityLookup,
                    baseColorLookup,
                    playerPosition,
                    preferredSpawnDirection,
                    groundY,
                    hasCameraSpawnBounds,
                    cameraSpawnBounds,
                    ref densityStats,
                    ref hasSpawnCommands);
            }

            if (!shouldRunTimedSpawn || spawnCountPerInterval <= 0)
            {
                continue;
            }

            // --- 3. 定期出現（上限を超えない数だけ出現させる）---
            var allowedSpawnCount = MonsterSpawnDirectorUtility.CalculateSpawnCountForNearbyDensity(
                spawnCountPerInterval,
                runtimeConfig.MaxAliveMonsterCount,
                densityStats.Alive,
                densityStats.Nearby,
                runtimeConfig.NearbyMonsterLowThreshold,
                runtimeConfig.NearbyMonsterTargetCount);

            if (allowedSpawnCount <= 0)
            {
                continue;
            }

            SpawnMonstersAroundPlayer(
                ref state,
                ref entityCommandBuffer,
                runtimeConfig,
                monsterPrefabs,
                transformLookup,
                groundSensorLookup,
                playerPosition,
                preferredSpawnDirection,
                groundY,
                hasCameraSpawnBounds,
                cameraSpawnBounds,
                allowedSpawnCount);

            densityStats.Alive += allowedSpawnCount;
            densityStats.Nearby += allowedSpawnCount;
            hasSpawnCommands = true;
        }

        if (hasSpawnCommands)
        {
            entityCommandBuffer.Playback(state.EntityManager);
        }

        entityCommandBuffer.Dispose();
    }

    /// <summary>
    /// モンスターの数の集計結果。
    /// </summary>
    private struct MonsterDensityStats
    {
        /// <summary>生きているモンスターの総数。</summary>
        public int Alive;

        /// <summary>プレイヤーの近くにいるモンスターの数。</summary>
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

    /// <summary>
    /// カメラから計算した出現距離を取得する。
    /// </summary>
    private bool TryGetCameraSpawnBounds(
        ref SystemState state,
        out MonsterSpawnCameraBounds cameraSpawnBounds)
    {
        foreach (var bounds in SystemAPI.Query<RefRO<MonsterSpawnCameraBounds>>())
        {
            cameraSpawnBounds = bounds.ValueRO;
            return cameraSpawnBounds.IsValid != 0;
        }

        cameraSpawnBounds = default;
        return false;
    }

    /// <summary>
    /// 設定の出現距離を、カメラから計算した値で置き換える。関連する距離（近くの範囲・再配置の距離）も合わせて補正する。
    /// </summary>
    private static MonsterSpawnDirectorConfig ApplyCameraSpawnBounds(
        MonsterSpawnDirectorConfig config,
        MonsterSpawnCameraBounds cameraSpawnBounds)
    {
        if (cameraSpawnBounds.IsValid == 0)
        {
            return config;
        }

        MonsterSpawnDirectorUtility.NormalizeSpawnDistanceRange(
            cameraSpawnBounds.MinSpawnDistanceFromPlayer,
            cameraSpawnBounds.MaxSpawnDistanceFromPlayer,
            out config.MinSpawnDistanceFromPlayer,
            out config.MaxSpawnDistanceFromPlayer);
        config.NearbyMonsterRadius = MonsterSpawnDirectorUtility.NormalizeNearbyMonsterRadius(
            config.NearbyMonsterRadius,
            config.MaxSpawnDistanceFromPlayer);
        config.RecycleDistanceFromPlayer = MonsterSpawnDirectorUtility.NormalizeRecycleDistance(
            config.RecycleDistanceFromPlayer,
            config.MaxSpawnDistanceFromPlayer);

        return config;
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

    /// <summary>
    /// 出現位置の地面の高さとして、プレイヤーが最後に接地した高さを使う。
    /// </summary>
    private static float GetSpawnGroundY(
        ComponentLookup<GroundSnap> groundSnapLookup,
        Entity playerEntity,
        float3 playerPosition)
    {
        if (playerEntity != Entity.Null &&
            groundSnapLookup.HasComponent(playerEntity))
        {
            return groundSnapLookup[playerEntity].GroundY;
        }

        return playerPosition.y;
    }

    /// <summary>
    /// 出現位置を寄せる方向を決める。移動中なら移動方向、止まっていれば向いている方向を使う。
    /// </summary>
    private static float2 GetPreferredSpawnDirection(
        ComponentLookup<Velocity> velocityLookup,
        ComponentLookup<FacingDirection> facingLookup,
        Entity playerEntity)
    {
        if (playerEntity != Entity.Null &&
            velocityLookup.HasComponent(playerEntity))
        {
            var velocity = velocityLookup[playerEntity].Value.xz;

            if (math.lengthsq(velocity) > 0.0001f)
            {
                return math.normalize(velocity);
            }
        }

        if (playerEntity != Entity.Null &&
            facingLookup.HasComponent(playerEntity))
        {
            var direction = facingLookup[playerEntity].Value;

            if (math.lengthsq(direction) > 0.0001f)
            {
                return math.normalize(direction);
            }
        }

        return new float2(0f, 1f);
    }

    /// <summary>
    /// 定期出現の時間になったかを判定する（余った時間は次回に持ち越す）。
    /// </summary>
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

    /// <summary>
    /// 遠くのモンスターを探す時間になったかを判定する。
    /// </summary>
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

    /// <summary>
    /// 近くのモンスターの数を確認する時間になったかを判定する。
    /// </summary>
    private bool ShouldRunNearbyDensityMaintenance(ref SystemState state, float intervalSeconds)
    {
        if (intervalSeconds <= 0f)
        {
            return true;
        }

        nearbyDensityElapsedSeconds += SystemAPI.Time.DeltaTime;

        if (!MonsterSpawnDirectorUtility.ShouldSpawnByInterval(
            nearbyDensityElapsedSeconds,
            intervalSeconds))
        {
            return false;
        }

        nearbyDensityElapsedSeconds = math.max(0f, nearbyDensityElapsedSeconds - intervalSeconds);
        return true;
    }

    /// <summary>
    /// 指定した数のモンスターを、プレイヤーの周囲に出現させる。
    /// </summary>
    private void SpawnMonstersAroundPlayer(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        MonsterSpawnDirectorConfig config,
        DynamicBuffer<MonsterSpawnPrefabElement> monsterPrefabs,
        ComponentLookup<LocalTransform> transformLookup,
        ComponentLookup<GroundSensor> groundSensorLookup,
        float3 playerPosition,
        float2 preferredSpawnDirection,
        float groundY,
        bool hasCameraSpawnBounds,
        MonsterSpawnCameraBounds cameraSpawnBounds,
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

            var position = CalculateRuntimeSpawnPosition(
                config,
                hasCameraSpawnBounds,
                cameraSpawnBounds,
                playerPosition,
                groundY,
                preferredSpawnDirection,
                config.ForwardSpawnBias,
                ref random);

            SpawnMonster(
                ref entityCommandBuffer,
                transformLookup,
                groundSensorLookup,
                monsterPrefab,
                position);
        }
    }

    /// <summary>
    /// 近くのモンスターが足りない場合に補う。まず遠くのモンスターを再配置し、それでも足りなければ新しく出現させる。
    /// </summary>
    private void MaintainNearbyMonsterDensity(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        MonsterSpawnDirectorConfig config,
        DynamicBuffer<MonsterSpawnPrefabElement> monsterPrefabs,
        ComponentLookup<LocalTransform> transformLookup,
        ComponentLookup<GroundSensor> groundSensorLookup,
        BufferLookup<LinkedEntityGroup> linkedEntityLookup,
        ComponentLookup<URPMaterialPropertyBaseColor> baseColorLookup,
        float3 playerPosition,
        float2 preferredSpawnDirection,
        float groundY,
        bool hasCameraSpawnBounds,
        MonsterSpawnCameraBounds cameraSpawnBounds,
        ref MonsterDensityStats densityStats,
        ref bool hasSpawnCommands)
    {
        var nearbyDensityDeficit = MonsterSpawnDirectorUtility.CalculateNearbyDensityDeficit(
            densityStats.Nearby,
            config.NearbyMonsterLowThreshold,
            config.NearbyMonsterTargetCount);

        if (nearbyDensityDeficit <= 0)
        {
            densityRecycleScanCursor = 0;
            return;
        }

        var recycledCount = RecycleDistantMonstersForNearbyDensity(
            ref state,
            config,
            playerPosition,
            preferredSpawnDirection,
            groundY,
            groundSensorLookup,
            linkedEntityLookup,
            baseColorLookup,
            hasCameraSpawnBounds,
            cameraSpawnBounds,
            nearbyDensityDeficit);

        densityStats.Nearby += recycledCount;
        var remainingDeficit = math.max(0, nearbyDensityDeficit - recycledCount);
        var allowedSpawnCount = MonsterSpawnDirectorUtility.CalculateSpawnCountForNearbyDensity(
            remainingDeficit,
            config.MaxAliveMonsterCount,
            densityStats.Alive,
            densityStats.Nearby,
            config.NearbyMonsterLowThreshold,
            config.NearbyMonsterTargetCount);

        if (allowedSpawnCount <= 0)
        {
            return;
        }

        SpawnMonstersAroundPlayer(
            ref state,
            ref entityCommandBuffer,
            config,
            monsterPrefabs,
            transformLookup,
            groundSensorLookup,
            playerPosition,
            preferredSpawnDirection,
            groundY,
            hasCameraSpawnBounds,
            cameraSpawnBounds,
            allowedSpawnCount);

        densityStats.Alive += allowedSpawnCount;
        densityStats.Nearby += allowedSpawnCount;
        hasSpawnCommands = true;
    }

    /// <summary>
    /// 生きているモンスターの総数と、プレイヤーの近くにいる数を数える。
    /// </summary>
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

    /// <summary>
    /// 近くのモンスターを増やすため、遠くにいるモンスターをプレイヤーの近くに再配置する。
    /// 調べる数には上限があり、上限に達したら続きは次のフレームで調べる。
    /// </summary>
    /// <returns>再配置した数。</returns>
    private int RecycleDistantMonstersForNearbyDensity(
        ref SystemState state,
        MonsterSpawnDirectorConfig config,
        float3 playerPosition,
        float2 preferredSpawnDirection,
        float groundY,
        ComponentLookup<GroundSensor> groundSensorLookup,
        BufferLookup<LinkedEntityGroup> linkedEntityLookup,
        ComponentLookup<URPMaterialPropertyBaseColor> baseColorLookup,
        bool hasCameraSpawnBounds,
        MonsterSpawnCameraBounds cameraSpawnBounds,
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
        var debuffRuntimeLookup = SystemAPI.GetComponentLookup<DebuffRuntimeState>(false);
        var debuffAggregateLookup = SystemAPI.GetComponentLookup<DebuffAggregate>(false);

        foreach (var (transform, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>>()
                     .WithAll<MonsterTag, MonsterRecycleTag>()
                     .WithNone<MonsterDestroyVfxState, FreezeTag>()
                     .WithEntityAccess())
        {
            // 前のフレームで調べ終わったところまでは飛ばす。
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

            var recyclePosition = CalculateRuntimeSpawnPosition(
                config,
                hasCameraSpawnBounds,
                cameraSpawnBounds,
                playerPosition,
                groundY,
                preferredSpawnDirection,
                config.ForwardSpawnBias,
                ref random);

            transform.ValueRW = CalculateMonsterPlacementTransform(
                groundSensorLookup,
                entity,
                recyclePosition,
                transform.ValueRO);
            ResetRecycledMonsterRuntimeState(
                entity,
                groundY,
                ref healthLookup,
                ref velocityLookup,
                ref hitVfxLookup,
                ref hitVfxConfigLookup,
                ref groundSnapLookup,
                ref debuffRuntimeLookup,
                ref debuffAggregateLookup,
                linkedEntityLookup,
                ref baseColorLookup);

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

    /// <summary>
    /// 出現位置を決める。カメラの情報があれば画面のすぐ外側に、なければ設定のリング状の範囲に出現させる。
    /// </summary>
    private static float3 CalculateRuntimeSpawnPosition(
        MonsterSpawnDirectorConfig config,
        bool hasCameraSpawnBounds,
        MonsterSpawnCameraBounds cameraSpawnBounds,
        float3 playerPosition,
        float groundY,
        float2 preferredSpawnDirection,
        float forwardBias,
        ref Unity.Mathematics.Random random)
    {
        if (hasCameraSpawnBounds && cameraSpawnBounds.IsValid != 0)
        {
            return MonsterSpawnDirectorUtility.CalculateBiasedCameraOutsideSpawnPosition(
                playerPosition,
                groundY,
                cameraSpawnBounds,
                preferredSpawnDirection,
                forwardBias,
                ref random);
        }

        return MonsterSpawnDirectorUtility.CalculateBiasedPlayerRingSpawnPosition(
            playerPosition,
            groundY,
            config.MinSpawnDistanceFromPlayer,
            config.MaxSpawnDistanceFromPlayer,
            preferredSpawnDirection,
            forwardBias,
            ref random);
    }

    /// <summary>
    /// プレイヤーから離れすぎたモンスターを、近くに再配置する。
    /// 調べる数には上限があり、上限に達したら続きは次のフレームで調べる。
    /// </summary>
    /// <returns>全モンスターを調べ終わったら true、続きがあれば false。</returns>
    private bool RecycleFarMonsters(
        ref SystemState state,
        MonsterSpawnDirectorConfig config,
        float3 playerPosition,
        float groundY,
        ComponentLookup<GroundSensor> groundSensorLookup,
        BufferLookup<LinkedEntityGroup> linkedEntityLookup,
        ComponentLookup<URPMaterialPropertyBaseColor> baseColorLookup,
        bool hasCameraSpawnBounds,
        MonsterSpawnCameraBounds cameraSpawnBounds)
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
        var debuffRuntimeLookup = SystemAPI.GetComponentLookup<DebuffRuntimeState>(false);
        var debuffAggregateLookup = SystemAPI.GetComponentLookup<DebuffAggregate>(false);

        foreach (var (transform, entity) in
                 SystemAPI.Query<RefRW<LocalTransform>>()
                     .WithAll<MonsterTag, MonsterRecycleTag>()
                     .WithNone<MonsterDestroyVfxState, FreezeTag>()
                     .WithEntityAccess())
        {
            // 前のフレームで調べ終わったところまでは飛ばす。
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

            var recyclePosition = CalculateRuntimeSpawnPosition(
                config,
                hasCameraSpawnBounds,
                cameraSpawnBounds,
                playerPosition,
                groundY,
                float2.zero,
                0f,
                ref random);

            transform.ValueRW = CalculateMonsterPlacementTransform(
                groundSensorLookup,
                entity,
                recyclePosition,
                transform.ValueRO);
            ResetRecycledMonsterRuntimeState(
                entity,
                groundY,
                ref healthLookup,
                ref velocityLookup,
                ref hitVfxLookup,
                ref hitVfxConfigLookup,
                ref groundSnapLookup,
                ref debuffRuntimeLookup,
                ref debuffAggregateLookup,
                linkedEntityLookup,
                ref baseColorLookup);

            if (checkedMonsterCount >= maxChecksPerFrame)
            {
                recycleScanCursor = seenMonsterCount;
                return false;
            }
        }

        recycleScanCursor = 0;
        return true;
    }

    /// <summary>
    /// モンスターを 1 体生成し、地面に立つ位置に置く。
    /// </summary>
    private static void SpawnMonster(
        ref EntityCommandBuffer entityCommandBuffer,
        ComponentLookup<LocalTransform> transformLookup,
        ComponentLookup<GroundSensor> groundSensorLookup,
        Entity monsterPrefab,
        float3 groundPosition)
    {
        if (monsterPrefab == Entity.Null)
        {
            return;
        }

        var monster = entityCommandBuffer.Instantiate(monsterPrefab);
        var transform = CreateMonsterPlacementTransform(
            transformLookup,
            groundSensorLookup,
            monsterPrefab,
            groundPosition);

        entityCommandBuffer.SetComponent(
            monster,
            transform);
    }

    /// <summary>
    /// 再配置したモンスターの状態（HP・速度・デバフ・色など）を初期値に戻す。
    /// Component を付け外しせず値を書き換えるだけなので、構造変更が起きない。
    /// </summary>
    private static void ResetRecycledMonsterRuntimeState(
        Entity monsterEntity,
        float groundY,
        ref ComponentLookup<HealthComponent> healthLookup,
        ref ComponentLookup<Velocity> velocityLookup,
        ref ComponentLookup<MonsterHitVfxState> hitVfxLookup,
        ref ComponentLookup<MonsterHitVfxConfig> hitVfxConfigLookup,
        ref ComponentLookup<GroundSnap> groundSnapLookup,
        ref ComponentLookup<DebuffRuntimeState> debuffRuntimeLookup,
        ref ComponentLookup<DebuffAggregate> debuffAggregateLookup,
        BufferLookup<LinkedEntityGroup> linkedEntityLookup,
        ref ComponentLookup<URPMaterialPropertyBaseColor> baseColorLookup)
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

        if (debuffRuntimeLookup.HasComponent(monsterEntity))
        {
            debuffRuntimeLookup[monsterEntity] = new DebuffRuntimeState
            {
                ActiveDebuffs = default
            };
        }

        if (debuffAggregateLookup.HasComponent(monsterEntity))
        {
            debuffAggregateLookup[monsterEntity] = DebuffMath.CreateNeutralAggregate();
        }

        if (hitVfxConfigLookup.HasComponent(monsterEntity))
        {
            ResetMonsterMaterialColorIfPresent(
                linkedEntityLookup,
                ref baseColorLookup,
                monsterEntity,
                hitVfxConfigLookup[monsterEntity].RestBaseColor);
        }
    }

    /// <summary>
    /// モンスターとその子の Entity の色を、元の色に戻す。
    /// </summary>
    private static void ResetMonsterMaterialColorIfPresent(
        BufferLookup<LinkedEntityGroup> linkedEntityLookup,
        ref ComponentLookup<URPMaterialPropertyBaseColor> baseColorLookup,
        Entity rootEntity,
        float4 restColor)
    {
        if (!linkedEntityLookup.HasBuffer(rootEntity))
        {
            SetMonsterMaterialColorIfPresent(ref baseColorLookup, rootEntity, restColor);
            return;
        }

        var linkedEntities = linkedEntityLookup[rootEntity];

        for (var linkedEntityIndex = 0; linkedEntityIndex < linkedEntities.Length; linkedEntityIndex++)
        {
            SetMonsterMaterialColorIfPresent(
                ref baseColorLookup,
                linkedEntities[linkedEntityIndex].Value,
                restColor);
        }
    }

    private static void SetMonsterMaterialColorIfPresent(
        ref ComponentLookup<URPMaterialPropertyBaseColor> baseColorLookup,
        Entity entity,
        float4 restColor)
    {
        if (!baseColorLookup.HasComponent(entity))
        {
            return;
        }

        baseColorLookup[entity] = new URPMaterialPropertyBaseColor
        {
            Value = restColor
        };
    }

    /// <summary>
    /// 新しく生成するモンスターの Transform を、Prefab の回転と大きさを保ったまま、地面に立つ位置で作る。
    /// </summary>
    private static LocalTransform CreateMonsterPlacementTransform(
        ComponentLookup<LocalTransform> transformLookup,
        ComponentLookup<GroundSensor> groundSensorLookup,
        Entity monsterPrefab,
        float3 groundPosition)
    {
        var prefabTransform = LocalTransform.Identity;

        if (transformLookup.HasComponent(monsterPrefab))
        {
            prefabTransform = transformLookup[monsterPrefab];
        }

        var position = groundPosition;

        if (groundSensorLookup.HasComponent(monsterPrefab))
        {
            position = MonsterSpawnDirectorUtility.CalculateGroundedSpawnPosition(
                groundPosition,
                groundSensorLookup[monsterPrefab],
                prefabTransform.Rotation,
                prefabTransform.Scale);
        }

        return LocalTransform.FromPositionRotationScale(
            position,
            prefabTransform.Rotation,
            prefabTransform.Scale);
    }

    /// <summary>
    /// 再配置するモンスターの Transform を、今の回転と大きさを保ったまま、地面に立つ位置で作る。
    /// </summary>
    private static LocalTransform CalculateMonsterPlacementTransform(
        ComponentLookup<GroundSensor> groundSensorLookup,
        Entity monsterEntity,
        float3 groundPosition,
        LocalTransform currentTransform)
    {
        var position = groundPosition;

        if (groundSensorLookup.HasComponent(monsterEntity))
        {
            position = MonsterSpawnDirectorUtility.CalculateGroundedSpawnPosition(
                groundPosition,
                groundSensorLookup[monsterEntity],
                currentTransform.Rotation,
                currentTransform.Scale);
        }

        return LocalTransform.FromPositionRotationScale(
            position,
            currentTransform.Rotation,
            currentTransform.Scale);
    }
}
