using NUnit.Framework;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

public sealed class HealthBarAnchorTests
{
    [Test]
    public void HealthBarAnchorUsesPrefabChildPositionAndRectSize()
    {
        var root = new GameObject("CharacterRoot");
        var healthBar = new GameObject("HP_Bar", typeof(RectTransform));

        healthBar.transform.SetParent(root.transform, false);
        healthBar.transform.localPosition = new Vector3(0f, 2.25f, 0f);

        var rectTransform = healthBar.GetComponent<RectTransform>();

        rectTransform.sizeDelta = new Vector2(84f, 9f);

        try
        {
            Assert.IsTrue(HealthBarAnchorAuthoringUtility.TryCreateHealthBarAnchor(
                root.transform,
                out var anchor));
            Assert.AreEqual(new float3(0f, 2.25f, 0f), anchor.LocalOffset);
            Assert.AreEqual(new float2(84f, 9f), anchor.Size);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void HealthBarAnchorCanUseExplicitMarkerObject()
    {
        var root = new GameObject("CharacterRoot");
        var marker = new GameObject("HealthBarAnchor");

        marker.transform.SetParent(root.transform, false);
        marker.transform.localPosition = new Vector3(0.5f, 3f, 0f);
        marker.AddComponent<HealthBarAnchorAuthoring>();

        try
        {
            Assert.IsTrue(HealthBarAnchorAuthoringUtility.TryCreateHealthBarAnchor(
                root.transform,
                out var anchor));
            Assert.AreEqual(new float3(0.5f, 3f, 0f), anchor.LocalOffset);
            Assert.AreEqual(new float2(100f, 12f), anchor.Size);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void PlayerPrefabContainsHealthBarAnchorMarkerAndMonsterPrefabDoesNot()
    {
        var markerGuid = ReadUnityGuid("Assets/Scripts/Health/HealthBarAnchorAuthoring.cs.meta");
        var playerPrefabText = File.ReadAllText("Assets/Prefab/Player.prefab");
        var monsterPrefabText = File.ReadAllText("Assets/Prefab/Monster.prefab");

        StringAssert.Contains("m_Name: HP_Bar", playerPrefabText);
        StringAssert.Contains($"guid: {markerGuid}", playerPrefabText);
        StringAssert.DoesNotContain("m_Name: HP_Bar", monsterPrefabText);
        StringAssert.DoesNotContain($"guid: {markerGuid}", monsterPrefabText);
    }

    [Test]
    public void HealthBarAnchorPreviewIsEditorOnlyGizmo()
    {
        var gizmoText = File.ReadAllText("Assets/Scripts/Health/HealthBarAnchorAuthoring.EditorGizmos.cs");

        StringAssert.StartsWith("#if UNITY_EDITOR", gizmoText);
        StringAssert.Contains("OnDrawGizmos", gizmoText);
        StringAssert.Contains("Handles.BeginGUI", gizmoText);
        StringAssert.Contains("HandleUtility.WorldToGUIPoint", gizmoText);
        StringAssert.Contains("EditorGUI.DrawRect", gizmoText);
    }

    [Test]
    public void HealthBarAnchorEditorExposesPositionAndSizeControls()
    {
        var gizmoText = File.ReadAllText("Assets/Scripts/Health/HealthBarAnchorAuthoring.EditorGizmos.cs");

        StringAssert.Contains("CustomEditor(typeof(HealthBarAnchorAuthoring))", gizmoText);
        StringAssert.Contains("OnSceneGUI", gizmoText);
        StringAssert.Contains("Handles.PositionHandle", gizmoText);
        StringAssert.Contains("Vector3Field", gizmoText);
        StringAssert.Contains("Vector2Field", gizmoText);
    }

    private static string ReadUnityGuid(string metaPath)
    {
        foreach (var line in File.ReadLines(metaPath))
        {
            const string prefix = "guid: ";

            if (line.StartsWith(prefix))
            {
                return line.Substring(prefix.Length);
            }
        }

        Assert.Fail($"guid が見つかりません: {metaPath}");
        return string.Empty;
    }
}
