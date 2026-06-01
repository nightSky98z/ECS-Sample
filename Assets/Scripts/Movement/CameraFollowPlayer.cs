using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// PlayerTag を持つ Entity の位置に Main Camera を追従させる。
/// </summary>
[RequireComponent(typeof(Camera))]
public sealed class CameraFollowPlayer : MonoBehaviour
{
    [Tooltip("プレイヤー位置から見たカメラの相対位置。")]
    [SerializeField]
    private Vector3 Offset = new Vector3(0f, 0f, -10f);

    [Tooltip("追従の滑らかさ。大きいほど素早く目標位置へ近づく。")]
    [SerializeField]
    private float FollowSpeed = 12f;

    [Tooltip("有効にすると、解像度やアスペクト比が変わっても見えるワールド範囲を安定させる。")]
    [SerializeField]
    private bool UseOrthographicProjection = true;

    [Tooltip("基準になる縦方向の表示半径。値が大きいほどカメラが引いた見た目になる。")]
    [SerializeField]
    [Min(0.1f)]
    private float BaseOrthographicSize = 12f;

    [Tooltip("横方向に最低限見せたいワールド幅。狭いアスペクトではこの幅を守るため自動で少し引く。")]
    [SerializeField]
    [Min(0f)]
    private float MinimumVisibleWorldWidth = 32f;

    private EntityQuery PlayerQuery;
    private Camera CachedCamera;

    private void OnEnable()
    {
        CachedCamera = GetComponent<Camera>();

        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            enabled = false;
            return;
        }

        PlayerQuery = world.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadOnly<LocalToWorld>()
        );
    }

    private void LateUpdate()
    {
        ApplyProjectionPolicy();

        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            return;
        }

        if (PlayerQuery.IsEmpty)
        {
            return;
        }

        var entityManager = world.EntityManager;
        var playerEntity = PlayerQuery.GetSingletonEntity();
        var playerTransform = entityManager.GetComponentData<LocalToWorld>(playerEntity);

        var playerPosition = (Vector3)playerTransform.Position;
        var targetPosition = playerPosition + Offset;

        transform.position = Vector3.Lerp(
            transform.position,
            targetPosition,
            1f - Mathf.Exp(-FollowSpeed * Time.deltaTime)
        );
    }

    private void ApplyProjectionPolicy()
    {
        if (!UseOrthographicProjection || CachedCamera == null)
        {
            return;
        }

        CachedCamera.orthographic = true;
        CachedCamera.orthographicSize = CameraFollowPlayerMath.CalculateAspectSafeOrthographicSize(
            BaseOrthographicSize,
            MinimumVisibleWorldWidth,
            CachedCamera.aspect);
    }
}

/// <summary>
/// CameraFollowPlayer の Unity object に依存しない表示範囲計算。
/// </summary>
public static class CameraFollowPlayerMath
{
    /// <summary>
    /// 基準 orthographic size と最小表示幅から、現在 aspect で使う size を求める。
    /// </summary>
    /// <param name="baseOrthographicSize">通常時の縦方向表示半径。</param>
    /// <param name="minimumVisibleWorldWidth">横方向に最低限見せたいワールド幅。0 以下なら無効。</param>
    /// <param name="aspect">Camera の width / height。</param>
    /// <returns>Camera.orthographicSize に設定する値。</returns>
    public static float CalculateAspectSafeOrthographicSize(
        float baseOrthographicSize,
        float minimumVisibleWorldWidth,
        float aspect)
    {
        var safeBaseSize = Mathf.Max(0.1f, baseOrthographicSize);

        if (minimumVisibleWorldWidth <= 0f || aspect <= 0f)
        {
            return safeBaseSize;
        }

        var sizeForMinimumWidth = minimumVisibleWorldWidth / (2f * aspect);

        return Mathf.Max(safeBaseSize, sizeForMinimumWidth);
    }
}
