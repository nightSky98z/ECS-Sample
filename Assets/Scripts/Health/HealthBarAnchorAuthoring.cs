using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// キャラクターの Prefab 内で、HP バーを表示する位置と大きさを指定するための目印。
/// この GameObject の位置が、HP バーの表示位置になる。
/// </summary>
public sealed partial class HealthBarAnchorAuthoring : MonoBehaviour
{
    [Tooltip("RectTransform がない場合に使う HP バーの UI サイズ。")]
    [SerializeField]
    private Vector2 FallbackSize = new Vector2(100f, 12f);

    /// <summary>
    /// RectTransform がないときに使う HP バーの大きさを返す。
    /// </summary>
    public Vector2 GetFallbackSize()
    {
        return HealthBarAnchorAuthoringUtility.NormalizeSize(FallbackSize);
    }
}

/// <summary>
/// キャラクターの Prefab の子オブジェクトから HealthBarAnchor を作る処理（Baker から呼ばれる）。
/// </summary>
public static class HealthBarAnchorAuthoringUtility
{
    private const string DefaultHealthBarObjectName = "HP_Bar";
    private static readonly Vector2 DefaultSize = new Vector2(100f, 12f);

    /// <summary>
    /// Prefab 内の目印（HealthBarAnchorAuthoring）、または「HP_Bar」という名前の子オブジェクトから HealthBarAnchor を作る。
    /// </summary>
    /// <param name="root">PlayerEntity / MonsterEntity が付いているルートの Transform。</param>
    /// <param name="anchor">作成した HealthBarAnchor。</param>
    /// <returns>目印になる子オブジェクトが見つかった場合は true。</returns>
    public static bool TryCreateHealthBarAnchor(Transform root, out HealthBarAnchor anchor)
    {
        anchor = default;

        if (root == null)
        {
            return false;
        }

        var marker = root.GetComponentInChildren<HealthBarAnchorAuthoring>(true);

        if (marker != null)
        {
            anchor = CreateAnchor(root, marker.transform, marker.GetFallbackSize());
            return true;
        }

        var namedHealthBar = FindChildByName(root, DefaultHealthBarObjectName);

        if (namedHealthBar == null)
        {
            return false;
        }

        anchor = CreateAnchor(root, namedHealthBar, DefaultSize);
        return true;
    }

    /// <summary>
    /// 大きさが 1 未満にならないように補正する。
    /// </summary>
    public static Vector2 NormalizeSize(Vector2 size)
    {
        return new Vector2(
            Mathf.Max(1f, size.x),
            Mathf.Max(1f, size.y));
    }

    /// <summary>
    /// ルートから見た目印の相対位置と大きさから HealthBarAnchor を作る。
    /// </summary>
    private static HealthBarAnchor CreateAnchor(
        Transform root,
        Transform anchorTransform,
        Vector2 fallbackSize)
    {
        var localOffset = root.InverseTransformPoint(anchorTransform.position);
        var size = ReadSize(anchorTransform, fallbackSize);

        return new HealthBarAnchor
        {
            LocalOffset = localOffset,
            Size = new float2(size.x, size.y)
        };
    }

    private static Vector2 ReadSize(Transform anchorTransform, Vector2 fallbackSize)
    {
        if (anchorTransform.TryGetComponent<RectTransform>(out var rectTransform))
        {
            return NormalizeSize(rectTransform.sizeDelta);
        }

        return NormalizeSize(fallbackSize);
    }

    /// <summary>
    /// 子孫の中から、指定した名前のオブジェクトを深さ優先で探す。
    /// </summary>
    private static Transform FindChildByName(Transform root, string childName)
    {
        for (var childIndex = 0; childIndex < root.childCount; childIndex++)
        {
            var child = root.GetChild(childIndex);

            if (child.name == childName)
            {
                return child;
            }

            var descendant = FindChildByName(child, childName);

            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }
}
