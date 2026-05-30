using NUnit.Framework;
using System.IO;
using UnityEngine;

public sealed class DebugOptionsOverlayTests
{
    [Test]
    public void CalculateInstantFpsConvertsPositiveDeltaTimeToFramesPerSecond()
    {
        var fps = DebugOptionsOverlayMath.CalculateInstantFps(1f / 60f);

        Assert.AreEqual(60f, fps, 0.001f);
    }

    [Test]
    public void CalculateInstantFpsReturnsZeroForInvalidDeltaTime()
    {
        Assert.AreEqual(0f, DebugOptionsOverlayMath.CalculateInstantFps(0f));
        Assert.AreEqual(0f, DebugOptionsOverlayMath.CalculateInstantFps(-0.1f));
    }

    [Test]
    public void ClampWindowRectKeepsWindowInsideScreenAndAppliesMinimumSize()
    {
        var rect = DebugOptionsOverlayMath.ClampWindowRect(
            new Rect(900f, -20f, 100f, 80f),
            800f,
            600f,
            new Vector2(280f, 180f));

        Assert.AreEqual(new Rect(520f, 0f, 280f, 180f), rect);
    }

    [Test]
    public void CalculateDefaultWindowRectPlacesWindowOnRightSide()
    {
        var rect = DebugOptionsOverlayMath.CalculateDefaultWindowRect(
            1200f,
            800f,
            new Vector2(300f, 240f),
            16f);

        Assert.AreEqual(new Rect(884f, 16f, 300f, 240f), rect);
    }

    [Test]
    public void RuntimeDrawerKnowsWhenAnyRuntimeOptionIsEnabled()
    {
        Assert.IsFalse(DebugOptionsRuntimeDrawer.HasAnyOptionEnabled(false, false, false));
        Assert.IsTrue(DebugOptionsRuntimeDrawer.HasAnyOptionEnabled(true, false, false));
        Assert.IsTrue(DebugOptionsRuntimeDrawer.HasAnyOptionEnabled(false, true, false));
        Assert.IsTrue(DebugOptionsRuntimeDrawer.HasAnyOptionEnabled(false, false, true));
    }

    [Test]
    public void SkillAttackRangeDebugFadeUsesConfiguredDuration()
    {
        Assert.AreEqual(1f, SkillAttackRangeDebugMath.CalculateFadeAlpha(0f, 5f));
        Assert.AreEqual(0.5f, SkillAttackRangeDebugMath.CalculateFadeAlpha(2.5f, 5f));
        Assert.AreEqual(0f, SkillAttackRangeDebugMath.CalculateFadeAlpha(5f, 5f));
        Assert.AreEqual(0f, SkillAttackRangeDebugMath.CalculateFadeAlpha(1f, 0f));
    }

    [Test]
    public void SkillAttackRangeDebugMathPrunesExpiredEvents()
    {
        Assert.IsFalse(SkillAttackRangeDebugMath.IsEventAlive(10f, 16f, 5f));
        Assert.IsTrue(SkillAttackRangeDebugMath.IsEventAlive(10f, 14.9f, 5f));
    }

    [Test]
    public void SkillRangeRendererCalculatesWorldSpaceCirclePoint()
    {
        var center = new Unity.Mathematics.float3(10f, 1f, -3f);
        var point = DebugOptionsRangeRendererMath.CalculateCirclePoint(
            center,
            radius: 4f,
            segmentIndex: 12,
            segmentCount: 48);

        Assert.AreEqual(10f, point.x, 0.0001f);
        Assert.AreEqual(1f, point.y, 0.0001f);
        Assert.AreEqual(1f, point.z, 0.0001f);
    }

    [Test]
    public void DebugOptionsExposeRuntimeColliderAndSkillRangeToggles()
    {
        var source = File.ReadAllText("Assets/Scripts/DebugOptionsOverlay.cs");

        StringAssert.Contains("Player Collider Wireframe", source);
        StringAssert.Contains("Skill Target Range", source);
        StringAssert.Contains("Skill Attack Range", source);
        StringAssert.Contains("DebugOptionsRuntimeDrawer.Draw", source);
        StringAssert.Contains("LateUpdate", source);
    }

    [Test]
    public void DebugOptionsRuntimeDrawerReadsColliderAndSkillData()
    {
        var source = File.ReadAllText("Assets/Scripts/DebugOptionsOverlay.cs");

        StringAssert.Contains("CollisionRadius", source);
        StringAssert.Contains("GroundSensor", source);
        StringAssert.Contains("PhysicsCollider", source);
        StringAssert.Contains("ComponentType.ReadOnly<PlayerTag>()", source);
        StringAssert.Contains("AttackSkillConfig", source);
        StringAssert.Contains("AttackSkillState", source);
        StringAssert.Contains("CalculateEffectiveTargetRange", source);
        StringAssert.Contains("SkillAttackRangeDebugEvents", source);
    }

    [Test]
    public void DebugOptionsKeepTargetRangePersistentAndAttackRangeAsFadeEvent()
    {
        var overlaySource = File.ReadAllText("Assets/Scripts/DebugOptionsOverlay.cs");
        var skillSource = File.ReadAllText("Assets/Scripts/SkillSystems.cs");

        StringAssert.Contains("AttackRangeFadeSeconds", overlaySource);
        StringAssert.Contains("LineRenderer", overlaySource);
        StringAssert.Contains("DrawSkillTargetRanges", overlaySource);
        StringAssert.Contains("DrawSkillAttackRangeEvents", overlaySource);
        StringAssert.Contains("SkillAttackRangeDebugEvents.Record", skillSource);
    }

    [Test]
    public void DebugOptionsDisposesRuntimeRangeRenderersOnShutdown()
    {
        var source = File.ReadAllText("Assets/Scripts/DebugOptionsOverlay.cs");

        StringAssert.Contains("OnDisable", source);
        StringAssert.Contains("OnDestroy", source);
        StringAssert.Contains("DebugOptionsRuntimeDrawer.Dispose", source);
        StringAssert.Contains("DebugOptionsRangeRenderer.DisposeAll", source);
        StringAssert.Contains("SkillAttackRangeDebugEvents.Clear", source);
        StringAssert.Contains("playModeStateChanged", source);
        Assert.IsFalse(source.Contains("DestroyLeakedGameObjects"));
        Assert.IsFalse(source.Contains("Resources.FindObjectsOfTypeAll<GameObject>"));
    }

    [Test]
    public void DebugOptionsWindowDoesNotUseRuntimeImgui()
    {
        var source = File.ReadAllText("Assets/Scripts/DebugOptionsOverlay.cs");

        StringAssert.Contains("Canvas", source);
        StringAssert.Contains("ProcessPointerInput", source);
        Assert.IsFalse(source.Contains("private void OnGUI"));
        Assert.IsFalse(source.Contains("GUI.Window"));
        Assert.IsFalse(source.Contains("GUILayout.Toggle"));
    }

    [Test]
    public void DebugOptionsUsesCurrentUnityBuiltInFont()
    {
        var source = File.ReadAllText("Assets/Scripts/DebugOptionsOverlay.cs");

        StringAssert.Contains("LegacyRuntime.ttf", source);
        Assert.IsFalse(source.Contains("Arial.ttf"));
    }
}
