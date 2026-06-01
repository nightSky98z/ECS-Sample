using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Entity の現在速度。MovementSystem / PhysicsSystem が直接更新する runtime 状態。
/// </summary>
public struct Velocity : IComponentData
{
    /// <summary>
    /// x/z は水平移動、y は重力や接地補正で使う。
    /// </summary>
    public float3 Value;
}

/// <summary>
/// Entity の基礎水平移動速度。倍率系の効果は別 component に持たせ、元値は破壊しない。
/// </summary>
public struct MoveSpeed : IComponentData
{
    /// <summary>
    /// 1 秒あたりの XZ 移動量。
    /// </summary>
    public float Value;
}

/// <summary>
/// 被弾など、入力や AI とは別に短時間だけ残る水平速度。
/// </summary>
public struct KnockbackVelocity : IComponentData
{
    /// <summary>
    /// MovementSystem が Velocity に合成する追加速度。
    /// </summary>
    public float3 Value;

    /// <summary>
    /// 1 秒あたりに減らす速度量。0 以下なら減衰しない。
    /// </summary>
    public float DecayPerSecond;
}

/// <summary>
/// Entity の HP。CurrentHp / MaxHp は damage、heal、level up で更新する runtime 状態。
/// </summary>
public struct HealthComponent : IComponentData
{
    /// <summary>
    /// 現在 HP。0 以下は死亡状態として扱う。
    /// </summary>
    public int CurrentHp;

    /// <summary>
    /// 現在の最大 HP。レベルアップや buff で更新されるため、固定 config ではない。
    /// </summary>
    public int MaxHp;
}

/// <summary>
/// Entity の経験値とレベル。CurrentExperience / Level は runtime 状態、RequiredExperience は現在レベルの必要量。
/// </summary>
public struct ExperienceComponent : IComponentData
{
    /// <summary>
    /// 現在 level 内で保持している経験値。
    /// </summary>
    public int CurrentExperience;

    /// <summary>
    /// 次 level へ進むために必要な経験値。MaxLevel 到達後は 0。
    /// </summary>
    public int RequiredExperience;

    /// <summary>
    /// 現在 level。ExperienceConstants.MaxLevel を上限とする。
    /// </summary>
    public int Level;
}

/// <summary>
/// Player ごとの level up 必要経験値曲線。RequiredExperienceMultiplierPerLevel は前 level 必要量への倍率。
/// </summary>
public struct ExperienceLevelConfig : IComponentData
{
    /// <summary>
    /// Lv1 から Lv2 に上がるための必要経験値。
    /// </summary>
    public int BaseRequiredExperience;

    /// <summary>
    /// 次 level 必要経験値へ掛ける倍率。1 未満は 1 として扱う。
    /// </summary>
    public float RequiredExperienceMultiplierPerLevel;
}

/// <summary>
/// Monster が死亡時に player へ渡す経験値。
/// </summary>
public struct ExperienceReward : IComponentData
{
    /// <summary>
    /// 死亡時に player へ加算する経験値。負値は 0 に正規化する。
    /// </summary>
    public int Value;
}

/// <summary>
/// Player level から runtime status を再計算するための固定寄りデータ。
/// </summary>
public struct PlayerLevelStats : IComponentData
{
    /// <summary>
    /// Lv1 の最大 HP。
    /// </summary>
    public int BaseMaxHp;

    /// <summary>
    /// レベル 1 つごとの最大 HP 増加量。
    /// </summary>
    public int MaxHpPerLevel;

    /// <summary>
    /// レベル 1 つごとのスキルダメージ倍率増加量。
    /// </summary>
    public float SkillDamageRatePerLevel;
}

/// <summary>
/// UI や debug 用の表示名。FixedString なので component 内に所有する。
/// </summary>
public struct EntityDisplayName : IComponentData
{
    /// <summary>
    /// ECS 側で所有する短い表示名。UnityEngine.Object への参照は保持しない。
    /// </summary>
    public FixedString64Bytes Value;
}

/// <summary>
/// 頭上 HP bar の prefab 内 anchor。LocalOffset は Entity local 空間、Size は UI pixel size。
/// </summary>
public struct HealthBarAnchor : IComponentData
{
    /// <summary>
    /// Entity root から見た HP bar の表示基準位置。
    /// </summary>
    public float3 LocalOffset;

    /// <summary>
    /// 画面上に出す HP bar の UI サイズ。
    /// </summary>
    public float2 Size;
}

/// <summary>
/// XZ 平面で使うゲーム用の接触半径。
/// </summary>
public struct CollisionRadius : IComponentData
{
    /// <summary>
    /// XZ 平面で使う半径。Unity Physics collider の形状とは独立したゲームロジック用の値。
    /// </summary>
    public float Value;
}

/// <summary>
/// Velocity.y に適用する重力加速度。
/// </summary>
public struct Gravity : IComponentData
{
    /// <summary>
    /// Velocity.y に加算する加速度。
    /// </summary>
    public float Acceleration;
}

/// <summary>
/// Entity の接地状態と、最後に検出した地面高さ。
/// </summary>
public struct GroundSnap : IComponentData
{
    /// <summary>
    /// 最後に接地した地面の world Y。
    /// </summary>
    public float GroundY;

    /// <summary>
    /// 0 = airborne, 1 = grounded。
    /// </summary>
    public byte IsGrounded;
}

/// <summary>
/// 低 FPS などで接地判定をすり抜けた Entity を最後に記録した地面高さへ戻す設定。
/// </summary>
public struct GroundFallRescue : IComponentData
{
    /// <summary>
    /// GroundY よりこの距離以上落ちたら救済する。0 以下は無効。
    /// </summary>
    public float MaxBelowGroundY;
}

/// <summary>
/// 接地できる地形 Entity のタグ。
/// </summary>
public struct GroundTag : IComponentData
{

}

/// <summary>
/// XZ 平面の移動で侵入できない static obstacle のタグ。
/// </summary>
public struct StaticObstacleTag : IComponentData
{

}

/// <summary>
/// 接地判定に使う SensorCollider の SphereCollider 情報。
/// </summary>
public struct GroundSensor : IComponentData
{
    /// <summary>
    /// Entity root から見た sensor sphere の local 中心。
    /// </summary>
    public float3 LocalCenter;

    /// <summary>
    /// Sensor sphere の半径。
    /// </summary>
    public float Radius;

    /// <summary>
    /// 接地とみなす余白距離。
    /// </summary>
    public float Skin;
}

/// <summary>
/// XZ 平面での向き。x が world X、y が world Z。
/// </summary>
public struct FacingDirection : IComponentData
{
    /// <summary>
    /// 正規化済みの XZ 向き。停止中は最後の有効向きを保持する。
    /// </summary>
    public float2 Value;
}

/// <summary>
/// プレイヤーへ直線移動する最小 AI のタグ。
/// </summary>
public struct MonsterSimpleAi : IComponentData
{

}
