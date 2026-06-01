using Unity.Entities;

/// <summary>
/// ステージ実行状態。
///
/// Warmup 中は map streaming だけを進め、移動、重力、スキル、ステージ時間などの gameplay 更新を止める。
/// </summary>
public enum StageRuntimePhase : byte
{
    Warmup = 0,
    Gameplay = 1,
    Cleared = 2
}

/// <summary>
/// 現在のステージ実行 phase。
///
/// MapCellAuthoring がある scene だけに置く。存在しない scene では従来通り gameplay system を動かす。
/// </summary>
public struct StageRuntimeState : IComponentData
{
    /// <summary>
    /// 現在の phase。Warmup は map 準備中、Gameplay は通常プレイ中を意味する。
    /// </summary>
    public StageRuntimePhase Phase;
}

/// <summary>
/// Gameplay system が共通で使う phase 判定。
/// </summary>
public static class StageRuntimeUtility
{
    /// <summary>
    /// gameplay 更新を実行してよい phase かを返す。
    /// </summary>
    public static bool IsGameplayPhase(StageRuntimePhase phase)
    {
        return phase == StageRuntimePhase.Gameplay;
    }
}
