using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// カメラに映っている地面の範囲をもとに計算した、モンスターの出現距離（画面のすぐ外側）。
/// MonsterSpawnCameraBoundsUpdater が書き込み、MonsterSpawnDirectorSystem が読む。
/// </summary>
public struct MonsterSpawnCameraBounds : IComponentData
{
    /// <summary>
    /// カメラに映っている地面の範囲を、プレイヤーを中心とした円で囲んだときの半径。
    /// </summary>
    public float VisibleGroundRadius;

    /// <summary>
    /// モンスターを出現させる、プレイヤーからの最小距離（XZ 平面）。
    /// </summary>
    public float MinSpawnDistanceFromPlayer;

    /// <summary>
    /// モンスターを出現させる、プレイヤーからの最大距離（XZ 平面）。
    /// </summary>
    public float MaxSpawnDistanceFromPlayer;

    /// <summary>
    /// 1 なら値が有効（カメラから計算できた）。0 の場合、出現処理は Inspector の設定値を使う。
    /// </summary>
    public byte IsValid;

    /// <summary>
    /// 画面の端（8 か所）を地面に投影した点の、プレイヤーからの相対位置。[0] 左下
    /// </summary>
    public float2 GroundOffset0;

    /// <summary>
    /// [1] 下辺の中央
    /// </summary>
    public float2 GroundOffset1;

    /// <summary>
    /// [2] 右下
    /// </summary>
    public float2 GroundOffset2;

    /// <summary>
    /// [3] 左辺の中央
    /// </summary>
    public float2 GroundOffset3;

    /// <summary>
    /// [4] 右辺の中央
    /// </summary>
    public float2 GroundOffset4;

    /// <summary>
    /// [5] 左上
    /// </summary>
    public float2 GroundOffset5;

    /// <summary>
    /// [6] 上辺の中央
    /// </summary>
    public float2 GroundOffset6;

    /// <summary>
    /// [7] 右上
    /// </summary>
    public float2 GroundOffset7;
}

/// <summary>
/// カメラの視野を読み取り、モンスターが「画面のすぐ外側」に出現するよう出現距離を調整する。
/// Camera は Managed オブジェクトで ECS の System（Burst）から直接読めないため、
/// この MonoBehaviour が計算結果だけを Component に書き込み、ECS 側はその値を読む。
/// 画面の大きさや縦横比が変わっても、敵が画面内に突然現れたり、遠すぎる場所に出たりしないようにしている。
/// </summary>
[DefaultExecutionOrder(1000)]
public sealed class MonsterSpawnCameraBoundsUpdater : MonoBehaviour
{
    // 画面の端の 8 か所（四隅と各辺の中央）。ここからカメラのレイを飛ばし、地面との交点を求める。
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

    /// <summary>
    /// 毎フレーム、カメラの視野から出現距離を計算して ECS に書き込む。計算できなければ「無効」を書き込む。
    /// </summary>
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

    /// <summary>
    /// 値を書き込む Entity とプレイヤーの Query を用意する。World が作り直された場合は作り直す。
    /// </summary>
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

    /// <summary>
    /// 作成した Entity と Query を破棄する。
    /// </summary>
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

    /// <summary>
    /// 画面の端からレイを飛ばして地面との交点を求め、カメラに映っている地面の範囲を計算する。
    /// </summary>
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

            // レイが地面と平行、または上を向いている場合は交点がないので除外する。
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
