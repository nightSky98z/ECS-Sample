using Unity.Mathematics;

/// <summary>
/// HP を直接更新する System / Baker が使う計算。
/// </summary>
public static class HealthMath
{
    public static HealthComponent CreateHealth(int currentHp, int maxHp)
    {
        var safeMaxHp = NormalizeMaxHp(maxHp);

        return new HealthComponent
        {
            CurrentHp = math.clamp(currentHp, 0, safeMaxHp),
            MaxHp = safeMaxHp
        };
    }

    public static HealthComponent CreateFullHealth(int maxHp)
    {
        return CreateHealth(maxHp, maxHp);
    }

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
