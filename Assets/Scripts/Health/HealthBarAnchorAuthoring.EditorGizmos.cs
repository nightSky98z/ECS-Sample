#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// HealthBarAnchorAuthoring のエディタ用プレビュー。
/// Prefab やシーンを編集しているときに、HP バーの位置と大きさを Scene ビューに表示する。
/// </summary>
public sealed partial class HealthBarAnchorAuthoring
{
    private const float PreviewFillAmount = 0.75f;
    private const float PreviewBorderSize = 1f;

    private static readonly Color PreviewBackFaceColor = new Color(0.2f, 0.2f, 0.2f, 0.55f);
    private static readonly Color PreviewBackLineColor = new Color(0.95f, 0.95f, 0.95f, 0.95f);
    private static readonly Color PreviewFrontFaceColor = new Color(0.95f, 0.05f, 0.05f, 0.85f);
    private static readonly Color PreviewFrontLineColor = new Color(1f, 0.35f, 0.35f, 1f);

    private void OnDrawGizmos()
    {
        DrawHealthBarPreview(false);
    }

    private void OnDrawGizmosSelected()
    {
        DrawHealthBarPreview(true);
    }

    /// <summary>
    /// HP バーの見た目を Scene ビュー上に描画する（画面上のピクセルサイズで表示）。
    /// </summary>
    private void DrawHealthBarPreview(bool selected)
    {
        var previewSize = ReadPreviewSize();
        var guiSize = previewSize / EditorGUIUtility.pixelsPerPoint;
        var guiPoint = HandleUtility.WorldToGUIPoint(transform.position);
        var rect = new Rect(
            guiPoint.x - guiSize.x * 0.5f,
            guiPoint.y - guiSize.y * 0.5f,
            guiSize.x,
            guiSize.y);

        Handles.BeginGUI();
        DrawGuiHealthBar(rect);

        if (selected)
        {
            var labelRect = new Rect(rect.x, rect.y - EditorGUIUtility.singleLineHeight, rect.width, EditorGUIUtility.singleLineHeight);

            EditorGUI.DropShadowLabel(labelRect, "HP_Bar");
        }

        Handles.EndGUI();
    }

    private Vector2 ReadPreviewSize()
    {
        if (TryGetComponent<RectTransform>(out var rectTransform))
        {
            return HealthBarAnchorAuthoringUtility.NormalizeSize(rectTransform.sizeDelta);
        }

        return GetFallbackSize();
    }

    /// <summary>
    /// 背景・HP 部分（75%）・枠線を描いて、実際の HP バーに近い見た目を表示する。
    /// </summary>
    private static void DrawGuiHealthBar(Rect rect)
    {
        EditorGUI.DrawRect(rect, PreviewBackFaceColor);

        var innerRect = new Rect(
            rect.x + PreviewBorderSize,
            rect.y + PreviewBorderSize,
            Mathf.Max(0f, rect.width - PreviewBorderSize * 2f),
            Mathf.Max(0f, rect.height - PreviewBorderSize * 2f));
        var fillRect = new Rect(
            innerRect.x,
            innerRect.y,
            innerRect.width * PreviewFillAmount,
            innerRect.height);

        EditorGUI.DrawRect(fillRect, PreviewFrontFaceColor);
        DrawOutline(rect, PreviewBackLineColor);
        DrawOutline(fillRect, PreviewFrontLineColor);
    }

    private static void DrawOutline(Rect rect, Color color)
    {
        EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, rect.width, PreviewBorderSize), color);
        EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMax - PreviewBorderSize, rect.width, PreviewBorderSize), color);
        EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, PreviewBorderSize, rect.height), color);
        EditorGUI.DrawRect(new Rect(rect.xMax - PreviewBorderSize, rect.yMin, PreviewBorderSize, rect.height), color);
    }
}

/// <summary>
/// HealthBarAnchorAuthoring の Inspector と Scene ビューの操作をカスタマイズするエディタ。
/// 位置と大きさを Inspector から直接編集でき、Undo にも対応している。
/// </summary>
[CustomEditor(typeof(HealthBarAnchorAuthoring))]
public sealed class HealthBarAnchorAuthoringEditor : Editor
{
    private SerializedProperty fallbackSizeProperty;

    private void OnEnable()
    {
        fallbackSizeProperty = serializedObject.FindProperty("FallbackSize");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var authoring = (HealthBarAnchorAuthoring)target;

        DrawPositionField(authoring.transform);
        DrawSizeField(authoring);

        serializedObject.ApplyModifiedProperties();
    }

    /// <summary>
    /// Scene ビューに移動ハンドルを表示し、ドラッグで HP バーの位置を調整できるようにする。
    /// </summary>
    private void OnSceneGUI()
    {
        var authoring = (HealthBarAnchorAuthoring)target;
        var anchorTransform = authoring.transform;
        var rotation = Tools.pivotRotation == PivotRotation.Local
            ? anchorTransform.rotation
            : Quaternion.identity;

        EditorGUI.BeginChangeCheck();
        var nextPosition = Handles.PositionHandle(anchorTransform.position, rotation);

        if (!EditorGUI.EndChangeCheck())
        {
            return;
        }

        Undo.RecordObject(anchorTransform, "Move HP Bar Anchor");
        anchorTransform.position = nextPosition;
        EditorUtility.SetDirty(anchorTransform);
        PrefabUtility.RecordPrefabInstancePropertyModifications(anchorTransform);
    }

    /// <summary>
    /// 親（Prefab のルート）から見たローカル座標を編集する。
    /// </summary>
    private static void DrawPositionField(Transform anchorTransform)
    {
        EditorGUI.BeginChangeCheck();
        var nextLocalPosition = EditorGUILayout.Vector3Field("Local Position", anchorTransform.localPosition);

        if (!EditorGUI.EndChangeCheck())
        {
            return;
        }

        Undo.RecordObject(anchorTransform, "Edit HP Bar Anchor Position");
        anchorTransform.localPosition = nextLocalPosition;
        EditorUtility.SetDirty(anchorTransform);
        PrefabUtility.RecordPrefabInstancePropertyModifications(anchorTransform);
    }

    /// <summary>
    /// 大きさを編集する。RectTransform があればその sizeDelta を、なければ FallbackSize を編集する。
    /// </summary>
    private void DrawSizeField(HealthBarAnchorAuthoring authoring)
    {
        if (authoring.TryGetComponent<RectTransform>(out var rectTransform))
        {
            EditorGUI.BeginChangeCheck();
            var nextSize = EditorGUILayout.Vector2Field("Size", rectTransform.sizeDelta);

            if (!EditorGUI.EndChangeCheck())
            {
                return;
            }

            Undo.RecordObject(rectTransform, "Edit HP Bar Anchor Size");
            rectTransform.sizeDelta = HealthBarAnchorAuthoringUtility.NormalizeSize(nextSize);
            EditorUtility.SetDirty(rectTransform);
            PrefabUtility.RecordPrefabInstancePropertyModifications(rectTransform);
            return;
        }

        EditorGUILayout.PropertyField(fallbackSizeProperty, new GUIContent("Size"));
    }
}
#endif
