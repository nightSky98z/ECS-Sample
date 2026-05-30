using NUnit.Framework;
using System.IO;

public sealed class StageObjectiveOverlayTests
{
    [Test]
    public void CalculateRemainingSecondsCountsDownFromLimit()
    {
        Assert.AreEqual(60f, StageObjectiveOverlayMath.CalculateRemainingSeconds(60f, 0f));
        Assert.AreEqual(42.5f, StageObjectiveOverlayMath.CalculateRemainingSeconds(60f, 17.5f));
        Assert.AreEqual(0f, StageObjectiveOverlayMath.CalculateRemainingSeconds(60f, 90f));
    }

    [Test]
    public void CalculateRemainingKillCountCountsDownFromTarget()
    {
        Assert.AreEqual(100, StageObjectiveOverlayMath.CalculateRemainingKillCount(100, 0));
        Assert.AreEqual(55, StageObjectiveOverlayMath.CalculateRemainingKillCount(100, 45));
        Assert.AreEqual(0, StageObjectiveOverlayMath.CalculateRemainingKillCount(100, 120));
    }

    [Test]
    public void CalculateBossHpFillClampsCurrentHpRate()
    {
        Assert.AreEqual(1f, StageObjectiveOverlayMath.CalculateBossHpFillAmount(100, 100), 0.0001f);
        Assert.AreEqual(0.25f, StageObjectiveOverlayMath.CalculateBossHpFillAmount(25, 100), 0.0001f);
        Assert.AreEqual(0f, StageObjectiveOverlayMath.CalculateBossHpFillAmount(-10, 100), 0.0001f);
    }

    [Test]
    public void FormatRemainingSecondsUsesMinuteSecondText()
    {
        Assert.AreEqual("05:00", StageObjectiveOverlayMath.FormatRemainingSeconds(300f));
        Assert.AreEqual("00:09", StageObjectiveOverlayMath.FormatRemainingSeconds(9.2f));
        Assert.AreEqual("00:00", StageObjectiveOverlayMath.FormatRemainingSeconds(-1f));
    }

    [Test]
    public void StageObjectiveOverlayReadsOnlyStageProgressEntities()
    {
        var source = File.ReadAllText("Assets/Scripts/StageObjectiveOverlay.cs");

        StringAssert.Contains("TimedSurvivalStageProgress", source);
        StringAssert.Contains("KillCountStageProgress", source);
        StringAssert.Contains("BossHealthStageProgress", source);
        StringAssert.Contains("StageClearState", source);
        StringAssert.Contains("GetSingleton<TimedSurvivalStageProgress>", source);
        StringAssert.Contains("SetVisible(false)", source);
        Assert.IsFalse(source.Contains("ToComponentDataArray"));
    }

    [Test]
    public void StageObjectiveOverlayDisposesRuntimeQueries()
    {
        var source = File.ReadAllText("Assets/Scripts/StageObjectiveOverlay.cs");

        StringAssert.Contains("DisposeQueries()", source);
        StringAssert.Contains("timedStageQuery.Dispose()", source);
        StringAssert.Contains("killCountStageQuery.Dispose()", source);
        StringAssert.Contains("bossStageQuery.Dispose()", source);
    }

    [Test]
    public void StageObjectiveOverlayRebuildsIncompleteUiReferences()
    {
        var source = File.ReadAllText("Assets/Scripts/StageObjectiveOverlay.cs");

        StringAssert.Contains("IsUiReady()", source);
        StringAssert.Contains("ClearUiReferences()", source);
        StringAssert.Contains("objectiveText != null", source);
        StringAssert.Contains("bossFrontImage != null", source);
    }

    [Test]
    public void StageObjectiveOverlayUsesCurrentUnityBuiltInFont()
    {
        var source = File.ReadAllText("Assets/Scripts/StageObjectiveOverlay.cs");

        StringAssert.Contains("LegacyRuntime.ttf", source);
        Assert.IsFalse(source.Contains("Arial.ttf"));
    }
}
