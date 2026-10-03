#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// EntitySpawnerAuthoring のエディタ用プレビュー。生成される位置に Prefab の形を Scene ビューで表示する。
/// </summary>
public partial class EntitySpawnerAuthoring
{
    private const int MaxPreviewSpawnPoints = 512;
    private const float PreviewPointRadius = 0.25f;
    private const float PreviewAreaHeight = 0.05f;

    [SerializeField]
    [Tooltip("シーンビューでスポーン範囲と予定位置を表示する。プレイヤービルドには含まれない。")]
    private bool DrawSpawnPreview = true;

    private readonly List<MeshFilter> PreviewMeshFilters = new List<MeshFilter>();

    /// <summary>
    /// オブジェクトを選択しているときだけ、Bake と同じ設定（seed・数・範囲）で計算した生成位置を描画する。
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (!DrawSpawnPreview)
        {
            return;
        }

        var center = (float3)transform.position;
        var areaSize = new float2(SpawnAreaSize.x, SpawnAreaSize.y);

        if (RandomizePosition)
        {
            Gizmos.color = new Color(0f, 0.8f, 1f, 0.8f);
            Gizmos.DrawWireCube(
                transform.position,
                new Vector3(math.abs(areaSize.x), PreviewAreaHeight, math.abs(areaSize.y)));
        }

        var spawnCount = SpawnTransformUtility.GetEffectiveSpawnCount(SpawnCount, RandomizePosition);

        if (spawnCount <= 0)
        {
            return;
        }

        var random = new Unity.Mathematics.Random(SpawnTransformUtility.NormalizeSeed(RandomSeed));
        var previewCount = math.min(spawnCount, MaxPreviewSpawnPoints);

        var previewRotation = transform.rotation;
        var previewScale = Vector3.one * transform.lossyScale.x;

        for (var spawnIndex = 0; spawnIndex < previewCount; spawnIndex++)
        {
            var position = SpawnTransformUtility.CreateSpawnPosition(
                center,
                RandomizePosition,
                areaSize,
                ref random);

            DrawPrefabPreview((Vector3)position, previewRotation, previewScale);
        }
    }

    /// <summary>
    /// 生成する Prefab に含まれるメッシュを、生成予定の位置に描画する。
    /// </summary>
    private void DrawPrefabPreview(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (SpawnEntityPrefab == null)
        {
            DrawFallbackPreview(position);
            return;
        }

        PreviewMeshFilters.Clear();
        SpawnEntityPrefab.GetComponentsInChildren(true, PreviewMeshFilters);

        if (PreviewMeshFilters.Count == 0)
        {
            DrawFallbackPreview(position);
            return;
        }

        var rootMatrix = Matrix4x4.TRS(position, rotation, scale);
        var prefabRootToLocal = SpawnEntityPrefab.transform.worldToLocalMatrix;

        for (var meshFilterIndex = 0; meshFilterIndex < PreviewMeshFilters.Count; meshFilterIndex++)
        {
            var meshFilter = PreviewMeshFilters[meshFilterIndex];
            var mesh = meshFilter.sharedMesh;

            if (mesh == null)
            {
                continue;
            }

            var localMatrix = prefabRootToLocal * meshFilter.transform.localToWorldMatrix;
            var previewMatrix = rootMatrix * localMatrix;

            DrawSolidMesh(mesh, previewMatrix);
        }
    }

    /// <summary>
    /// メッシュを Gizmo として描画する（Scene ビューが再描画されるたびに描き直される）。
    /// </summary>
    private static void DrawSolidMesh(Mesh mesh, Matrix4x4 matrix)
    {
        var previousMatrix = Gizmos.matrix;

        Gizmos.color = new Color(1f, 0.35f, 0.1f, 0.9f);
        Gizmos.matrix = matrix;

        var subMeshCount = math.max(mesh.subMeshCount, 1);

        for (var subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
        {
            Gizmos.DrawMesh(mesh, subMeshIndex, Vector3.zero, Quaternion.identity, Vector3.one);
        }

        Gizmos.matrix = previousMatrix;
    }

    /// <summary>
    /// Prefab やメッシュがない場合に、位置だけが分かる簡単な目印を描画する。
    /// </summary>
    private static void DrawFallbackPreview(Vector3 position)
    {
        Gizmos.color = new Color(1f, 0.35f, 0.1f, 0.9f);
        Gizmos.DrawSphere(position, PreviewPointRadius);
    }
}

/// <summary>
/// EntitySpawnerAuthoring の Inspector 表示をカスタマイズするエディタ。
/// </summary>
[CustomEditor(typeof(EntitySpawnerAuthoring))]
public sealed class EntitySpawnerAuthoringEditor : Editor
{
    private SerializedProperty drawSpawnPreviewProperty;
    private SerializedProperty spawnEntityPrefabProperty;
    private SerializedProperty spawnCountProperty;
    private SerializedProperty randomizePositionProperty;
    private SerializedProperty randomSeedProperty;
    private SerializedProperty spawnAreaSizeProperty;

    private void OnEnable()
    {
        drawSpawnPreviewProperty = serializedObject.FindProperty("DrawSpawnPreview");
        spawnEntityPrefabProperty = serializedObject.FindProperty("SpawnEntityPrefab");
        spawnCountProperty = serializedObject.FindProperty("SpawnCount");
        randomizePositionProperty = serializedObject.FindProperty("RandomizePosition");
        randomSeedProperty = serializedObject.FindProperty("RandomSeed");
        spawnAreaSizeProperty = serializedObject.FindProperty("SpawnAreaSize");
    }

    /// <summary>
    /// 位置をランダムにしない設定のときは、生成数とランダム用の設定を編集できないようにする（誤設定を防ぐため）。
    /// </summary>
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(drawSpawnPreviewProperty);
        EditorGUILayout.PropertyField(spawnEntityPrefabProperty);
        EditorGUILayout.PropertyField(randomizePositionProperty);

        ClampScenePositionSpawnCount();

        using (new EditorGUI.DisabledScope(!randomizePositionProperty.boolValue))
        {
            EditorGUILayout.PropertyField(spawnCountProperty);
        }

        using (new EditorGUI.DisabledScope(!randomizePositionProperty.boolValue))
        {
            EditorGUILayout.PropertyField(randomSeedProperty);
            EditorGUILayout.PropertyField(spawnAreaSizeProperty);
        }

        serializedObject.ApplyModifiedProperties();
    }

    /// <summary>
    /// 位置をランダムにしない設定のときは、生成数を 0 か 1 に制限する（以前の設定値が残って Bake されないようにするため）。
    /// </summary>
    private void ClampScenePositionSpawnCount()
    {
        if (randomizePositionProperty.boolValue)
        {
            return;
        }

        if (spawnCountProperty.intValue > 1)
        {
            spawnCountProperty.intValue = 1;
        }
    }
}
#endif
