using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// カメラをプレイヤー（ECS の Entity）の位置に滑らかに追従させる。
/// 正投影にすることで、画面の解像度や縦横比が変わっても見えるワールドの範囲が大きく変わらないようにしている。
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

        // 指数関数で補間係数を決め、フレームレートが変わっても追従の速さが変わらないようにする。
        transform.position = Vector3.Lerp(
            transform.position,
            targetPosition,
            1f - Mathf.Exp(-FollowSpeed * Time.deltaTime)
        );
    }

    /// <summary>
    /// 正投影の表示サイズを、画面の縦横比に合わせて調整する。
    /// </summary>
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
/// CameraFollowPlayer の表示範囲の計算。Unity のオブジェクトに依存しないので、単体テストできる。
/// </summary>
public static class CameraFollowPlayerMath
{
    /// <summary>
    /// 基準の表示サイズと最低限見せたい横幅から、現在の縦横比で使う表示サイズを求める。
    /// 縦長の画面でも横方向が狭くなりすぎないよう、必要に応じてカメラを引く。
    /// </summary>
    /// <param name="baseOrthographicSize">通常時の縦方向の表示半径。</param>
    /// <param name="minimumVisibleWorldWidth">横方向に最低限見せたいワールドの幅。0 以下なら無効。</param>
    /// <param name="aspect">画面の縦横比（幅 / 高さ）。</param>
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
