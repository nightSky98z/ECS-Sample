using System.IO;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public sealed class SpawnRequestTests
{
    [Test]
    public void SpawnRequestStoresPrefabAndLocalTransform()
    {
        var prefab = new Entity
        {
            Index = 42,
            Version = 7
        };
        var transform = LocalTransform.FromPositionRotationScale(
            new float3(1f, 2f, 3f),
            quaternion.RotateY(0.5f),
            2f);

        var request = new SpawnRequest
        {
            Prefab = prefab,
            Transform = transform
        };

        Assert.AreEqual(prefab, request.Prefab);
        Assert.AreEqual(transform, request.Transform);
    }

    [Test]
    public void SpawnRequestIsBufferElementNotOneShotRequestEntity()
    {
        var spawnSystemText = File.ReadAllText("Assets/Scripts/EntitySpawnSystem.cs");

        Assert.IsTrue(typeof(IBufferElementData).IsAssignableFrom(typeof(SpawnRequest)));
        Assert.IsFalse(typeof(IComponentData).IsAssignableFrom(typeof(SpawnRequest)));
        Assert.IsFalse(spawnSystemText.Contains("DestroyEntity(request"));
        Assert.IsTrue(spawnSystemText.Contains("requests.Clear()"));
    }

    [Test]
    public void SpawnedEntityMarkerIsNotPartOfTheSpawnProtocol()
    {
        var markerType = typeof(SpawnRequest).Assembly.GetType("SpawnEntityComponent");

        Assert.IsNull(markerType);
    }

    [Test]
    public void ScenePositionModeUsesSpawnerPosition()
    {
        var random = new Random(123u);
        var center = new float3(10f, 2f, -5f);

        var position = SpawnTransformUtility.CreateSpawnPosition(
            center,
            false,
            new float2(100f, 100f),
            ref random);

        Assert.AreEqual(center, position);
    }

    [Test]
    public void RandomPositionModeUsesSeedAndArea()
    {
        var randomA = new Random(123u);
        var randomB = new Random(123u);
        var center = new float3(10f, 2f, -5f);
        var areaSize = new float2(20f, 8f);

        var positionA = SpawnTransformUtility.CreateSpawnPosition(center, true, areaSize, ref randomA);
        var positionB = SpawnTransformUtility.CreateSpawnPosition(center, true, areaSize, ref randomB);

        Assert.AreEqual(positionA, positionB);
        Assert.GreaterOrEqual(positionA.x, 0f);
        Assert.LessOrEqual(positionA.x, 20f);
        Assert.AreEqual(2f, positionA.y);
        Assert.GreaterOrEqual(positionA.z, -9f);
        Assert.LessOrEqual(positionA.z, -1f);
    }

    [Test]
    public void NormalizeSeedNeverReturnsZero()
    {
        Assert.AreEqual(1u, SpawnTransformUtility.NormalizeSeed(0));
        Assert.AreEqual(123u, SpawnTransformUtility.NormalizeSeed(123));
    }

    [Test]
    public void EffectiveSpawnCountKeepsZeroAsNoSpawn()
    {
        Assert.AreEqual(0, SpawnTransformUtility.GetEffectiveSpawnCount(0, false));
        Assert.AreEqual(0, SpawnTransformUtility.GetEffectiveSpawnCount(0, true));
    }

    [Test]
    public void EffectiveSpawnCountLimitsScenePositionModeToOne()
    {
        Assert.AreEqual(1, SpawnTransformUtility.GetEffectiveSpawnCount(1, false));
        Assert.AreEqual(1, SpawnTransformUtility.GetEffectiveSpawnCount(20, false));
    }

    [Test]
    public void EffectiveSpawnCountUsesRequestedCountForRandomPositionMode()
    {
        Assert.AreEqual(1, SpawnTransformUtility.GetEffectiveSpawnCount(1, true));
        Assert.AreEqual(20, SpawnTransformUtility.GetEffectiveSpawnCount(20, true));
    }

    [Test]
    public void MonsterPrefabDoesNotUsePlayerAuthoring()
    {
        var playerEntityGuid = ReadUnityGuid("Assets/Scripts/PlayerEntity.cs.meta");
        var monsterPrefabText = File.ReadAllText("Assets/Prefab/Monster.prefab");

        StringAssert.DoesNotContain($"guid: {playerEntityGuid}", monsterPrefabText);
    }

    [Test]
    public void CharacterPrefabsDoNotUseRigidbody()
    {
        var playerPrefabText = File.ReadAllText("Assets/Prefab/Player.prefab");
        var monsterPrefabText = File.ReadAllText("Assets/Prefab/Monster.prefab");

        StringAssert.DoesNotContain("Rigidbody:", playerPrefabText);
        StringAssert.DoesNotContain("Rigidbody:", monsterPrefabText);
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
