using Unity.Mathematics;

/// <summary>
/// Monster hit VFX の時間と material color 計算。
///
/// 被弾 VFX は renderer の色を短時間だけ差し替える。再生開始や material 書き込みは別 System が行い、
/// ここでは duration / progress / color の値計算だけを扱う。
/// </summary>
public static class MonsterHitVfxMath
{
    /// <summary>
    /// 被弾 VFX duration を 0 より大きい値へ正規化する。
    /// </summary>
    public static float NormalizeDuration(float duration)
    {
        return math.max(0.01f, math.abs(duration));
    }

    /// <summary>
    /// elapsed / duration から 0..1 の再生率を返す。
    /// </summary>
    public static float CalculateProgress(float elapsedTime, float duration)
    {
        return math.clamp(elapsedTime / NormalizeDuration(duration), 0f, 1f);
    }

    /// <summary>
    /// 被弾色から通常色へ戻る base color を返す。
    /// </summary>
    public static float4 CalculateBaseColor(float4 hitColor, float4 restColor, float progress)
    {
        return math.lerp(hitColor, restColor, math.clamp(progress, 0f, 1f));
    }
}
