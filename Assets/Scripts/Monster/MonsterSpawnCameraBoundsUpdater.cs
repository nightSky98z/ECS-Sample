using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// Camera の現在の地面表示範囲から、通常 monster を画面外へ出すための距離を ECS に渡す。
/// </summary>
public struct MonsterSpawnCameraBounds : IComponentData
{
    /// <summary>
    /// Camera から見えている地面範囲を player 中心の円で包んだ半径。
    /// </summary>
    public float VisibleGroundRadius;

    /// <summary>
    /// 通常 monster を出す player からの最小 XZ 距離。
    /// </summary>
    public float MinSpawnDistanceFromPlayer;

    /// <summary>
    /// 通常 monster を出す player からの最大 XZ 距離。
    /// </summary>
    public float MaxSpawnDistanceFromPlayer;

    /// <summary>
    /// 1 のとき、Camera から計算した値として spawn director が利用できる。
    /// </summary>
    public byte IsValid;

    /// <summary>
    /// Viewport 境界 sample 0 の player からの XZ offset。
    /// </summary>
    public float2 GroundOffset0;

    /// <summary>
    /// Viewport 境界 sample 1 の player からの XZ offset。
    /// </summary>
    public float2 GroundOffset1;

    /// <summary>
    /// Viewport 境界 sample 2 の player からの XZ offset。
    /// </summary>
    public float2 GroundOffset2;

    /// <summary>
    /// Viewport 境界 sample 3 の player からの XZ offset。
    /// </summary>
    public float2 GroundOffset3;

    /// <summary>
    /// Viewport 境界 sample 4 の player からの XZ offset。
    /// </summary>
    public float2 GroundOffset4;

    /// <summary>
    /// Viewport 境界 sample 5 の player からの XZ offset。
    /// </summary>
    public float2 GroundOffset5;

    /// <summary>
    /// Viewport 境界 sample 6 の player からの XZ offset。
    /// </summary>
    public float2 GroundOffset6;

    /// <summary>
    /// Viewport 境界 sample 7 の player からの XZ offset。
    /// </summary>
    public float2 GroundOffset7;
}

/// <summary>
/// Main Camera の視野を読み、monster spawn ring を画面外の少し外側へ合わせる。
///
/// Camera は managed object なので ECS system から直接読まず、この MonoBehaviour が小さい
/// singleton component へ値だけを書き込む。MonsterSpawnDirectorSystem はその component を読む。
/// </summary>
[DefaultExecutionOrder(1000)]
public sealed class MonsterSpawnCameraBoundsUpdater : MonoBehaviour
{
    private static readonly Vector2[] ViewportSamples =
    {
        new Vector2(0f, 0f),
        new Vector2(0.5f, 0f),
        new Vector2(1f, 0f),
        new Vector2(0f, 0.5f),
        new Vector2(1f, 0.5f),
        new Vector2(0f, 1f),
        new Vector2(0.5f, 1f),
        new Vector2(1f, 1f)
    };

    [SerializeField]
    [Tooltip("視野計算に使うカメラ。未指定なら同じ GameObject の Camera、なければ MainCamera を使う。")]
    private Camera TargetCamera;

    [SerializeField]
    [Min(0.1f)]
    [Tooltip("通常モンスターがプレイヤーへ近づく想定速度。画面外から視野へ入る秒数を距離に変換する。")]
    private float ExpectedMonsterMoveSpeed = 2.5f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("生成後、最速で視野へ入ってほしい秒数。")]
    private float MinEnterViewSeconds = 1f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("生成後、遅くとも視野へ入ってほしい秒数。")]
    private float MaxEnterViewSeconds = 2f;

    private World cachedWorld;
    private Entity boundsEntity;
    private EntityQuery playerQuery;
    private bool hasPlayerQuery;

    private void OnEnable()
    {
        EnsureCamera();
        EnsureEcsState();
    }

    private void LateUpdate()
    {
        if (!EnsureEcsState() ||
            !TryGetPlayerPosition(out var playerPosition, out var groundY) ||
            !EnsureCamera() ||
            !TryCalculateVisibleGroundBounds(
                TargetCamera,
                playerPosition,
                groundY,
                out var visibleGroundRadius,
                out var cameraBounds))
        {
            WriteInvalidBounds();
            return;
        }

        MonsterSpawnDirectorUtility.CalculateCameraOutsideSpawnDistanceRange(
            visibleGroundRadius,
            ExpectedMonsterMoveSpeed,
            MinEnterViewSeconds,
            MaxEnterViewSeconds,
            out var minSpawnDistance,
            out var maxSpawnDistance);

        cameraBounds.VisibleGroundRadius = visibleGroundRadius;
        cameraBounds.MinSpawnDistanceFromPlayer = minSpawnDistance;
        cameraBounds.MaxSpawnDistanceFromPlayer = maxSpawnDistance;
        cameraBounds.IsValid = 1;
        cachedWorld.EntityManager.SetComponentData(boundsEntity, cameraBounds);
    }

    private void OnDisable()
    {
        ReleaseEcsState();
    }

    private bool EnsureCamera()
    {
        if (TargetCamera != null)
        {
            return true;
        }

        TargetCamera = GetComponent<Camera>();

        if (TargetCamera != null)
        {
            return true;
        }

        TargetCamera = Camera.main;
        return TargetCamera != null;
    }

    private bool EnsureEcsState()
    {
        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            return false;
        }

        if (cachedWorld == world &&
            boundsEntity != Entity.Null &&
            world.EntityManager.Exists(boundsEntity))
        {
            return true;
        }

        ReleaseEcsState();

        cachedWorld = world;
        boundsEntity = cachedWorld.EntityManager.CreateEntity(
            ComponentType.ReadWrite<MonsterSpawnCameraBounds>());
        playerQuery = cachedWorld.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadOnly<LocalToWorld>());
        hasPlayerQuery = true;
        WriteInvalidBounds();
        return true;
    }

    private void ReleaseEcsState()
    {
        if (hasPlayerQuery &&
            cachedWorld != null &&
            cachedWorld.IsCreated)
        {
            playerQuery.Dispose();
        }

        hasPlayerQuery = false;

        if (cachedWorld != null &&
            cachedWorld.IsCreated &&
            boundsEntity != Entity.Null &&
            cachedWorld.EntityManager.Exists(boundsEntity))
        {
            cachedWorld.EntityManager.DestroyEntity(boundsEntity);
        }

        cachedWorld = null;
        boundsEntity = Entity.Null;
    }

    private bool TryGetPlayerPosition(out float3 playerPosition, out float groundY)
    {
        playerPosition = float3.zero;
        groundY = 0f;

        if (!hasPlayerQuery || playerQuery.IsEmpty)
        {
            return false;
        }

        var entityManager = cachedWorld.EntityManager;
        var playerEntity = playerQuery.GetSingletonEntity();
        var playerTransform = entityManager.GetComponentData<LocalToWorld>(playerEntity);

        playerPosition = playerTransform.Position;
        groundY = playerPosition.y;

        if (entityManager.HasComponent<GroundSnap>(playerEntity))
        {
            groundY = entityManager.GetComponentData<GroundSnap>(playerEntity).GroundY;
        }

        return true;
    }

    private void WriteInvalidBounds()
    {
        if (cachedWorld == null ||
            !cachedWorld.IsCreated ||
            boundsEntity == Entity.Null ||
            !cachedWorld.EntityManager.Exists(boundsEntity))
        {
            return;
        }

        cachedWorld.EntityManager.SetComponentData(boundsEntity, new MonsterSpawnCameraBounds
        {
            IsValid = 0
        });
    }

    private static bool TryCalculateVisibleGroundBounds(
        Camera targetCamera,
        float3 playerPosition,
        float groundY,
        out float visibleGroundRadius,
        out MonsterSpawnCameraBounds cameraBounds)
    {
        visibleGroundRadius = 0f;
        cameraBounds = default;

        if (targetCamera == null)
        {
            return false;
        }

        var hitCount = 0;

        for (var sampleIndex = 0; sampleIndex < ViewportSamples.Length; sampleIndex++)
        {
            var sample = ViewportSamples[sampleIndex];
            var ray = targetCamera.ViewportPointToRay(new Vector3(sample.x, sample.y, 0f));

            if (math.abs(ray.direction.y) <= 0.0001f)
            {
                continue;
            }

            var distanceToGround = (groundY - ray.origin.y) / ray.direction.y;

            if (distanceToGround <= 0f)
            {
                continue;
            }

            var hitPosition = ray.origin + ray.direction * distanceToGround;
            var delta = new float2(
                hitPosition.x - playerPosition.x,
                hitPosition.z - playerPosition.z);

            SetGroundOffset(ref cameraBounds, sampleIndex, delta);
            visibleGroundRadius = math.max(visibleGroundRadius, math.length(delta));
            hitCount++;
        }

        return hitCount > 0 && visibleGroundRadius > 0f;
    }

    private static void SetGroundOffset(
        ref MonsterSpawnCameraBounds cameraBounds,
        int sampleIndex,
        float2 offset)
    {
        switch (sampleIndex)
        {
            case 0:
                cameraBounds.GroundOffset0 = offset;
                break;
            case 1:
                cameraBounds.GroundOffset1 = offset;
                break;
            case 2:
                cameraBounds.GroundOffset2 = offset;
                break;
            case 3:
                cameraBounds.GroundOffset3 = offset;
                break;
            case 4:
                cameraBounds.GroundOffset4 = offset;
                break;
            case 5:
                cameraBounds.GroundOffset5 = offset;
                break;
            case 6:
                cameraBounds.GroundOffset6 = offset;
                break;
            case 7:
                cameraBounds.GroundOffset7 = offset;
                break;
        }
    }
}
