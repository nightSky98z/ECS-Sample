using NUnit.Framework;
using System.IO;
using Unity.Mathematics;

public sealed class MonsterDestroySystemTests
{
    [Test]
    public void CalculateProgressClampsToDuration()
    {
        Assert.AreEqual(0f, MonsterDestroyVfxMath.CalculateProgress(-1f, 0.5f));
        Assert.AreEqual(0.5f, MonsterDestroyVfxMath.CalculateProgress(0.25f, 0.5f));
        Assert.AreEqual(1f, MonsterDestroyVfxMath.CalculateProgress(2f, 0.5f));
    }

    [Test]
    public void CalculateScaleShrinksFromOriginalToEndScale()
    {
        Assert.AreEqual(2f, MonsterDestroyVfxMath.CalculateScale(2f, 0.25f, 0f));
        Assert.AreEqual(1.125f, MonsterDestroyVfxMath.CalculateScale(2f, 0.25f, 0.5f));
        Assert.AreEqual(0.25f, MonsterDestroyVfxMath.CalculateScale(2f, 0.25f, 1f));
    }

    [Test]
    public void CalculateBaseColorInterpolatesDestroyMaterialColor()
    {
        var start = new float4(1f, 0f, 0f, 1f);
        var end = new float4(0f, 0f, 0f, 0f);

        var color = MonsterDestroyVfxMath.CalculateBaseColor(start, end, 0.25f);

        Assert.AreEqual(0.75f, color.x, 0.0001f);
        Assert.AreEqual(0f, color.y, 0.0001f);
        Assert.AreEqual(0f, color.z, 0.0001f);
        Assert.AreEqual(0.75f, color.w, 0.0001f);
    }

    [Test]
    public void CalculateHitVfxColorReturnsFromHitColorToRestColor()
    {
        var hit = new float4(1f, 1f, 1f, 1f);
        var rest = new float4(0f, 1f, 0f, 1f);

        var color = MonsterHitVfxMath.CalculateBaseColor(hit, rest, 0.5f);

        Assert.AreEqual(0.5f, color.x, 0.0001f);
        Assert.AreEqual(1f, color.y, 0.0001f);
        Assert.AreEqual(0.5f, color.z, 0.0001f);
        Assert.AreEqual(1f, color.w, 0.0001f);
    }

    [Test]
    public void CalculateHitVfxFirstFrameUsesHitColor()
    {
        var hit = new float4(1f, 0f, 0f, 1f);
        var rest = new float4(0f, 1f, 0f, 1f);
        var progress = MonsterHitVfxMath.CalculateProgress(0f, 0.12f);

        var color = MonsterHitVfxMath.CalculateBaseColor(hit, rest, progress);

        Assert.AreEqual(hit, color);
    }

    [Test]
    public void MonsterEntityBakesDestroyVfxConfig()
    {
        var source = File.ReadAllText("Assets/Scripts/Monster/MonsterEntity.cs");

        StringAssert.Contains("DestroyVfxDuration", source);
        StringAssert.Contains("DestroyVfxStartColor", source);
        StringAssert.Contains("DestroyVfxEndColor", source);
        StringAssert.Contains("MonsterDestroyVfxConfig", source);
        StringAssert.Contains("HitVfxDuration", source);
        StringAssert.Contains("MonsterHitVfxConfig", source);
        StringAssert.Contains("MonsterHitVfxState", source);
    }

    [Test]
    public void MonsterEntityOnlyAddsRecycleTagWhenAuthoringAllowsIt()
    {
        var source = File.ReadAllText("Assets/Scripts/Monster/MonsterEntity.cs");
        var prefab = File.ReadAllText("Assets/Prefab/Monster.prefab");

        StringAssert.Contains("RecycleAfterDeath", source);
        StringAssert.Contains("if (authoring.RecycleAfterDeath)", source);
        StringAssert.Contains("RecycleAfterDeath: 1", prefab);
    }

    [Test]
    public void MonsterRendererBaseColorIsBakedByRendererOwner()
    {
        var source = File.ReadAllText("Assets/Scripts/Monster/MonsterEntity.cs");

        StringAssert.Contains("Baker<Renderer>", source);
        StringAssert.Contains("GetComponentInParent<MonsterEntity>(true)", source);
        StringAssert.Contains("URPMaterialPropertyBaseColor", source);
        Assert.IsFalse(source.Contains("AddMaterialBaseColorComponents(authoring)"));
    }

    [Test]
    public void MonsterDestroySystemStartsDeathStateAndDestroysLinkedGroup()
    {
        var source = File.ReadAllText("Assets/Scripts/Monster/MonsterDestroySystem.cs");

        StringAssert.Contains("MonsterTag", source);
        StringAssert.Contains("HealthComponent", source);
        StringAssert.Contains("MonsterDestroyVfxState", source);
        StringAssert.Contains("RemoveComponent<Velocity>", source);
        StringAssert.Contains("DestroyLinkedEntityGroup", source);
        StringAssert.Contains("LinkedEntityGroup", source);
        Assert.IsFalse(source.Contains("Request"));
    }

    [Test]
    public void RecyclableMonsterIsReusedAfterDeathVfx()
    {
        var source = File.ReadAllText("Assets/Scripts/Monster/MonsterDestroySystem.cs");

        StringAssert.Contains("MonsterRecycleTag", source);
        StringAssert.Contains("TryGetDeathRecycleContext", source);
        StringAssert.Contains("RecycleMonsterAfterDeathVfx", source);
        StringAssert.Contains("RemoveComponent<MonsterDestroyVfxState>", source);
        StringAssert.Contains("CurrentHp = health.MaxHp", source);
    }

    [Test]
    public void DeadMonsterDoesNotMoveDuringDestroyVfx()
    {
        var movementSource = File.ReadAllText("Assets/Scripts/Movement/MovementSystem.cs");
        var physicsSource = File.ReadAllText("Assets/Scripts/Physics/PhysicsSystem.cs");
        var groundSource = File.ReadAllText("Assets/Scripts/Physics/GroundSensorSystem.cs");

        StringAssert.Contains("WithNone<MonsterDestroyVfxState>", movementSource);
        StringAssert.Contains("WithNone<MonsterDestroyVfxState>", physicsSource);
        StringAssert.Contains("WithNone<MonsterDestroyVfxState>", groundSource);
    }

    [Test]
    public void VFXPlaySystemOwnsHitAndDeathMaterialPlayback()
    {
        var source = File.ReadAllText("Assets/Scripts/VFX/VFXPlaySystem.cs");

        StringAssert.Contains("VFXPlaySystem", source);
        StringAssert.Contains("UpdateAfter(typeof(MonsterDestroySystem))", source);
        StringAssert.Contains("MonsterHitVfxState", source);
        StringAssert.Contains("MonsterDestroyVfxState", source);
        StringAssert.Contains("URPMaterialPropertyBaseColor", source);
        StringAssert.Contains("WithNone<MonsterDestroyVfxState>", source);
        StringAssert.Contains("CalculateScale", source);
        StringAssert.Contains("entityCommandBuffer.SetComponent(entity, materialColor)", source);
        StringAssert.Contains("entityCommandBuffer.AddComponent(entity, materialColor)", source);
        Assert.IsFalse(source.Contains("Request"));
    }

    [Test]
    public void MonsterVfxUsesUnityRenderingBaseColorOverride()
    {
        var componentSource = File.ReadAllText("Assets/Scripts/Monster/MonsterDestroyComponents.cs");
        var destroySource = File.ReadAllText("Assets/Scripts/Monster/MonsterDestroySystem.cs");
        var vfxSource = File.ReadAllText("Assets/Scripts/VFX/VFXPlaySystem.cs");

        Assert.IsFalse(componentSource.Contains("MaterialProperty(\"_BaseColor\")"));
        Assert.IsFalse(componentSource.Contains("MonsterMaterialBaseColor"));
        Assert.IsFalse(destroySource.Contains("URPMaterialPropertyBaseColor"));
        StringAssert.Contains("URPMaterialPropertyBaseColor", vfxSource);
    }
}
