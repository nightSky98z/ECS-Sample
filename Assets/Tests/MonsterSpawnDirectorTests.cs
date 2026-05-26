using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;

public sealed class MonsterSpawnDirectorTests
{
    [Test]
    public void CalculateSpawnPositionIsDeterministicForSameRandomState()
    {
        var randomA = new Unity.Mathematics.Random(123u);
        var randomB = new Unity.Mathematics.Random(123u);
        var playerPosition = new float3(10f, 2f, -5f);

        var positionA = MonsterSpawnDirectorUtility.CalculateSpawnPosition(
            playerPosition,
            20f,
            ref randomA);
        var positionB = MonsterSpawnDirectorUtility.CalculateSpawnPosition(
            playerPosition,
            20f,
            ref randomB);

        Assert.AreEqual(positionA, positionB);
    }

    [Test]
    public void CalculateSpawnPositionKeepsMonsterInsideSpawnRing()
    {
        var random = new Unity.Mathematics.Random(456u);
        var playerPosition = new float3(10f, 2f, -5f);

        var position = MonsterSpawnDirectorUtility.CalculateSpawnPosition(
            playerPosition,
            20f,
            ref random);
        var offset = position.xz - playerPosition.xz;
        var distance = math.length(offset);

        Assert.GreaterOrEqual(distance, 14f);
        Assert.LessOrEqual(distance, 20f);
        Assert.AreEqual(playerPosition.y, position.y);
    }

    [Test]
    public void CalculateCellSpawnPositionKeepsMonsterInsideCellInterior()
    {
        var random = new Unity.Mathematics.Random(789u);
        var cellCenter = new float3(-100f, 0f, 200f);

        var position = MonsterSpawnDirectorUtility.CalculateCellSpawnPosition(
            cellCenter,
            100f,
            8f,
            ref random);

        Assert.GreaterOrEqual(position.x, -142f);
        Assert.LessOrEqual(position.x, -58f);
        Assert.AreEqual(0f, position.y);
        Assert.GreaterOrEqual(position.z, 158f);
        Assert.LessOrEqual(position.z, 242f);
    }

    [Test]
    public void CalculatePlayerRingSpawnPositionKeepsMonsterOutsidePlayerSafeDistance()
    {
        var random = new Unity.Mathematics.Random(321u);
        var playerPosition = new float3(10f, 3f, -5f);

        for (var sampleIndex = 0; sampleIndex < 32; sampleIndex++)
        {
            var position = MonsterSpawnDirectorUtility.CalculatePlayerRingSpawnPosition(
                playerPosition,
                0f,
                18f,
                42f,
                ref random);
            var distance = math.length(position.xz - playerPosition.xz);

            Assert.GreaterOrEqual(distance, 18f);
            Assert.LessOrEqual(distance, 42f);
            Assert.AreEqual(0f, position.y);
        }
    }

    [Test]
    public void CalculatePlayerRingSpawnPositionAcceptsReversedDistanceRange()
    {
        var random = new Unity.Mathematics.Random(6543u);
        var playerPosition = new float3(-20f, 2f, 9f);

        var position = MonsterSpawnDirectorUtility.CalculatePlayerRingSpawnPosition(
            playerPosition,
            1f,
            42f,
            18f,
            ref random);
        var distance = math.length(position.xz - playerPosition.xz);

        Assert.GreaterOrEqual(distance, 18f);
        Assert.LessOrEqual(distance, 42f);
        Assert.AreEqual(1f, position.y);
    }

    [Test]
    public void CalculateGroundedSpawnPositionPlacesGroundSensorBottomOnGround()
    {
        var groundPosition = new float3(12f, 0f, -8f);
        var groundSensor = new GroundSensor
        {
            LocalCenter = new float3(0f, -0.428f, 0f),
            Radius = 0.1f,
            Skin = 0.03f
        };

        var position = MonsterSpawnDirectorUtility.CalculateGroundedSpawnPosition(
            groundPosition,
            groundSensor,
            quaternion.identity,
            1f);

        Assert.AreEqual(12f, position.x);
        Assert.AreEqual(0.528f, position.y, 0.0001f);
        Assert.AreEqual(-8f, position.z);
    }

    [Test]
    public void CalculateCellSpawnCountKeepsValueInsideInclusiveRange()
    {
        var random = new Unity.Mathematics.Random(987u);

        for (var sampleIndex = 0; sampleIndex < 32; sampleIndex++)
        {
            var spawnCount = MonsterSpawnDirectorUtility.CalculateCellSpawnCount(
                3,
                7,
                ref random);

            Assert.GreaterOrEqual(spawnCount, 3);
            Assert.LessOrEqual(spawnCount, 7);
        }
    }

    [Test]
    public void CalculateCellSpawnCountAcceptsReversedRange()
    {
        var random = new Unity.Mathematics.Random(654u);

        for (var sampleIndex = 0; sampleIndex < 32; sampleIndex++)
        {
            var spawnCount = MonsterSpawnDirectorUtility.CalculateCellSpawnCount(
                7,
                3,
                ref random);

            Assert.GreaterOrEqual(spawnCount, 3);
            Assert.LessOrEqual(spawnCount, 7);
        }
    }

    [Test]
    public void SelectMonsterPrefabSkipsZeroWeightEntries()
    {
        using var world = new World("MonsterSpawnDirectorTests");
        var entityManager = world.EntityManager;
        var owner = entityManager.CreateEntity();
        var skippedPrefab = entityManager.CreateEntity();
        var selectedPrefab = entityManager.CreateEntity();
        var prefabs = entityManager.AddBuffer<MonsterSpawnPrefabElement>(owner);

        prefabs.Add(new MonsterSpawnPrefabElement
        {
            Prefab = skippedPrefab,
            Weight = 0f
        });
        prefabs.Add(new MonsterSpawnPrefabElement
        {
            Prefab = selectedPrefab,
            Weight = 2f
        });

        var selected = MonsterSpawnDirectorUtility.SelectMonsterPrefab(prefabs, 0f);

        Assert.AreEqual(selectedPrefab, selected);
    }

    [Test]
    public void SelectMonsterPrefabReturnsNullWhenNoPositiveWeightsExist()
    {
        using var world = new World("MonsterSpawnDirectorTests");
        var entityManager = world.EntityManager;
        var owner = entityManager.CreateEntity();
        var prefab = entityManager.CreateEntity();
        var prefabs = entityManager.AddBuffer<MonsterSpawnPrefabElement>(owner);

        prefabs.Add(new MonsterSpawnPrefabElement
        {
            Prefab = prefab,
            Weight = 0f
        });

        var selected = MonsterSpawnDirectorUtility.SelectMonsterPrefab(prefabs, 0f);

        Assert.AreEqual(Entity.Null, selected);
    }
}
