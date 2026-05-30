using NUnit.Framework;
using System.IO;

public sealed class ExperienceTests
{
    [Test]
    public void ExperienceRequirementUsesTunableLevelCapAndCurve()
    {
        Assert.AreEqual(99, ExperienceConstants.MaxLevel);
        Assert.AreEqual(7, ExperienceConstants.ExpectedStageCount);
        Assert.AreEqual(90, ExperienceConstants.TargetLevelAfterStageSix);
        Assert.AreEqual(2f, ExperienceConstants.DefaultRequiredExperienceMultiplierPerLevel);
        Assert.AreEqual(50, ExperienceMath.CalculateRequiredExperienceForNextLevel(1));
        Assert.AreEqual(100, ExperienceMath.CalculateRequiredExperienceForNextLevel(2));
        Assert.AreEqual(200, ExperienceMath.CalculateRequiredExperienceForNextLevel(3));
        Assert.AreEqual(0, ExperienceMath.CalculateRequiredExperienceForNextLevel(ExperienceConstants.MaxLevel));
    }

    [Test]
    public void ExperienceRequirementMultiplierIsConfigurable()
    {
        var config = new ExperienceLevelConfig
        {
            BaseRequiredExperience = 40,
            RequiredExperienceMultiplierPerLevel = 1.5f
        };

        Assert.AreEqual(40, ExperienceMath.CalculateRequiredExperienceForNextLevel(1, config));
        Assert.AreEqual(60, ExperienceMath.CalculateRequiredExperienceForNextLevel(2, config));
        Assert.AreEqual(90, ExperienceMath.CalculateRequiredExperienceForNextLevel(3, config));
    }

    [Test]
    public void ConfiguredExperienceRequirementIsUsedWhenAddingAndLeveling()
    {
        var config = new ExperienceLevelConfig
        {
            BaseRequiredExperience = 40,
            RequiredExperienceMultiplierPerLevel = 1.5f
        };
        var experience = ExperienceMath.CreateInitialExperience(config);

        experience = ExperienceMath.AddExperience(experience, 45, config);
        experience = ExperienceMath.ProcessLevelUps(experience, config);

        Assert.AreEqual(2, experience.Level);
        Assert.AreEqual(5, experience.CurrentExperience);
        Assert.AreEqual(60, experience.RequiredExperience);
    }

    [Test]
    public void AddExperienceAccumulatesThenProcessLevelUpsCarriesRemainder()
    {
        var experience = ExperienceMath.CreateInitialExperience();
        var requiredForLevelOne = experience.RequiredExperience;

        experience = ExperienceMath.AddExperience(experience, requiredForLevelOne + 5);

        Assert.AreEqual(1, experience.Level);
        Assert.AreEqual(requiredForLevelOne + 5, experience.CurrentExperience);

        experience = ExperienceMath.ProcessLevelUps(experience);

        Assert.AreEqual(2, experience.Level);
        Assert.AreEqual(5, experience.CurrentExperience);
        Assert.AreEqual(ExperienceMath.CalculateRequiredExperienceForNextLevel(2), experience.RequiredExperience);
    }

    [Test]
    public void AddExperienceStopsAtMaxLevel()
    {
        var experience = new ExperienceComponent
        {
            Level = ExperienceConstants.MaxLevel - 1,
            CurrentExperience = 0,
            RequiredExperience = ExperienceMath.CalculateRequiredExperienceForNextLevel(ExperienceConstants.MaxLevel - 1)
        };

        experience = ExperienceMath.AddExperience(experience, int.MaxValue);
        experience = ExperienceMath.ProcessLevelUps(experience);

        Assert.AreEqual(ExperienceConstants.MaxLevel, experience.Level);
        Assert.AreEqual(0, experience.CurrentExperience);
        Assert.AreEqual(0, experience.RequiredExperience);
    }

    [Test]
    public void PlayerAndMonsterBakersAddExperienceData()
    {
        var playerSource = File.ReadAllText("Assets/Scripts/PlayerEntity.cs");
        var monsterSource = File.ReadAllText("Assets/Scripts/MonsterEntity.cs");

        StringAssert.Contains("ExperienceMath.CreateInitialExperience", playerSource);
        StringAssert.Contains("RequiredExperienceMultiplierPerLevel", playerSource);
        StringAssert.Contains("ExperienceLevelConfig", playerSource);
        StringAssert.Contains("PlayerLevelStats", playerSource);
        StringAssert.Contains("ExperienceRewardValue", monsterSource);
        StringAssert.Contains("ExperienceReward", monsterSource);
    }

    [Test]
    public void PlayerLevelStatsUpdateRuntimeHealthAndSkillDamageRate()
    {
        var stats = new PlayerLevelStats
        {
            BaseMaxHp = 100,
            MaxHpPerLevel = 5,
            SkillDamageRatePerLevel = 0.02f
        };
        var health = new HealthComponent
        {
            CurrentHp = 50,
            MaxHp = 100
        };

        var nextHealth = ExperienceMath.ApplyPlayerLevelToHealth(health, stats, 3);

        Assert.AreEqual(110, nextHealth.MaxHp);
        Assert.AreEqual(60, nextHealth.CurrentHp);
        Assert.AreEqual(1.04f, ExperienceMath.CalculatePlayerSkillDamageRate(stats, 3), 0.0001f);
    }

    [Test]
    public void MonsterDestroySystemAwardsExperienceToPlayer()
    {
        var source = File.ReadAllText("Assets/Scripts/MonsterDestroySystem.cs");

        StringAssert.Contains("ExperienceReward", source);
        StringAssert.Contains("AddExperienceToPlayers", source);
        StringAssert.Contains("ExperienceMath.AddExperience", source);
    }

    [Test]
    public void LevelUpSystemRunsAfterMonsterDestroySystem()
    {
        var source = File.ReadAllText("Assets/Scripts/ExperienceSystem.cs");

        StringAssert.Contains("LevelUpSystem", source);
        StringAssert.Contains("UpdateAfter(typeof(MonsterDestroySystem))", source);
        StringAssert.Contains("ExperienceMath.ProcessLevelUps", source);
        StringAssert.Contains("ExperienceLevelConfig", source);
        StringAssert.Contains("ApplyPlayerLevelStats", source);
        StringAssert.Contains("HealthComponent", source);
        Assert.IsFalse(source.Contains("MoveSpeed"));
    }
}
