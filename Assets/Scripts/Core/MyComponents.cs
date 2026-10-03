using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

// =============================================================================
// 複数の機能で共有する基本 Component（移動・HP・経験値・接地など）
// =============================================================================

/// <summary>
/// Entity の現在の速度。MovementSystem と PhysicsSystem が毎フレーム更新する。
/// </summary>
public struct Velocity : IComponentData
{
    /// <summary>
    /// x / z は水平移動、y は重力や接地の補正に使う。
    /// </summary>
    public float3 Value;
}

/// <summary>
/// Entity の基本の移動速度。
/// デバフなどの倍率は別の Component に持たせ、この元の値は書き換えない。
/// </summary>
public struct MoveSpeed : IComponentData
{
    /// <summary>
    /// 1 秒あたりの水平移動量。
    /// </summary>
    public float Value;
}

/// <summary>
/// ノックバックなど、入力や AI とは別に一時的に加わる水平方向の速度。
/// </summary>
public struct KnockbackVelocity : IComponentData
{
    /// <summary>
    /// MovementSystem が Velocity に足し合わせる速度。
    /// </summary>
    public float3 Value;

    /// <summary>
    /// 1 秒あたりに減らす速度量。0 以下なら減衰しない。
    /// </summary>
    public float DecayPerSecond;
}

/// <summary>
/// Entity の HP。ダメージ・回復・レベルアップで更新される。
/// </summary>
public struct HealthComponent : IComponentData
{
    /// <summary>
    /// 現在の HP。0 以下で死亡とみなす。
    /// </summary>
    public int CurrentHp;

    /// <summary>
    /// 現在の最大 HP。レベルアップやバフで変わるため、固定の設定値ではなく実行時の状態として持つ。
    /// </summary>
    public int MaxHp;
}

/// <summary>
/// Entity の経験値とレベル。
/// </summary>
public struct ExperienceComponent : IComponentData
{
    /// <summary>
    /// 現在のレベルで貯まっている経験値。
    /// </summary>
    public int CurrentExperience;

    /// <summary>
    /// 次のレベルに上がるために必要な経験値。最大レベルに達したら 0。
    /// </summary>
    public int RequiredExperience;

    /// <summary>
    /// 現在のレベル。上限は ExperienceConstants.MaxLevel。
    /// </summary>
    public int Level;
}

/// <summary>
/// レベルアップに必要な経験値の増え方（成長曲線）。
/// </summary>
public struct ExperienceLevelConfig : IComponentData
{
    /// <summary>
    /// Lv1 から Lv2 に上がるために必要な経験値。
    /// </summary>
    public int BaseRequiredExperience;

    /// <summary>
    /// レベルが 1 上がるごとに、必要経験値にかける倍率。1 未満は 1 として扱う。
    /// </summary>
    public float RequiredExperienceMultiplierPerLevel;
}

/// <summary>
/// モンスターを倒したときにプレイヤーがもらえる経験値。
/// </summary>
public struct ExperienceReward : IComponentData
{
    /// <summary>
    /// 獲得できる経験値。負の値は 0 として扱う。
    /// </summary>
    public int Value;
}

/// <summary>
/// プレイヤーのレベルから最大 HP やダメージ倍率を計算するための設定。
/// </summary>
public struct PlayerLevelStats : IComponentData
{
    /// <summary>
    /// Lv1 のときの最大 HP。
    /// </summary>
    public int BaseMaxHp;

    /// <summary>
    /// レベルが 1 上がるごとに増える最大 HP。
    /// </summary>
    public int MaxHpPerLevel;

    /// <summary>
    /// レベルが 1 上がるごとに増えるスキルダメージ倍率。
    /// </summary>
    public float SkillDamageRatePerLevel;
}

/// <summary>
/// UI やデバッグ表示に使う名前。
/// </summary>
public struct EntityDisplayName : IComponentData
{
    /// <summary>
    /// 表示名。FixedString にすることで Component 内に直接持て、Managed な string を参照せずに済む。
    /// </summary>
    public FixedString64Bytes Value;
}

/// <summary>
/// 頭上に表示する HP バーの位置と大きさ。
/// </summary>
public struct HealthBarAnchor : IComponentData
{
    /// <summary>
    /// Entity の原点から見た HP バーの表示位置（ローカル座標）。
    /// </summary>
    public float3 LocalOffset;

    /// <summary>
    /// 画面上の HP バーの大きさ（UI のピクセル単位）。
    /// </summary>
    public float2 Size;
}

/// <summary>
/// ゲームロジック用の当たり判定の半径（XZ 平面）。
/// </summary>
public struct CollisionRadius : IComponentData
{
    /// <summary>
    /// 半径。Unity Physics の Collider の形とは別に、ゲーム側の判定だけで使う値。
    /// </summary>
    public float Value;
}

/// <summary>
/// Entity にかかる重力。
/// </summary>
public struct Gravity : IComponentData
{
    /// <summary>
    /// Velocity.y に毎秒加える加速度。
    /// </summary>
    public float Acceleration;
}

/// <summary>
/// 接地しているかどうかと、最後に検出した地面の高さ。
/// </summary>
public struct GroundSnap : IComponentData
{
    /// <summary>
    /// 最後に接地した地面の高さ（ワールド座標の Y）。
    /// </summary>
    public float GroundY;

    /// <summary>
    /// 1 なら接地中、0 なら空中。
    /// </summary>
    public byte IsGrounded;
}

/// <summary>
/// FPS が低いときなどに地面をすり抜けてしまった Entity を、最後に記録した地面の高さへ戻すための設定。
/// </summary>
public struct GroundFallRescue : IComponentData
{
    /// <summary>
    /// GroundY からこの距離以上落ちたら元の高さへ戻す。0 以下なら無効。
    /// </summary>
    public float MaxBelowGroundY;
}

/// <summary>
/// 上に乗ることができる地面を表すタグ。
/// </summary>
public struct GroundTag : IComponentData
{

}

/// <summary>
/// 通り抜けられない障害物（木や岩など）を表すタグ。
/// </summary>
public struct StaticObstacleTag : IComponentData
{

}

/// <summary>
/// 接地判定に使う球の情報（SensorCollider の SphereCollider から Bake する）。
/// </summary>
public struct GroundSensor : IComponentData
{
    /// <summary>
    /// Entity の原点から見た球の中心（ローカル座標）。
    /// </summary>
    public float3 LocalCenter;

    /// <summary>
    /// 球の半径。
    /// </summary>
    public float Radius;

    /// <summary>
    /// 地面からこの距離以内なら接地しているとみなす余裕の幅。
    /// </summary>
    public float Skin;
}

/// <summary>
/// XZ 平面での向き。x がワールドの X、y がワールドの Z に対応する。
/// </summary>
public struct FacingDirection : IComponentData
{
    /// <summary>
    /// 正規化された向き。止まっている間は、最後に動いていたときの向きを保持する。
    /// </summary>
    public float2 Value;
}

/// <summary>
/// プレイヤーに向かってまっすぐ進む、最も単純な AI を使うモンスターのタグ。
/// </summary>
public struct MonsterSimpleAi : IComponentData
{

}
