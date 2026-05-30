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
    public void CalculateBiasedPlayerRingSpawnPositionUsesForwardHemisphereWhenBiasIsFull()
    {
        var random = new Unity.Mathematics.Random(9876u);
        var playerPosition = new float3(10f, 3f, -5f);
        var preferredDirection = new float2(1f, 0f);

        for (var sampleIndex = 0; sampleIndex < 32; sampleIndex++)
        {
            var position = MonsterSpawnDirectorUtility.CalculateBiasedPlayerRingSpawnPosition(
                playerPosition,
                0f,
                18f,
                42f,
                preferredDirection,
                1f,
                ref random);
            var offset = position.xz - playerPosition.xz;
            var distance = math.length(offset);

            Assert.GreaterOrEqual(math.dot(math.normalizesafe(offset), preferredDirection), -0.0001f);
            Assert.GreaterOrEqual(distance, 18f);
            Assert.LessOrEqual(distance, 42f);
        }
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
    public void ShouldSpawnByIntervalUsesElapsedSeconds()
    {
        Assert.IsTrue(MonsterSpawnDirectorUtility.ShouldSpawnByInterval(0f, 0f));
        Assert.IsFalse(MonsterSpawnDirectorUtility.ShouldSpawnByInterval(0.49f, 0.5f));
        Assert.IsTrue(MonsterSpawnDirectorUtility.ShouldSpawnByInterval(0.5f, 0.5f));
    }

    [Test]
    public void ShouldRecycleByDistanceUsesPlayerDistance()
    {
        Assert.IsFalse(MonsterSpawnDirectorUtility.ShouldRecycleByDistance(
            new float3(3f, 0f, 4f),
            float3.zero,
            5f));
        Assert.IsTrue(MonsterSpawnDirectorUtility.ShouldRecycleByDistance(
            new float3(6f, 0f, 0f),
            float3.zero,
            5f));
        Assert.IsFalse(MonsterSpawnDirectorUtility.ShouldRecycleByDistance(
            new float3(100f, 0f, 0f),
            float3.zero,
            0f));
    }

    [Test]
    public void NormalizeRecycleDistanceKeepsRecycleOutsideSpawnRing()
    {
        Assert.AreEqual(42.01f, MonsterSpawnDirectorUtility.NormalizeRecycleDistance(
            recycleDistance: 20f,
            maxSpawnDistanceFromPlayer: 42f),
            0.0001f);
        Assert.AreEqual(96f, MonsterSpawnDirectorUtility.NormalizeRecycleDistance(
            recycleDistance: 96f,
            maxSpawnDistanceFromPlayer: 42f),
            0.0001f);
    }

    [Test]
    public void MonsterSpawnDirectorRecyclesFarMonstersWithoutDestroyingEntities()
    {
        var source = System.IO.File.ReadAllText("Assets/Scripts/MonsterSpawnDirectorAuthoring.cs");
        var monsterSource = System.IO.File.ReadAllText("Assets/Scripts/MonsterEntity.cs");

        StringAssert.Contains("RecycleFarMonsters", source);
        StringAssert.Contains("RecycleCheckIntervalSeconds", source);
        StringAssert.Contains("MaxRecycleChecksPerFrame", source);
        StringAssert.Contains("recycleScanCursor", source);
        StringAssert.Contains("WithAll<MonsterTag, MonsterRecycleTag>", source);
        StringAssert.Contains("WithNone<MonsterDestroyVfxState>", source);
        StringAssert.Contains("ResetRecycledMonsterRuntimeState", source);
        StringAssert.Contains("CalculateMonsterPlacementTransform", source);
        StringAssert.Contains("AddComponent<MonsterRecycleTag>", monsterSource);
        Assert.IsFalse(source.Contains("DespawnFarMonsters"));
        Assert.IsFalse(source.Contains("DestroyLinkedEntityGroup"));
        Assert.IsFalse(source.Contains("DestroyEntity(rootEntity)"));
    }

    [Test]
    public void MonsterSpawnDirectorPlaysBackSpawnCommandsAfterConfigQuery()
    {
        var source = System.IO.File.ReadAllText("Assets/Scripts/MonsterSpawnDirectorAuthoring.cs");
        var queryIndex = source.IndexOf(
            "SystemAPI.Query<\n                     RefRO<MonsterSpawnDirectorConfig>",
            System.StringComparison.Ordinal);
        var spawnCallIndex = source.IndexOf(
            "SpawnMonstersAroundPlayer(",
            queryIndex,
            System.StringComparison.Ordinal);
        var loopEndIndex = source.IndexOf(
            "\n        }\n\n        if (hasSpawnCommands)",
            spawnCallIndex,
            System.StringComparison.Ordinal);
        var playbackIndex = source.IndexOf(
            "entityCommandBuffer.Playback",
            spawnCallIndex,
            System.StringComparison.Ordinal);

        Assert.GreaterOrEqual(queryIndex, 0);
        Assert.GreaterOrEqual(spawnCallIndex, 0);
        Assert.GreaterOrEqual(loopEndIndex, 0);
        Assert.Greater(playbackIndex, loopEndIndex);
    }

    [Test]
    public void CalculateSpawnCountUnderLimitUsesRemainingCapacity()
    {
        Assert.AreEqual(3, MonsterSpawnDirectorUtility.CalculateSpawnCountUnderLimit(
            requestedSpawnCount: 8,
            maxAliveMonsterCount: 10,
            aliveMonsterCount: 7));
        Assert.AreEqual(0, MonsterSpawnDirectorUtility.CalculateSpawnCountUnderLimit(
            requestedSpawnCount: 8,
            maxAliveMonsterCount: 10,
            aliveMonsterCount: 10));
        Assert.AreEqual(0, MonsterSpawnDirectorUtility.CalculateSpawnCountUnderLimit(
            requestedSpawnCount: 8,
            maxAliveMonsterCount: 0,
            aliveMonsterCount: 0));
    }

    [Test]
    public void CalculateSpawnCountForNearbyDensityRefillsTowardTargetWhenNearbyIsLow()
    {
        Assert.AreEqual(25, MonsterSpawnDirectorUtility.CalculateSpawnCountForNearbyDensity(
            spawnCountPerInterval: 8,
            maxAliveMonsterCount: 100,
            aliveMonsterCount: 40,
            nearbyMonsterCount: 15,
            nearbyLowThreshold: 20,
            nearbyTargetCount: 40));
    }

    [Test]
    public void CalculateSpawnCountForNearbyDensityRespectsGlobalAliveCapacity()
    {
        Assert.AreEqual(3, MonsterSpawnDirectorUtility.CalculateSpawnCountForNearbyDensity(
            spawnCountPerInterval: 8,
            maxAliveMonsterCount: 50,
            aliveMonsterCount: 47,
            nearbyMonsterCount: 10,
            nearbyLowThreshold: 20,
            nearbyTargetCount: 40));
    }

    [Test]
    public void MonsterSpawnDirectorMaintainsNearbyDensityBeforeSpawningNewEntities()
    {
        var source = System.IO.File.ReadAllText("Assets/Scripts/MonsterSpawnDirectorAuthoring.cs");

        StringAssert.Contains("NearbyMonsterTargetCount", source);
        StringAssert.Contains("NearbyMonsterLowThreshold", source);
        StringAssert.Contains("NearbyMonsterRadius", source);
        StringAssert.Contains("ForwardSpawnBias", source);
        StringAssert.Contains("CountAliveMonsterDensity", source);
        StringAssert.Contains("RecycleDistantMonstersForNearbyDensity", source);
        StringAssert.Contains("CalculateSpawnCountForNearbyDensity", source);
    }

    [Test]
    public void NormalizeRecycleChecksPerFrameKeepsPositiveBatchSize()
    {
        var tag = new MonsterRecycleTag();

        Assert.AreEqual(1, MonsterSpawnDirectorUtility.NormalizeRecycleChecksPerFrame(0));
        Assert.AreEqual(100, MonsterSpawnDirectorUtility.NormalizeRecycleChecksPerFrame(100));
        Assert.AreEqual(1, MonsterSpawnDirectorUtility.NormalizeRecycleChecksPerFrame(-4));
        Assert.AreEqual(default(MonsterRecycleTag), tag);
    }

    [Test]
    public void SelectTimedSpawnSettingsUsesHighestReachedStageTuning()
    {
        using var world = new World("MonsterSpawnDirectorStageTuningTests");
        var entityManager = world.EntityManager;
        var owner = entityManager.CreateEntity();
        var tunings = entityManager.AddBuffer<MonsterSpawnStageTuningElement>(owner);

        tunings.Add(new MonsterSpawnStageTuningElement
        {
            MinStageProgress = 0.2f,
            SpawnIntervalSeconds = 0.75f,
            SpawnCountPerInterval = 12
        });
        tunings.Add(new MonsterSpawnStageTuningElement
        {
            MinStageProgress = 0.6f,
            SpawnIntervalSeconds = 0.4f,
            SpawnCountPerInterval = 20
        });

        MonsterSpawnDirectorUtility.SelectTimedSpawnSettings(
            tunings,
            stageProgress: 0.1f,
            baseSpawnIntervalSeconds: 1f,
            baseSpawnCountPerInterval: 8,
            out var earlySpawnIntervalSeconds,
            out var earlySpawnCountPerInterval);
        MonsterSpawnDirectorUtility.SelectTimedSpawnSettings(
            tunings,
            stageProgress: 0.5f,
            baseSpawnIntervalSeconds: 1f,
            baseSpawnCountPerInterval: 8,
            out var middleSpawnIntervalSeconds,
            out var middleSpawnCountPerInterval);
        MonsterSpawnDirectorUtility.SelectTimedSpawnSettings(
            tunings,
            stageProgress: 0.8f,
            baseSpawnIntervalSeconds: 1f,
            baseSpawnCountPerInterval: 8,
            out var lateSpawnIntervalSeconds,
            out var lateSpawnCountPerInterval);

        Assert.AreEqual(1f, earlySpawnIntervalSeconds);
        Assert.AreEqual(8, earlySpawnCountPerInterval);
        Assert.AreEqual(0.75f, middleSpawnIntervalSeconds);
        Assert.AreEqual(12, middleSpawnCountPerInterval);
        Assert.AreEqual(0.4f, lateSpawnIntervalSeconds);
        Assert.AreEqual(20, lateSpawnCountPerInterval);
    }

    [Test]
    public void MonsterSpawnDirectorStopsTimedSpawnAfterStageClear()
    {
        var source = System.IO.File.ReadAllText("Assets/Scripts/MonsterSpawnDirectorAuthoring.cs");
        var clearIndex = source.IndexOf("if (isStageCleared)", System.StringComparison.Ordinal);
        var recycleIndex = source.IndexOf("if (recycleScanActive", System.StringComparison.Ordinal);

        StringAssert.Contains("IsAnyStageCleared", source);
        StringAssert.Contains("isStageCleared", source);
        StringAssert.Contains("continue;", source);
        Assert.GreaterOrEqual(clearIndex, 0);
        Assert.Greater(recycleIndex, clearIndex);
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
