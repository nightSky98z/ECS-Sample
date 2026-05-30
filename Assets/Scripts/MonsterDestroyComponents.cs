using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Monster death material VFX の prefab 側設定。
/// </summary>
public struct MonsterDestroyVfxConfig : IComponentData
{
    public float Duration;
    public float4 StartBaseColor;
    public float4 EndBaseColor;
    public float EndScale;
}

/// <summary>
/// Death VFX 再生中の runtime 状態。通常 monster は完了後の recycle 復元値としても使う。
/// </summary>
public struct MonsterDestroyVfxState : IComponentData
{
    public float ElapsedTime;
    public float OriginalScale;
    public float OriginalCollisionRadius;
}

/// <summary>
/// Monster hit material VFX の prefab 側設定。
/// </summary>
public struct MonsterHitVfxConfig : IComponentData
{
    public float Duration;
    public float4 HitBaseColor;
    public float4 RestBaseColor;
}

/// <summary>
/// Hit VFX 再生中の runtime 状態。
/// </summary>
public struct MonsterHitVfxState : IComponentData
{
    public float ElapsedTime;

    /// <summary>
    /// 0 = stopped, 1 = playing。
    /// </summary>
    public byte IsPlaying;
}
