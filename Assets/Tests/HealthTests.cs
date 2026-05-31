using NUnit.Framework;
using System.IO;

public sealed class HealthTests
{
    [Test]
    public void CreateHealthClampsCurrentHpToMaxHp()
    {
        var health = HealthMath.CreateHealth(150, 100);

        Assert.AreEqual(100, health.CurrentHp);
        Assert.AreEqual(100, health.MaxHp);
    }

    [Test]
    public void ApplyHealthDeltaClampsDamageAndHeal()
    {
        var health = new HealthComponent { CurrentHp = 50, MaxHp = 100 };

        var damaged = HealthMath.ApplyHealthDelta(health, -80);
        var healed = HealthMath.ApplyHealthDelta(health, 80);

        Assert.AreEqual(0, damaged.CurrentHp);
        Assert.AreEqual(100, healed.CurrentHp);
        Assert.AreEqual(100, damaged.MaxHp);
        Assert.AreEqual(100, healed.MaxHp);
    }

    [Test]
    public void ApplyHealthDeltaHandlesLargeHealWithoutOverflow()
    {
        var health = new HealthComponent { CurrentHp = 50, MaxHp = 100 };

        var healed = HealthMath.ApplyHealthDelta(health, int.MaxValue);

        Assert.AreEqual(100, healed.CurrentHp);
    }

    [Test]
    public void IsDeadTreatsZeroHpAsDead()
    {
        var alive = new HealthComponent { CurrentHp = 1, MaxHp = 100 };
        var dead = new HealthComponent { CurrentHp = 0, MaxHp = 100 };

        Assert.IsFalse(HealthMath.IsDead(alive));
        Assert.IsTrue(HealthMath.IsDead(dead));
    }

    [Test]
    public void HealthDoesNotUseStructuralChangeRequestComponent()
    {
        var componentText = File.ReadAllText("Assets/Scripts/Core/MyComponents.cs");
        var healthText = File.ReadAllText("Assets/Scripts/Health/HealthMath.cs");

        Assert.IsFalse(componentText.Contains("HealthChangeRequest"));
        Assert.IsFalse(healthText.Contains("HealthChangeRequest"));
        Assert.IsFalse(healthText.Contains("EntityCommandBuffer"));
    }

    [Test]
    public void HealthKeepsMutableMaxHpWithRuntimeState()
    {
        var componentText = File.ReadAllText("Assets/Scripts/Core/MyComponents.cs");

        StringAssert.Contains("public struct HealthComponent", componentText);
        StringAssert.Contains("public int MaxHp", componentText);
        StringAssert.Contains("public int CurrentHp", componentText);
        Assert.IsFalse(componentText.Contains("public struct HealthConfig"));
    }
}
