using NUnit.Framework;
using System.IO;

public sealed class StageProgressTests
{
    [Test]
    public void CalculateTimedSurvivalProgressNormalizesToTimeLimit()
    {
        Assert.AreEqual(0f, StageProgressMath.CalculateTimedSurvivalProgress(-1f, 60f));
        Assert.AreEqual(0.5f, StageProgressMath.CalculateTimedSurvivalProgress(30f, 60f));
        Assert.AreEqual(1f, StageProgressMath.CalculateTimedSurvivalProgress(90f, 60f));
        Assert.AreEqual(0f, StageProgressMath.CalculateTimedSurvivalProgress(30f, 0f));
    }

    [Test]
    public void CalculateKillCountProgressNormalizesToTargetKillCount()
    {
        Assert.AreEqual(0f, StageProgressMath.CalculateKillCountProgress(-3, 10));
        Assert.AreEqual(0.5f, StageProgressMath.CalculateKillCountProgress(5, 10));
        Assert.AreEqual(1f, StageProgressMath.CalculateKillCountProgress(12, 10));
        Assert.AreEqual(0f, StageProgressMath.CalculateKillCountProgress(5, 0));
    }

    [Test]
    public void AddKillCountIgnoresNegativeDelta()
    {
        Assert.AreEqual(5, StageProgressMath.AddKillCount(5, -3));
        Assert.AreEqual(8, StageProgressMath.AddKillCount(5, 3));
    }

    [Test]
    public void CalculateBossHealthProgressUsesLostHpRate()
    {
        Assert.AreEqual(0f, StageProgressMath.CalculateBossHealthProgress(100, 100));
        Assert.AreEqual(0.25f, StageProgressMath.CalculateBossHealthProgress(75, 100));
        Assert.AreEqual(1f, StageProgressMath.CalculateBossHealthProgress(0, 100));
    }

    [Test]
    public void ClearChecksMatchStageConditions()
    {
        Assert.IsFalse(StageProgressMath.IsTimedSurvivalStageCleared(59f, 60f));
        Assert.IsTrue(StageProgressMath.IsTimedSurvivalStageCleared(60f, 60f));
        Assert.IsFalse(StageProgressMath.IsKillCountStageCleared(9, 10));
        Assert.IsTrue(StageProgressMath.IsKillCountStageCleared(10, 10));
        Assert.IsFalse(StageProgressMath.IsBossStageCleared(1));
        Assert.IsTrue(StageProgressMath.IsBossStageCleared(0));
    }

    [Test]
    public void ClearStateLatchNeverReturnsToUncleared()
    {
        Assert.AreEqual(1, StageProgressMath.LatchClearState(1, false));
        Assert.AreEqual(1, StageProgressMath.LatchClearState(0, true));
        Assert.AreEqual(0, StageProgressMath.LatchClearState(0, false));
    }

    [Test]
    public void StageClearSystemIsExplicitAndRequiresStageClearState()
    {
        var source = File.ReadAllText("Assets/Scripts/Stage/StageProgressAuthoring.cs");

        StringAssert.Contains("StageClearSystem", source);
        StringAssert.Contains("RequireForUpdate<StageClearState>", source);
        StringAssert.Contains("SystemAPI.Time.DeltaTime", source);
        Assert.IsFalse(source.Contains("Time.unscaledDeltaTime"));
    }

    [Test]
    public void BossHealthStageUsesRuntimeBossMaxHp()
    {
        var source = File.ReadAllText("Assets/Scripts/Stage/StageProgressAuthoring.cs");

        StringAssert.Contains("state.EntityManager.GetComponentData<HealthComponent>", source);
        StringAssert.Contains("maxHp = math.max(1, health.MaxHp)", source);
        Assert.IsFalse(source.Contains("math.max(health.MaxHp, maxHp)"));
    }
}
