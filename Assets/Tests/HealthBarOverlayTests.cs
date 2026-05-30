using NUnit.Framework;
using System.IO;
using UnityEngine;

public sealed class HealthBarOverlayTests
{
    [Test]
    public void CalculateFillAmountClampsHpRatio()
    {
        var quarter = new HealthComponent
        {
            CurrentHp = 25,
            MaxHp = 100
        };
        var overheal = new HealthComponent
        {
            CurrentHp = 150,
            MaxHp = 100
        };
        var dead = new HealthComponent
        {
            CurrentHp = -10,
            MaxHp = 100
        };

        Assert.AreEqual(0.25f, HealthBarOverlayMath.CalculateFillAmount(quarter), 0.0001f);
        Assert.AreEqual(1f, HealthBarOverlayMath.CalculateFillAmount(overheal), 0.0001f);
        Assert.AreEqual(0f, HealthBarOverlayMath.CalculateFillAmount(dead), 0.0001f);
    }

    [Test]
    public void CalculateFillAmountReturnsZeroForInvalidMaxHp()
    {
        var health = new HealthComponent
        {
            CurrentHp = 10,
            MaxHp = 0
        };

        Assert.AreEqual(0f, HealthBarOverlayMath.CalculateFillAmount(health), 0.0001f);
    }

    [Test]
    public void IsScreenPointVisibleRejectsOutsideScreenAndBehindCamera()
    {
        var screenSize = new Vector2(1920f, 1080f);

        Assert.IsTrue(HealthBarOverlayMath.IsScreenPointVisible(
            new Vector3(960f, 540f, 1f),
            screenSize));
        Assert.IsFalse(HealthBarOverlayMath.IsScreenPointVisible(
            new Vector3(-1f, 540f, 1f),
            screenSize));
        Assert.IsFalse(HealthBarOverlayMath.IsScreenPointVisible(
            new Vector3(960f, 540f, -1f),
            screenSize));
    }

    [Test]
    public void HealthBarOverlayRequiresHealthBarAnchor()
    {
        var overlayText = File.ReadAllText("Assets/Scripts/HealthBarOverlay.cs");

        StringAssert.Contains("ComponentType.ReadOnly<HealthComponent>()", overlayText);
        StringAssert.Contains("ComponentType.ReadOnly<HealthBarAnchor>()", overlayText);
        StringAssert.Contains("ComponentType.ReadOnly<LocalToWorld>()", overlayText);
        StringAssert.Contains("ComponentType.Exclude<Prefab>()", overlayText);
        StringAssert.DoesNotContain("ComponentType.ReadOnly<PlayerTag>()", overlayText);
        StringAssert.DoesNotContain("ComponentType.ReadOnly<MonsterTag>()", overlayText);
        StringAssert.DoesNotContain("WorldOffset", overlayText);
    }
}
