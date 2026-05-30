using NUnit.Framework;
using System.IO;

public sealed class HealthBarPrefabTests
{
    [Test]
    public void HpBarPrefabHasBackgroundAndFilledFrontImage()
    {
        var prefabText = File.ReadAllText("Assets/Prefab/HP_Bar.prefab");

        Assert.IsTrue(prefabText.Contains("m_Name: HP_Bar"));
        Assert.IsTrue(prefabText.Contains("m_Name: background-image"));
        Assert.IsTrue(prefabText.Contains("m_Name: front-image"));
        Assert.IsTrue(prefabText.Contains("m_Type: 3"));
        Assert.IsTrue(prefabText.Contains("m_FillMethod: 0"));
        Assert.IsTrue(prefabText.Contains("m_FillAmount: 1"));
    }
}
