using NUnit.Framework;
using System.IO;
using Unity.Mathematics;

public sealed class MapStreamingTests
{
    [Test]
    public void StreamingRadiiKeepNestedBoundaries()
    {
        Assert.AreEqual(0, MapStreamingUtility.NormalizeActiveRadius(-2));
        Assert.AreEqual(2, MapStreamingUtility.NormalizePreloadRadius(activeRadius: 2, preloadRadius: 1));
        Assert.AreEqual(3, MapStreamingUtility.NormalizeUnloadRadius(preloadRadius: 3, unloadRadius: 1));
    }

    [Test]
    public void PreloadCanSkipCellsBehindPlayerDirection()
    {
        var center = int2.zero;
        var forward = new float2(0f, 1f);

        Assert.IsTrue(MapStreamingUtility.ShouldLoadCell(
            center,
            new int2(0, -1),
            activeRadius: 1,
            preloadRadius: 2,
            preferredDirection: forward,
            preloadBehindCells: 0,
            forwardPreloadDotThreshold: -0.25f));

        Assert.IsFalse(MapStreamingUtility.ShouldLoadCell(
            center,
            new int2(0, -2),
            activeRadius: 1,
            preloadRadius: 2,
            preferredDirection: forward,
            preloadBehindCells: 0,
            forwardPreloadDotThreshold: -0.25f));

        Assert.IsTrue(MapStreamingUtility.ShouldLoadCell(
            center,
            new int2(2, 0),
            activeRadius: 1,
            preloadRadius: 2,
            preferredDirection: forward,
            preloadBehindCells: 0,
            forwardPreloadDotThreshold: -0.25f));
    }

    [Test]
    public void LoadPriorityProcessesActiveCellsBeforePreloadCells()
    {
        var center = int2.zero;

        Assert.Less(
            MapStreamingUtility.CalculateLoadPriority(center, new int2(1, 0), activeRadius: 1),
            MapStreamingUtility.CalculateLoadPriority(center, new int2(2, 0), activeRadius: 1));
    }

    [Test]
    public void EmergencyCreateBudgetCoversMissingActiveCells()
    {
        Assert.AreEqual(
            9,
            MapStreamingUtility.CalculateEmergencyCreateBudget(
                normalBudget: 2,
                missingActiveCellCount: 9));
        Assert.AreEqual(
            4,
            MapStreamingUtility.CalculateEmergencyCreateBudget(
                normalBudget: 4,
                missingActiveCellCount: 1));
    }

    [Test]
    public void ActiveStreamingGapRaisesEmergencyCreateBudget()
    {
        Assert.IsTrue(MapStreamingUtility.NeedsEmergencyActiveCellCreation(
            missingActiveCellCount: 1,
            hasUnreadyActiveCell: false));
        Assert.IsTrue(MapStreamingUtility.NeedsEmergencyActiveCellCreation(
            missingActiveCellCount: 0,
            hasUnreadyActiveCell: true));
        Assert.IsFalse(MapStreamingUtility.NeedsEmergencyActiveCellCreation(
            missingActiveCellCount: 0,
            hasUnreadyActiveCell: false));
    }

    [Test]
    public void UnloadingCellIsNotReusableForCoordinateLookup()
    {
        Assert.IsTrue(MapStreamingUtility.CanReuseExistingCellForCoordLookup(hasUnloadState: false));
        Assert.IsFalse(MapStreamingUtility.CanReuseExistingCellForCoordLookup(hasUnloadState: true));
    }

    [Test]
    public void RadiusOneCellSetContainsNineCells()
    {
        Assert.AreEqual(1, MapStreamingUtility.CalculateCellCountInRadius(0));
        Assert.AreEqual(9, MapStreamingUtility.CalculateCellCountInRadius(1));
        Assert.AreEqual(25, MapStreamingUtility.CalculateCellCountInRadius(2));
    }

    [Test]
    public void OwnedEntityDestroyCursorAdvancesOnlyWithinFrameBudget()
    {
        Assert.AreEqual(
            3,
            MapStreamingUtility.CalculateNextOwnedEntityDestroyIndex(
                currentIndex: 0,
                ownedEntityCount: 10,
                frameBudget: 3));
        Assert.AreEqual(
            10,
            MapStreamingUtility.CalculateNextOwnedEntityDestroyIndex(
                currentIndex: 8,
                ownedEntityCount: 10,
                frameBudget: 3));
        Assert.AreEqual(
            1,
            MapStreamingUtility.CalculateNextOwnedEntityDestroyIndex(
                currentIndex: -10,
                ownedEntityCount: 10,
                frameBudget: 0));
    }

    [Test]
    public void StaticMeshPriorityUsesVisibleGroundRadiusAndCellBounds()
    {
        var playerPosition = new float2(49f, 0f);

        Assert.IsTrue(MapStreamingUtility.ShouldPrioritizeStaticMeshSpawn(
            playerPosition,
            new int2(1, 0),
            cellSize: 100f,
            visibleGroundRadius: 8f,
            visiblePadding: 0f));

        Assert.IsFalse(MapStreamingUtility.ShouldPrioritizeStaticMeshSpawn(
            float2.zero,
            new int2(2, 0),
            cellSize: 100f,
            visibleGroundRadius: 8f,
            visiblePadding: 0f));
    }

    [Test]
    public void StaticMeshPriorityCanUsePaddingForNearFutureScreenArea()
    {
        Assert.IsFalse(MapStreamingUtility.ShouldPrioritizeStaticMeshSpawn(
            float2.zero,
            new int2(1, 0),
            cellSize: 100f,
            visibleGroundRadius: 40f,
            visiblePadding: 0f));

        Assert.IsTrue(MapStreamingUtility.ShouldPrioritizeStaticMeshSpawn(
            float2.zero,
            new int2(1, 0),
            cellSize: 100f,
            visibleGroundRadius: 40f,
            visiblePadding: 12f));
    }

    [Test]
    public void CenterCellStaticMeshSpawnUsesFrameBudget()
    {
        var source = File.ReadAllText("Assets/Scripts/PCG/PCGStaticMeshLocalSpawnSystem.cs");

        StringAssert.Contains("MaxVisibleStaticMeshSpawnsPerFrame", source);
        Assert.IsFalse(source.Contains("int.MaxValue"));
    }
}
