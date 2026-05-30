using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Character prefab 内で HP bar の頭上位置と UI size を指定する marker。
/// </summary>
public sealed partial class HealthBarAnchorAuthoring : MonoBehaviour
{
    [Tooltip("RectTransform がない場合に使う HP バーの UI サイズ。")]
    [SerializeField]
    private Vector2 FallbackSize = new Vector2(100f, 12f);

    public Vector2 GetFallbackSize()
    {
        return HealthBarAnchorAuthoringUtility.NormalizeSize(FallbackSize);
    }
}

/// <summary>
/// Character prefab の子 Transform から HealthBarAnchor を作る authoring 補助。
/// </summary>
public static class HealthBarAnchorAuthoringUtility
{
    private const string DefaultHealthBarObjectName = "HP_Bar";
    private static readonly Vector2 DefaultSize = new Vector2(100f, 12f);

    /// <summary>
    /// Prefab 内の marker または HP_Bar 子 object から HP bar anchor を作る。
    /// </summary>
    /// <param name="root">PlayerEntity / MonsterEntity が付いた root Transform。</param>
    /// <param name="anchor">bake 後に Entity が持つ HP bar anchor。</param>
    /// <returns>anchor として使える子 object が見つかった場合は true。</returns>
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

    public static Vector2 NormalizeSize(Vector2 size)
    {
        return new Vector2(
            Mathf.Max(1f, size.x),
            Mathf.Max(1f, size.y));
    }

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
