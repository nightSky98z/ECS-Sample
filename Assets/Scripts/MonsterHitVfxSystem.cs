using Unity.Mathematics;

/// <summary>
/// Monster hit VFX の時間と material color 計算。
/// </summary>
public static class MonsterHitVfxMath
{
    public static float NormalizeDuration(float duration)
    {
        return math.max(0.01f, math.abs(duration));
    }

    public static float CalculateProgress(float elapsedTime, float duration)
    {
        return math.clamp(elapsedTime / NormalizeDuration(duration), 0f, 1f);
    }

    public static float4 CalculateBaseColor(float4 hitColor, float4 restColor, float progress)
    {
        return math.lerp(hitColor, restColor, math.clamp(progress, 0f, 1f));
    }
}
