using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// モンスターの死亡演出（色を変えながら縮んで消える）の設定。
/// </summary>
public struct MonsterDestroyVfxConfig : IComponentData
{
    /// <summary>演出の長さ（秒）。</summary>
    public float Duration;

    /// <summary>演出開始時の色。</summary>
    public float4 StartBaseColor;

    /// <summary>演出終了時の色。</summary>
    public float4 EndBaseColor;

    /// <summary>演出終了時の大きさ（元の大きさに対する倍率）。</summary>
    public float EndScale;
}

/// <summary>
/// 死亡演出中の状態。この Component が付いている間は「死亡演出中」として、移動や当たり判定の対象から外れる。
/// 再利用するモンスターは、演出のあとにこの値を使って元の大きさと当たり判定に戻す。
/// </summary>
public struct MonsterDestroyVfxState : IComponentData
{
    /// <summary>演出開始からの経過時間（秒）。</summary>
    public float ElapsedTime;

    /// <summary>死亡前の大きさ。</summary>
    public float OriginalScale;

    /// <summary>死亡前の当たり判定の半径。</summary>
    public float OriginalCollisionRadius;
}

/// <summary>
/// モンスターの被弾演出（一瞬だけ色が変わる）の設定。
/// </summary>
public struct MonsterHitVfxConfig : IComponentData
{
    /// <summary>演出の長さ（秒）。</summary>
    public float Duration;

    /// <summary>被弾した瞬間の色。</summary>
    public float4 HitBaseColor;

    /// <summary>元の色（演出の最後にこの色へ戻る）。</summary>
    public float4 RestBaseColor;
}

/// <summary>
/// 被弾演出の再生状態。演出のたびに Component を付け外しせず、フラグで再生中かどうかを表す。
/// </summary>
public struct MonsterHitVfxState : IComponentData
{
    /// <summary>演出開始からの経過時間（秒）。</summary>
    public float ElapsedTime;

    /// <summary>
    /// 1 なら再生中、0 なら停止中。
    /// </summary>
    public byte IsPlaying;
}
