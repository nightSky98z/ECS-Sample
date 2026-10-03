using Unity.Entities;

/// <summary>
/// ステージの進行段階。
/// 準備中（Warmup）はマップの生成だけを進め、移動・重力・スキル・ステージの経過時間などのゲーム処理は止めておく。
/// </summary>
public enum StageRuntimePhase : byte
{
    /// <summary>準備中（マップを生成している）。</summary>
    Warmup = 0,

    /// <summary>プレイ中。</summary>
    Gameplay = 1,

    /// <summary>クリア済み。</summary>
    Cleared = 2
}

/// <summary>
/// 現在のステージの進行段階。
/// MapCellAuthoring があるシーンにだけ存在する。存在しないシーン（テスト用など）では、ゲーム処理は常に動く。
/// </summary>
public struct StageRuntimeState : IComponentData
{
    /// <summary>
    /// 現在の段階。
    /// </summary>
    public StageRuntimePhase Phase;
}

/// <summary>
/// ゲーム処理の System が共通で使う、進行段階の判定。
/// </summary>
public static class StageRuntimeUtility
{
    /// <summary>
    /// ゲーム処理（移動・スキルなど）を実行してよい段階かを返す。
    /// </summary>
    public static bool IsGameplayPhase(StageRuntimePhase phase)
    {
        return phase == StageRuntimePhase.Gameplay;
    }
}
