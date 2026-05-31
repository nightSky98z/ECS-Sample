#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

/// <summary>
/// PCGStaticMeshAuthoring の Scene View preview。
/// </summary>
public partial class PCGStaticMeshAuthoring
{
    private const int MaxPreviewInstances = 512;

    [SerializeField]
    [Tooltip("シーンビューに PCG 配置プレビューを描画する。プレイヤービルドには含まれない。")]
    private bool DrawPlacementPreview = true;

    private readonly List<MeshFilter> PreviewMeshFilters = new List<MeshFilter>();

    /// <summary>
    /// 選択時だけ、Bake と同じ seed / area / scale から予定配置を描画する。
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (!DrawPlacementPreview)
        {
            return;
        }

        var instanceCount = PCGStaticMeshUtility.GetBakeInstanceCount(InstanceCount);

        if (instanceCount <= 0)
        {
            return;
        }

        var prefabs = CollectPreviewPrefabs();

        if (prefabs.Count == 0)
        {
            return;
        }

        var previewedInstances = 0;
        var authoringRotation = ToMathQuaternion(transform.rotation);
        var settings = PCGStaticMeshUtility.CreatePlacementSettings(
            (float3)transform.position + math.rotate(authoringRotation, new float3(0f, GroundY, 0f)),
            authoringRotation,
            new float2(AreaSize.x, AreaSize.y),
            new float2(ScaleRange.x, ScaleRange.y),
            RandomizeYaw);
        var random = new Unity.Mathematics.Random(
            PCGStaticMeshUtility.NormalizeSeed(RandomSeed));

        for (var instanceIndex = 0; instanceIndex < instanceCount; instanceIndex++)
        {
            if (previewedInstances >= MaxPreviewInstances)
            {
                return;
            }

            var prefab = SelectWeightedPrefab(prefabs, ref random);

            if (prefab == null)
            {
                return;
            }

            var placement = PCGStaticMeshUtility.CreatePlacement(settings, ref random);

            DrawPrefabPreview(prefab, placement);
            previewedInstances++;
        }
    }

    private List<PCGStaticMeshPrefabCandidate> CollectPreviewPrefabs()
    {
        var entries = GetEffectiveStaticMeshEntries();
        var prefabs = new List<PCGStaticMeshPrefabCandidate>();

        for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
        {
            var entry = entries[entryIndex];
            var prefab = entry.Prefab;
            var weight = PCGStaticMeshUtility.NormalizeWeight(entry.Weight);

            if (prefab != null && weight > 0f)
            {
                prefabs.Add(new PCGStaticMeshPrefabCandidate
                {
                    Prefab = prefab,
                    Weight = weight
                });
            }
        }

        return prefabs;
    }

    /// <summary>
    /// Prefab の MeshFilter を使って、Bake 予定位置に prefab 形状を描画する。
    /// </summary>
    private void DrawPrefabPreview(GameObject prefab, PCGStaticMeshPlacement placement)
    {
        PreviewMeshFilters.Clear();
        prefab.GetComponentsInChildren(true, PreviewMeshFilters);

        if (PreviewMeshFilters.Count == 0)
        {
            return;
        }

        var rootMatrix = Matrix4x4.TRS(
            (Vector3)placement.Position,
            ToUnityQuaternion(placement.Rotation),
            Vector3.one * placement.Scale);
        var prefabRootToLocal = prefab.transform.worldToLocalMatrix;

        for (var meshFilterIndex = 0; meshFilterIndex < PreviewMeshFilters.Count; meshFilterIndex++)
        {
            var meshFilter = PreviewMeshFilters[meshFilterIndex];
            var mesh = meshFilter.sharedMesh;
            var meshRenderer = meshFilter.GetComponent<MeshRenderer>();

            if (mesh == null || meshRenderer == null || !meshRenderer.enabled)
            {
                continue;
            }

            var localMatrix = prefabRootToLocal * meshFilter.transform.localToWorldMatrix;
            var previewMatrix = rootMatrix * localMatrix;

            DrawMaterialMesh(mesh, meshRenderer.sharedMaterials, previewMatrix);
        }
    }

    /// <summary>
    /// Prefab の material で、Gizmo 用の追加色を乗せずに mesh だけを描画する。
    /// </summary>
    private static void DrawMaterialMesh(Mesh mesh, Material[] materials, Matrix4x4 matrix)
    {
        var subMeshCount = math.min(mesh.subMeshCount, materials.Length);

        for (var subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
        {
            var material = materials[subMeshIndex];

            if (material == null || !material.SetPass(0))
            {
                continue;
            }

            Graphics.DrawMeshNow(mesh, matrix, subMeshIndex);
        }
    }

    private static Quaternion ToUnityQuaternion(quaternion rotation)
    {
        return new Quaternion(rotation.value.x, rotation.value.y, rotation.value.z, rotation.value.w);
    }

    private static quaternion ToMathQuaternion(Quaternion rotation)
    {
        return new quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
    }
}

/// <summary>
/// PCGStaticMeshAuthoring の Inspector 表示を制御する Editor。
/// </summary>
[CustomEditor(typeof(PCGStaticMeshAuthoring))]
public sealed class PCGStaticMeshAuthoringEditor : Editor
{
    private SerializedProperty drawPlacementPreviewProperty;
    private SerializedProperty staticMeshEntriesProperty;
    private SerializedProperty legacyStaticMeshPrefabsProperty;
    private SerializedProperty instanceCountProperty;
    private SerializedProperty randomSeedProperty;
    private SerializedProperty areaSizeProperty;
    private SerializedProperty groundYProperty;
    private SerializedProperty scaleRangeProperty;
    private SerializedProperty randomizeYawProperty;

    private void OnEnable()
    {
        drawPlacementPreviewProperty = serializedObject.FindProperty("DrawPlacementPreview");
        staticMeshEntriesProperty = serializedObject.FindProperty("StaticMeshEntries");
        legacyStaticMeshPrefabsProperty = serializedObject.FindProperty("StaticMeshPrefabs");
        instanceCountProperty = serializedObject.FindProperty("InstanceCount");
        randomSeedProperty = serializedObject.FindProperty("RandomSeed");
        areaSizeProperty = serializedObject.FindProperty("AreaSize");
        groundYProperty = serializedObject.FindProperty("GroundY");
        scaleRangeProperty = serializedObject.FindProperty("ScaleRange");
        randomizeYawProperty = serializedObject.FindProperty("RandomizeYaw");
    }

    /// <summary>
    /// 配置生成に必要な値を明示的な順序で表示する。
    /// </summary>
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        MigrateLegacySerializedPrefabs();

        EditorGUILayout.PropertyField(drawPlacementPreviewProperty);
        EditorGUILayout.PropertyField(staticMeshEntriesProperty, true);
        DrawLodStaticPrefabWarning();
        EditorGUILayout.PropertyField(instanceCountProperty);
        EditorGUILayout.PropertyField(randomSeedProperty);
        EditorGUILayout.PropertyField(areaSizeProperty);
        EditorGUILayout.PropertyField(groundYProperty);
        EditorGUILayout.PropertyField(scaleRangeProperty);
        EditorGUILayout.PropertyField(randomizeYawProperty);

        ClampSerializedValues();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawLodStaticPrefabWarning()
    {
        var staticLodPrefabs = CollectStaticLodPrefabs();

        if (staticLodPrefabs.Count == 0)
        {
            return;
        }

        EditorGUILayout.HelpBox(
            "LODGroup 付き prefab が Unity Static になっています。PCG は実行時に Entity を生成するため、Static のままだと Entities Graphics の最適化で LOD が無効になります。",
            MessageType.Warning);

        if (GUILayout.Button("PCG LOD Prefab の Static を解除"))
        {
            for (var prefabIndex = 0; prefabIndex < staticLodPrefabs.Count; prefabIndex++)
            {
                ClearStaticFlagsInHierarchy(staticLodPrefabs[prefabIndex]);
            }

            AssetDatabase.SaveAssets();
        }
    }

    private List<GameObject> CollectStaticLodPrefabs()
    {
        var prefabs = new List<GameObject>();

        for (var entryIndex = 0; entryIndex < staticMeshEntriesProperty.arraySize; entryIndex++)
        {
            var entryProperty = staticMeshEntriesProperty.GetArrayElementAtIndex(entryIndex);
            var prefabProperty = entryProperty.FindPropertyRelative("Prefab");
            var prefab = prefabProperty.objectReferenceValue as GameObject;

            if (prefab == null ||
                prefab.GetComponentInChildren<LODGroup>(true) == null ||
                !HasStaticFlagsInHierarchy(prefab))
            {
                continue;
            }

            if (!prefabs.Contains(prefab))
            {
                prefabs.Add(prefab);
            }
        }

        return prefabs;
    }

    private static bool HasStaticFlagsInHierarchy(GameObject root)
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);

        for (var transformIndex = 0; transformIndex < transforms.Length; transformIndex++)
        {
            if (GameObjectUtility.GetStaticEditorFlags(transforms[transformIndex].gameObject) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearStaticFlagsInHierarchy(GameObject root)
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);

        for (var transformIndex = 0; transformIndex < transforms.Length; transformIndex++)
        {
            var gameObject = transforms[transformIndex].gameObject;

            Undo.RecordObject(gameObject, "Clear PCG LOD Prefab Static Flags");
            GameObjectUtility.SetStaticEditorFlags(gameObject, (StaticEditorFlags)0);
            EditorUtility.SetDirty(gameObject);
        }
    }

    private void MigrateLegacySerializedPrefabs()
    {
        if (staticMeshEntriesProperty.arraySize > 0 ||
            legacyStaticMeshPrefabsProperty == null ||
            legacyStaticMeshPrefabsProperty.arraySize == 0)
        {
            return;
        }

        staticMeshEntriesProperty.arraySize = legacyStaticMeshPrefabsProperty.arraySize;

        for (var prefabIndex = 0; prefabIndex < legacyStaticMeshPrefabsProperty.arraySize; prefabIndex++)
        {
            var legacyPrefabProperty = legacyStaticMeshPrefabsProperty.GetArrayElementAtIndex(prefabIndex);
            var entryProperty = staticMeshEntriesProperty.GetArrayElementAtIndex(prefabIndex);

            entryProperty.FindPropertyRelative("Prefab").objectReferenceValue =
                legacyPrefabProperty.objectReferenceValue;
            entryProperty.FindPropertyRelative("Weight").floatValue = 1f;
        }
    }

    private void ClampSerializedValues()
    {
        if (instanceCountProperty.intValue < 0)
        {
            instanceCountProperty.intValue = 0;
        }

        if (randomSeedProperty.intValue == 0)
        {
            randomSeedProperty.intValue = 1;
        }

        areaSizeProperty.vector2Value = new Vector2(
            math.max(0f, math.abs(areaSizeProperty.vector2Value.x)),
            math.max(0f, math.abs(areaSizeProperty.vector2Value.y)));

        var scaleRange = PCGStaticMeshUtility.NormalizeScaleRange(
            new float2(scaleRangeProperty.vector2Value.x, scaleRangeProperty.vector2Value.y));

        scaleRangeProperty.vector2Value = new Vector2(scaleRange.x, scaleRange.y);

        for (var entryIndex = 0; entryIndex < staticMeshEntriesProperty.arraySize; entryIndex++)
        {
            var entryProperty = staticMeshEntriesProperty.GetArrayElementAtIndex(entryIndex);
            var prefabProperty = entryProperty.FindPropertyRelative("Prefab");
            var weightProperty = entryProperty.FindPropertyRelative("Weight");

            if (prefabProperty.objectReferenceValue == null && weightProperty.floatValue == 0f)
            {
                weightProperty.floatValue = 1f;
            }

            weightProperty.floatValue = PCGStaticMeshUtility.NormalizeWeight(weightProperty.floatValue);
        }
    }
}
#endif
