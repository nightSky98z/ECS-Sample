using Unity.Mathematics;

/// <summary>
/// モンスターの被弾演出の計算処理（時間と色）。
/// 被弾演出は、見た目の色を一瞬だけ変えて元に戻す演出。
/// 再生の開始や色の書き込みは別の System が行い、ここでは値の計算だけを扱う。
/// </summary>
public static class MonsterHitVfxMath
{
    /// <summary>
    /// 演出の長さが 0 以下にならないように補正する（0 で割るのを防ぐ）。
    /// </summary>
    public static float NormalizeDuration(float duration)
    {
        return math.max(0.01f, math.abs(duration));
    }

    /// <summary>
    /// 経過時間から、演出の進み具合（0〜1）を返す。
    /// </summary>
    public static float CalculateProgress(float elapsedTime, float duration)
    {
        return math.clamp(elapsedTime / NormalizeDuration(duration), 0f, 1f);
    }

    /// <summary>
    /// 進み具合に応じて、被弾時の色から元の色へ徐々に戻した色を返す。
    /// </summary>
    public static float4 CalculateBaseColor(float4 hitColor, float4 restColor, float progress)
    {
        return math.lerp(hitColor, restColor, math.clamp(progress, 0f, 1f));
    }
}
