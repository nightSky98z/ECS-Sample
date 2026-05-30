using NUnit.Framework;
using System.IO;

public sealed class GameFlowTests
{
    [Test]
    public void GameFlowBootstrapUsesExpectedSceneNames()
    {
        Assert.AreEqual("GameStartScene", GameFlowBootstrap.StartSceneName);
        Assert.AreEqual("DebugScene", GameFlowBootstrap.GameplaySceneName);
        Assert.AreEqual("ResultScene", GameFlowBootstrap.ResultSceneName);
    }

    [Test]
    public void BuildSettingsContainGameFlowScenes()
    {
        var source = File.ReadAllText("ProjectSettings/EditorBuildSettings.asset");
        var startSceneIndex = source.IndexOf("path: Assets/Scenes/GameStartScene.unity");
        var gameplaySceneIndex = source.IndexOf("path: Assets/Scenes/DebugScene.unity");

        StringAssert.Contains("Assets/Scenes/GameStartScene.unity", source);
        StringAssert.Contains("Assets/Scenes/DebugScene.unity", source);
        StringAssert.Contains("Assets/Scenes/ResultScene.unity", source);
        Assert.GreaterOrEqual(startSceneIndex, 0);
        Assert.Greater(gameplaySceneIndex, startSceneIndex);
    }

    [Test]
    public void GameFlowRedirectsDirectGameplayOrResultLaunchToStart()
    {
        Assert.IsFalse(GameFlowBootstrap.ShouldRedirectToStartScene(GameFlowBootstrap.StartSceneName, false));
        Assert.IsTrue(GameFlowBootstrap.ShouldRedirectToStartScene(GameFlowBootstrap.GameplaySceneName, false));
        Assert.IsFalse(GameFlowBootstrap.ShouldRedirectToStartScene(GameFlowBootstrap.GameplaySceneName, true));
        Assert.IsTrue(GameFlowBootstrap.ShouldRedirectToStartScene(GameFlowBootstrap.ResultSceneName, false));
        Assert.IsFalse(GameFlowBootstrap.ShouldRedirectToStartScene(GameFlowBootstrap.ResultSceneName, true));
    }

    [Test]
    public void GameFlowBootstrapDisposesStageClearQuery()
    {
        var source = File.ReadAllText("Assets/Scripts/GameFlowBootstrap.cs");

        StringAssert.Contains("DisposeStageClearQuery()", source);
        StringAssert.Contains("stageClearQuery.Dispose()", source);
        StringAssert.Contains("GetSingleton<StageClearState>()", source);
        Assert.IsFalse(source.Contains("ToComponentDataArray<StageClearState>"));
        Assert.IsFalse(source.Contains("hasStageClearQuery = false;\n        framesSinceSceneLoaded = 0;"));
    }
}
