using NUnit.Framework;
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
}
