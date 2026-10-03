using Unity.Mathematics;

/// <summary>
/// HP の計算処理。HP を更新する System や Baker から呼ばれる。
/// ここでは「値を正しい範囲に収める」「増減を適用する」ことだけを扱い、
/// ダメージの発生や死亡時の処理は呼び出し側の System が担当する。
/// </summary>
public static class HealthMath
{
    /// <summary>
    /// 現在 HP と最大 HP を正しい範囲に収めた HealthComponent を作る。
    /// </summary>
    /// <param name="currentHp">現在 HP。0〜maxHp に収める。</param>
    /// <param name="maxHp">最大 HP。1 未満は 1 として扱う。</param>
    /// <returns>範囲内に収めた HP。</returns>
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
    /// HP が満タンの HealthComponent を作る。
    /// </summary>
    /// <param name="maxHp">最大 HP。1 未満は 1 として扱う。</param>
    /// <returns>満タンの HP。</returns>
    public static HealthComponent CreateFullHealth(int maxHp)
    {
        return CreateHealth(maxHp, maxHp);
    }

    /// <summary>
    /// HP を増減させ、0〜MaxHp に収めた結果を返す。
    /// </summary>
    /// <param name="health">変更前の HP。</param>
    /// <param name="delta">増減量。負の値はダメージ、正の値は回復。</param>
    /// <returns>変更後の HP。</returns>
    public static HealthComponent ApplyHealthDelta(HealthComponent health, int delta)
    {
        var safeMaxHp = NormalizeMaxHp(health.MaxHp);
        var safeCurrentHp = math.clamp(health.CurrentHp, 0, safeMaxHp);
        // 大きなダメージや回復でも int があふれないよう、long で計算する。
        var nextHp = (long)safeCurrentHp + delta;

        return new HealthComponent
        {
            CurrentHp = ClampHp(nextHp, safeMaxHp),
            MaxHp = safeMaxHp
        };
    }

    /// <summary>
    /// 死亡しているかどうかを返す。
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
