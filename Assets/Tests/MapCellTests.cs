using NUnit.Framework;
using System.IO;
using Unity.Mathematics;
using Unity.Transforms;

public sealed class MapCellTests
{
    [Test]
    public void CalculateCellCoordUsesCenteredCellBounds()
    {
        Assert.AreEqual(new int2(0, 0), MapCellUtility.CalculateCellCoord(new float3(0f, 0f, 0f), 100f));
        Assert.AreEqual(new int2(0, 0), MapCellUtility.CalculateCellCoord(new float3(49.9f, 0f, -49.9f), 100f));
        Assert.AreEqual(new int2(1, 0), MapCellUtility.CalculateCellCoord(new float3(50.1f, 0f, 0f), 100f));
        Assert.AreEqual(new int2(-1, 0), MapCellUtility.CalculateCellCoord(new float3(-50.1f, 0f, 0f), 100f));
    }

    [Test]
    public void CalculateCellWorldPositionPlacesPrefabRootAtCellCenter()
    {
        Assert.AreEqual(new float3(0f, 0f, 0f), MapCellUtility.CalculateCellWorldPosition(new int2(0, 0), 100f, 0f));
        Assert.AreEqual(new float3(100f, 2f, -200f), MapCellUtility.CalculateCellWorldPosition(new int2(1, -2), 100f, 2f));
    }

    [Test]
    public void IsInsideLoadRadiusUsesChebyshevDistance()
    {
        var center = new int2(-3, 5);

        Assert.IsTrue(MapCellUtility.IsInsideCellRadius(center, new int2(-2, 4), 1));
        Assert.IsFalse(MapCellUtility.IsInsideCellRadius(center, new int2(-1, 5), 1));
    }

    [Test]
    public void CreateCellRootTransformPreservesPrefabRotationAndScale()
    {
        var prefabTransform = LocalTransform.FromPositionRotationScale(
            new float3(999f, 3f, 999f),
            quaternion.RotateY(0.5f),
            10f);

        var cellTransform = MapCellUtility.CreateCellRootTransform(
            new int2(-2, 1),
            100f,
            0f,
            prefabTransform);

        Assert.AreEqual(new float3(-200f, 0f, 100f), cellTransform.Position);
        Assert.AreEqual(prefabTransform.Rotation.value, cellTransform.Rotation.value);
        Assert.AreEqual(10f, cellTransform.Scale);
    }

    [Test]
    public void CreateCellSeedIsStablePerCellCoord()
    {
        var seedA = MapCellUtility.CreateCellSeed(123, new int2(-10, 4));
        var seedB = MapCellUtility.CreateCellSeed(123, new int2(-10, 4));
        var seedC = MapCellUtility.CreateCellSeed(123, new int2(-11, 4));

        Assert.AreEqual(seedA, seedB);
        Assert.AreNotEqual(seedA, seedC);
        Assert.AreNotEqual(0u, seedA);
    }

    [Test]
    public void SelectWeightedIndexSkipsInvalidCellPrefabs()
    {
        var weights = new[] { 0f, -1f, 2f, 8f };

        Assert.AreEqual(2, MapCellUtility.SelectWeightedIndex(weights, 0f));
        Assert.AreEqual(3, MapCellUtility.SelectWeightedIndex(weights, 2.1f));
    }

    [Test]
    public void MapCellUsesDataFlagsInsteadOfSpawnedMarkerComponents()
    {
        var mapCell = new MapCell
        {
            LocalStaticMeshSpawned = 0,
            MonstersSpawned = 0
        };
        var scriptsText = File.ReadAllText("Assets/Scripts/MapCellAuthoring.cs") +
                          File.ReadAllText("Assets/Scripts/PCGStaticMeshLocalSpawnSystem.cs") +
                          File.ReadAllText("Assets/Scripts/MonsterSpawnDirectorAuthoring.cs") +
                          File.ReadAllText("Assets/Scripts/MapNavAuthoring.cs");

        Assert.AreEqual(0, mapCell.LocalStaticMeshSpawned);
        Assert.AreEqual(0, mapCell.MonstersSpawned);
        Assert.IsFalse(scriptsText.Contains("PCGStaticMeshLocalSpawned"));
        Assert.IsFalse(scriptsText.Contains("MapCellMonsterSpawned"));
        Assert.IsFalse(scriptsText.Contains("MapNavBuildRequest"));
    }

    [Test]
    public void DebugSubSceneMapCellAuthoringHasCellPrefabAssigned()
    {
        var sceneText = File.ReadAllText("Assets/Scenes/SubScenes/SubScene_Sample.unity");

        Assert.IsTrue(sceneText.Contains("CellPrefabs:"));
        Assert.IsFalse(sceneText.Contains("CellPrefab: {fileID: 0}"));
    }

    [Test]
    public void OldPCGStaticMeshCellStreamingSystemIsRemoved()
    {
        Assert.IsFalse(File.Exists("Assets/Scripts/PCGStaticMeshCellSystem.cs"));
    }
}
