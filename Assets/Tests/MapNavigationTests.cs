using NUnit.Framework;
using System.IO;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

public sealed class MapNavigationTests
{
    [Test]
    public void CalculateCellOriginCentersGridOnCell()
    {
        var origin = MapNavUtility.CalculateCellOrigin(
            new float3(100f, 0f, -50f),
            new int2(50, 50),
            2f);

        Assert.AreEqual(new float3(50f, 0f, -100f), origin);
    }

    [Test]
    public void WorldToTileUsesCellOriginAndTileSize()
    {
        var tile = MapNavUtility.WorldToTile(
            new float3(53.9f, 0f, -96.1f),
            new float3(50f, 0f, -100f),
            2f);

        Assert.AreEqual(new int2(1, 1), tile);
    }

    [Test]
    public void DoesTileOverlapAabbExpandsTileByAgentRadius()
    {
        var obstacleAabb = new Aabb
        {
            Min = new float3(1.25f, -1f, 1.25f),
            Max = new float3(1.75f, 1f, 1.75f)
        };
        var tileCenter = new float3(0f, 0f, 0f);

        Assert.IsFalse(MapNavUtility.DoesTileOverlapAabb(tileCenter, 1f, 0f, obstacleAabb));
        Assert.IsTrue(MapNavUtility.DoesTileOverlapAabb(tileCenter, 1f, 1f, obstacleAabb));
    }

    [Test]
    public void RebuildFlowFieldPointsTowardGoal()
    {
        using var world = new World("MapNavigationTests");
        var entityManager = world.EntityManager;
        var entity = entityManager.CreateEntity();
        var tiles = entityManager.AddBuffer<MapNavTile>(entity);
        var gridSize = new int2(3, 3);

        for (var tileIndex = 0; tileIndex < 9; tileIndex++)
        {
            tiles.Add(new MapNavTile
            {
                Walkable = 1,
                Cost = MapNavUtility.UnreachableCost,
                Direction = float2.zero
            });
        }

        MapNavUtility.RebuildFlowField(tiles, gridSize, new int2(1, 1));

        var leftTile = tiles[MapNavUtility.GetTileIndex(new int2(0, 1), gridSize)];
        var goalTile = tiles[MapNavUtility.GetTileIndex(new int2(1, 1), gridSize)];

        Assert.AreEqual(1, leftTile.Cost);
        Assert.AreEqual(new float2(1f, 0f), leftTile.Direction);
        Assert.AreEqual(0, goalTile.Cost);
        Assert.AreEqual(float2.zero, goalTile.Direction);
    }

    [Test]
    public void RebuildFlowFieldDoesNotCrossBlockedTile()
    {
        using var world = new World("MapNavigationTests");
        var entityManager = world.EntityManager;
        var entity = entityManager.CreateEntity();
        var tiles = entityManager.AddBuffer<MapNavTile>(entity);
        var gridSize = new int2(3, 1);

        tiles.Add(new MapNavTile { Walkable = 1, Cost = MapNavUtility.UnreachableCost });
        tiles.Add(new MapNavTile { Walkable = 0, Cost = MapNavUtility.UnreachableCost });
        tiles.Add(new MapNavTile { Walkable = 1, Cost = MapNavUtility.UnreachableCost });

        MapNavUtility.RebuildFlowField(tiles, gridSize, new int2(2, 0));

        Assert.AreEqual(MapNavUtility.UnreachableCost, tiles[0].Cost);
        Assert.AreEqual(float2.zero, tiles[0].Direction);
    }

    [Test]
    public void RebuildFlowFieldAllowsDiagonalButForbidsCornerCut()
    {
        using var world = new World("MapNavigationTests");
        var entityManager = world.EntityManager;
        var entity = entityManager.CreateEntity();
        var tiles = entityManager.AddBuffer<MapNavTile>(entity);
        var gridSize = new int2(3, 3);

        for (var tileIndex = 0; tileIndex < 9; tileIndex++)
        {
            tiles.Add(new MapNavTile
            {
                Walkable = 1,
                Cost = MapNavUtility.UnreachableCost,
                Direction = float2.zero
            });
        }

        var blockedNorthIndex = MapNavUtility.GetTileIndex(new int2(1, 2), gridSize);
        var blockedNorth = tiles[blockedNorthIndex];

        blockedNorth.Walkable = 0;
        tiles[blockedNorthIndex] = blockedNorth;

        MapNavUtility.RebuildFlowField(tiles, gridSize, new int2(2, 2));

        var cornerTile = tiles[MapNavUtility.GetTileIndex(new int2(1, 1), gridSize)];

        Assert.AreEqual(new float2(1f, 0f), cornerTile.Direction);
    }

    [Test]
    public void CalculateGoalExitDirectionPointsFromEdgeTileToPlayerOutsideCell()
    {
        var direction = MapNavUtility.CalculateGoalExitDirection(
            new float3(5f, 0f, 1.5f),
            new float3(0f, 0f, 0f),
            new int2(2, 1),
            1f);

        Assert.AreEqual(new float2(1f, 0f), direction);
    }

    [Test]
    public void TrySampleDirectionUsesNearbyReachableTileWhenCurrentTileIsBlocked()
    {
        using var world = new World("MapNavigationTests");
        var entityManager = world.EntityManager;
        var entity = entityManager.CreateEntity();
        var tiles = entityManager.AddBuffer<MapNavTile>(entity);
        var gridSize = new int2(3, 3);

        for (var tileIndex = 0; tileIndex < 9; tileIndex++)
        {
            tiles.Add(new MapNavTile
            {
                Walkable = 1,
                Cost = 1,
                Direction = new float2(1f, 0f)
            });
        }

        var blockedTileIndex = MapNavUtility.GetTileIndex(new int2(1, 1), gridSize);
        var blockedTile = tiles[blockedTileIndex];

        blockedTile.Walkable = 0;
        blockedTile.Cost = MapNavUtility.UnreachableCost;
        blockedTile.Direction = float2.zero;
        tiles[blockedTileIndex] = blockedTile;

        var navCell = new MapNavCellData
        {
            GridSize = gridSize,
            TileSize = 1f,
            AgentRadius = 0.5f,
            Origin = float3.zero
        };
        var foundDirection = MapNavUtility.TrySampleDirection(
            tiles,
            navCell,
            new float3(1.5f, 0f, 1.5f),
            out var direction);

        Assert.IsTrue(foundDirection);
        Assert.AreEqual(new float2(1f, 0f), direction);
    }

    [Test]
    public void DebugSubSceneHasMapNavAuthoring()
    {
        var sceneText = File.ReadAllText("Assets/Scenes/SubScenes/SubScene_Sample.unity");

        Assert.IsTrue(sceneText.Contains("MapNavAuthoring"));
        Assert.IsTrue(sceneText.Contains("GridSize: {x: 50, y: 50}"));
    }
}
