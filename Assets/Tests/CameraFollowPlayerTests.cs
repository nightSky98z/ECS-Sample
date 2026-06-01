using NUnit.Framework;

public sealed class CameraFollowPlayerTests
{
    [Test]
    public void CalculateAspectSafeOrthographicSizeKeepsBaseSizeOnWideAspect()
    {
        var size = CameraFollowPlayerMath.CalculateAspectSafeOrthographicSize(
            baseOrthographicSize: 12f,
            minimumVisibleWorldWidth: 32f,
            aspect: 16f / 9f);

        Assert.AreEqual(12f, size, 0.0001f);
    }

    [Test]
    public void CalculateAspectSafeOrthographicSizeZoomsOutOnNarrowAspect()
    {
        var size = CameraFollowPlayerMath.CalculateAspectSafeOrthographicSize(
            baseOrthographicSize: 12f,
            minimumVisibleWorldWidth: 32f,
            aspect: 1f);

        Assert.AreEqual(16f, size, 0.0001f);
    }

    [Test]
    public void CalculateAspectSafeOrthographicSizeIgnoresInvalidWidth()
    {
        var size = CameraFollowPlayerMath.CalculateAspectSafeOrthographicSize(
            baseOrthographicSize: 12f,
            minimumVisibleWorldWidth: 0f,
            aspect: 1f);

        Assert.AreEqual(12f, size, 0.0001f);
    }
}
