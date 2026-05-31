using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Transforms;
using Unity.Mathematics;
using UnityEngine;

public sealed class PCGStaticMeshTests
{
    [Test]
    public void NormalizeScaleRangeOrdersValuesAndUsesAbsoluteScale()
    {
        var range = PCGStaticMeshUtility.NormalizeScaleRange(new float2(3f, -1f));

        Assert.AreEqual(new float2(1f, 3f), range);
    }

    [Test]
    public void CreatePlacementIsDeterministicForSameSeed()
    {
        var settings = new PCGStaticMeshPlacementSettings
        {
            Center = new float3(10f, 2f, -5f),
            Rotation = quaternion.RotateY(0.25f),
            AreaSize = new float2(20f, 8f),
            ScaleRange = new float2(0.8f, 1.2f),
            RandomizeYaw = true
        };
        var randomA = new Unity.Mathematics.Random(123u);
        var randomB = new Unity.Mathematics.Random(123u);

        var placementA = PCGStaticMeshUtility.CreatePlacement(settings, ref randomA);
        var placementB = PCGStaticMeshUtility.CreatePlacement(settings, ref randomB);

        Assert.AreEqual(placementA.Position, placementB.Position);
        Assert.AreEqual(placementA.Rotation.value, placementB.Rotation.value);
        Assert.AreEqual(placementA.Scale, placementB.Scale);
    }

    [Test]
    public void CreatePlacementKeepsPositionInsideRotatedAuthoringArea()
    {
        var settings = new PCGStaticMeshPlacementSettings
        {
            Center = new float3(10f, 2f, -5f),
            Rotation = quaternion.identity,
            AreaSize = new float2(20f, 8f),
            ScaleRange = new float2(2f, 4f),
            RandomizeYaw = false
        };
        var random = new Unity.Mathematics.Random(456u);

        var placement = PCGStaticMeshUtility.CreatePlacement(settings, ref random);

        Assert.GreaterOrEqual(placement.Position.x, 0f);
        Assert.LessOrEqual(placement.Position.x, 20f);
        Assert.AreEqual(2f, placement.Position.y);
        Assert.GreaterOrEqual(placement.Position.z, -9f);
        Assert.LessOrEqual(placement.Position.z, -1f);
        Assert.GreaterOrEqual(placement.Scale, 2f);
        Assert.LessOrEqual(placement.Scale, 4f);
    }

    [Test]
    public void CreateWorldPlacementTransformUsesCellPositionButNotCellVisualScale()
    {
        var cellTransform = LocalTransform.FromPositionRotationScale(
            new float3(100f, 0f, -50f),
            quaternion.identity,
            10f);
        var localInstance = new PCGStaticMeshLocalInstance
        {
            Prefab = Unity.Entities.Entity.Null,
            LocalPosition = new float3(5f, 0f, -7f),
            LocalRotation = quaternion.identity,
            Scale = 2f
        };

        var worldTransform = PCGStaticMeshUtility.CreateWorldPlacementTransform(
            cellTransform,
            localInstance);

        Assert.AreEqual(new float3(105f, 0f, -57f), worldTransform.Position);
        Assert.AreEqual(2f, worldTransform.Scale);
    }

    [Test]
    public void CalculateCellCoordUsesFloorForNegativeWorldPositions()
    {
        var coord = PCGStaticMeshUtility.CalculateCellCoord(
            new float3(-0.1f, 0f, 32.1f),
            32f);

        Assert.AreEqual(new int2(-1, 1), coord);
    }

    [Test]
    public void IsInsideCellRadiusUsesChebyshevDistance()
    {
        var center = new int2(10, -5);

        Assert.IsTrue(PCGStaticMeshUtility.IsInsideCellRadius(center, new int2(12, -3), 2));
        Assert.IsFalse(PCGStaticMeshUtility.IsInsideCellRadius(center, new int2(13, -3), 2));
    }

    [Test]
    public void CreateCellPlacementSettingsCentersAreaOnCell()
    {
        var settings = PCGStaticMeshUtility.CreateCellPlacementSettings(
            new int2(-2, 3),
            16f,
            1.5f,
            new float2(0.8f, 1.2f),
            true);

        Assert.AreEqual(new float3(-24f, 1.5f, 56f), settings.Center);
        Assert.AreEqual(new float2(16f, 16f), settings.AreaSize);
        Assert.AreEqual(new float2(0.8f, 1.2f), settings.ScaleRange);
        Assert.IsTrue(settings.RandomizeYaw);
    }

    [Test]
    public void CreateCellSeedIsStablePerWorldSeedAndCellCoord()
    {
        var seedA = PCGStaticMeshUtility.CreateCellSeed(123, new int2(4, -9));
        var seedB = PCGStaticMeshUtility.CreateCellSeed(123, new int2(4, -9));
        var seedC = PCGStaticMeshUtility.CreateCellSeed(123, new int2(5, -9));

        Assert.AreEqual(seedA, seedB);
        Assert.AreNotEqual(seedA, seedC);
        Assert.AreNotEqual(0u, seedA);
    }

    [Test]
    public void GetBakeInstanceCountKeepsZeroAsNoPlacement()
    {
        Assert.AreEqual(0, PCGStaticMeshUtility.GetBakeInstanceCount(-1));
        Assert.AreEqual(0, PCGStaticMeshUtility.GetBakeInstanceCount(0));
        Assert.AreEqual(12, PCGStaticMeshUtility.GetBakeInstanceCount(12));
    }

    [Test]
    public void CalculateLodDistanceUsesWorldSizeAndTransitionHeight()
    {
        var distance = PCGStaticMeshUtility.CalculateLodDistance(10f, 0.5f);

        Assert.AreEqual(20f, distance);
    }

    [Test]
    public void CalculateLodDistanceRejectsZeroTransitionHeight()
    {
        var distance = PCGStaticMeshUtility.CalculateLodDistance(10f, 0f);

        Assert.IsTrue(float.IsPositiveInfinity(distance));
    }

    [Test]
    public void GetMaxAbsAxisScaleUsesLargestMatrixAxis()
    {
        var matrix = Matrix4x4.TRS(
            Vector3.zero,
            Quaternion.identity,
            new Vector3(2f, 3f, 4f));

        var scale = PCGStaticMeshUtility.GetMaxAbsAxisScale(matrix);

        Assert.AreEqual(4f, scale);
    }

    [Test]
    public void CreateBoxGeometryUsesMeshBounds()
    {
        var geometry = PCGStaticMeshUtility.CreateBoxGeometry(
            new Bounds(
                new Vector3(1f, 2f, 3f),
                new Vector3(4f, 5f, 6f)));

        Assert.AreEqual(new float3(1f, 2f, 3f), geometry.Center);
        Assert.AreEqual(new float3(4f, 5f, 6f), geometry.Size);
        Assert.AreEqual(quaternion.identity.value, geometry.Orientation.value);
    }

    [Test]
    public void CreateCapsuleGeometryMatchesUnityCapsuleDirectionY()
    {
        var geometry = PCGStaticMeshUtility.CreateCapsuleGeometry(
            new float3(0f, 10f, 0f),
            0.5f,
            20f,
            1);

        Assert.AreEqual(new float3(0f, 0.5f, 0f), geometry.Vertex0);
        Assert.AreEqual(new float3(0f, 19.5f, 0f), geometry.Vertex1);
        Assert.AreEqual(0.5f, geometry.Radius);
    }

    [Test]
    public void CalculateTotalWeightIgnoresZeroAndNegativeWeights()
    {
        var totalWeight = PCGStaticMeshUtility.CalculateTotalWeight(new[] { 1f, 0f, -2f, 3f });

        Assert.AreEqual(4f, totalWeight);
    }

    [Test]
    public void SelectWeightedIndexUsesCumulativePositiveWeights()
    {
        var weights = new[] { 1f, 3f, 6f };

        Assert.AreEqual(0, PCGStaticMeshUtility.SelectWeightedIndex(weights, 0.25f));
        Assert.AreEqual(1, PCGStaticMeshUtility.SelectWeightedIndex(weights, 1.25f));
        Assert.AreEqual(2, PCGStaticMeshUtility.SelectWeightedIndex(weights, 4.25f));
    }

    [Test]
    public void SelectWeightedIndexSkipsZeroAndNegativeWeights()
    {
        var weights = new[] { 0f, -3f, 2f };

        Assert.AreEqual(2, PCGStaticMeshUtility.SelectWeightedIndex(weights, 0f));
        Assert.AreEqual(2, PCGStaticMeshUtility.SelectWeightedIndex(weights, 1.5f));
    }

    [Test]
    public void SelectWeightedIndexReturnsMinusOneWhenNoPositiveWeightExists()
    {
        var weights = new[] { 0f, -3f };

        Assert.AreEqual(-1, PCGStaticMeshUtility.SelectWeightedIndex(weights, 0f));
    }

    [Test]
    public void PcgStaticMeshPrefabReferencesResolveToAssets()
    {
        var prefabPaths = ResolvePcgStaticMeshPrefabPaths();

        Assert.IsNotEmpty(prefabPaths);
    }

    [Test]
    public void PcgLodPrefabsDoNotUseUnityStaticFlags()
    {
        var prefabPaths = ResolvePcgStaticMeshPrefabPaths();

        for (var prefabIndex = 0; prefabIndex < prefabPaths.Count; prefabIndex++)
        {
            var prefabPath = prefabPaths[prefabIndex];
            var prefabText = File.ReadAllText(prefabPath);

            if (!prefabText.Contains("LODGroup:"))
            {
                continue;
            }

            Assert.IsFalse(
                HasNonZeroStaticEditorFlags(prefabText),
                $"{prefabPath} has Unity Static flags. Runtime-spawned PCG LOD prefabs must stay dynamic for Entities Graphics LOD.");
        }
    }

    private static List<string> ResolvePcgStaticMeshPrefabPaths()
    {
        var templateText = File.ReadAllText("Assets/Prefab/Plane_Template.prefab");
        var matches = Regex.Matches(
            templateText,
            @"Prefab:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32}),\s*type:\s*3\}");
        var prefabPaths = new List<string>();

        for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
        {
            var guid = matches[matchIndex].Groups[1].Value;
            var prefabPath = FindAssetPathByGuid(guid);

            Assert.IsNotNull(prefabPath, $"Missing prefab asset for GUID {guid}.");
            prefabPaths.Add(prefabPath);
        }

        return prefabPaths;
    }

    private static string FindAssetPathByGuid(string guid)
    {
        var metaPaths = Directory.GetFiles("Assets", "*.meta", SearchOption.AllDirectories);

        for (var metaIndex = 0; metaIndex < metaPaths.Length; metaIndex++)
        {
            var metaPath = metaPaths[metaIndex];

            if (File.ReadAllText(metaPath).Contains($"guid: {guid}"))
            {
                return metaPath.Substring(0, metaPath.Length - ".meta".Length);
            }
        }

        return null;
    }

    private static bool HasNonZeroStaticEditorFlags(string prefabText)
    {
        return Regex.IsMatch(prefabText, @"m_StaticEditorFlags:\s*(?!0\b)-?\d+");
    }
}
