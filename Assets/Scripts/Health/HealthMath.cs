using Unity.Mathematics;

/// <summary>
/// HP を直接更新する System / Baker が使う計算。
///
/// HealthComponent は runtime 状態なので、ここでは値の正規化と差分適用だけを扱う。
/// ダメージ発生元や死亡遷移は呼び出し側 System の責務。
/// </summary>
public static class HealthMath
{
    /// <summary>
    /// current / max を安全な範囲に収めた HealthComponent を作る。
    /// </summary>
    /// <param name="currentHp">現在 HP。0..maxHp に丸める。</param>
    /// <param name="maxHp">最大 HP。1 未満は 1 として扱う。</param>
    /// <returns>正規化済み HP。</returns>
    public static HealthComponent CreateHealth(int currentHp, int maxHp)
    {
        var safeMaxHp = NormalizeMaxHp(maxHp);

        return new HealthComponent
        {
            CurrentHp = math.clamp(currentHp, 0, safeMaxHp),
            MaxHp = safeMaxHp
        };
    }

    /// <summary>
    /// 最大 HP と同じ現在 HP を持つ HealthComponent を作る。
    /// </summary>
    /// <param name="maxHp">最大 HP。1 未満は 1 として扱う。</param>
    /// <returns>全回復状態の HP。</returns>
    public static HealthComponent CreateFullHealth(int maxHp)
    {
        return CreateHealth(maxHp, maxHp);
    }

    /// <summary>
    /// HP 差分を適用し、0..MaxHp に丸めた結果を返す。
    /// </summary>
    /// <param name="health">更新前の HP。</param>
    /// <param name="delta">加算差分。負値はダメージ、正値は回復。</param>
    /// <returns>差分適用後の HP。</returns>
    public static HealthComponent ApplyHealthDelta(HealthComponent health, int delta)
    {
        var safeMaxHp = NormalizeMaxHp(health.MaxHp);
        var safeCurrentHp = math.clamp(health.CurrentHp, 0, safeMaxHp);
        var nextHp = (long)safeCurrentHp + delta;

        return new HealthComponent
        {
            CurrentHp = ClampHp(nextHp, safeMaxHp),
            MaxHp = safeMaxHp
        };
    }

    /// <summary>
    /// HP が死亡状態かどうかを返す。
    /// </summary>
    /// <param name="health">判定する HP。</param>
    /// <returns>CurrentHp が 0 以下なら true。</returns>
    public static bool IsDead(HealthComponent health)
    {
        return health.CurrentHp <= 0;
    }

    private static int NormalizeMaxHp(int maxHp)
    {
        return math.max(1, maxHp);
    }

    private static int ClampHp(long hp, int maxHp)
    {
        if (hp <= 0L)
        {
            return 0;
        }

        if (hp >= maxHp)
        {
            return maxHp;
        }

        return (int)hp;
    }
}
