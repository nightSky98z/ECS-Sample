using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;

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
/// Monster material VFX が renderer の _BaseColor を上書きするための material property。
/// </summary>
[MaterialProperty("_BaseColor")]
public struct MonsterMaterialBaseColor : IComponentData
{
    public float4 Value;
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
    public byte IsPlaying;
}
