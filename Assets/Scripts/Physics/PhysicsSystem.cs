using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// 重力を Velocity.y に加え、落下速度を計算する。
/// 役割分担：水平方向の移動は MovementSystem、接地の判定は GroundSensorSystem が担当し、
/// この System は「重力で落下速度を増やす」ことだけを行う。
/// Rigidbody を使わず自前で計算することで、大量の Entity でも処理を軽く保っている。
/// </summary>
[UpdateAfter(typeof(PlayerInputSystem))]
[UpdateAfter(typeof(MonsterSimpleAiSystem))]
[UpdateBefore(typeof(MovementSystem))]
public partial struct PhysicsSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<Gravity>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (!StageRuntimeUtility.IsGameplayPhase(runtimeState.ValueRO.Phase))
            {
                return;
            }
        }

        var deltaTime = SystemAPI.Time.DeltaTime;

        foreach (var (velocity, gravity, groundSnap) in
                 SystemAPI.Query<RefRW<Velocity>, RefRO<Gravity>, RefRO<GroundSnap>>()
                     .WithNone<MonsterDestroyVfxState>())
        {
            var currentVelocity = velocity.ValueRO.Value;

            currentVelocity.y = PhysicsMath.ApplyGravity(
                currentVelocity.y,
                gravity.ValueRO.Acceleration,
                deltaTime,
                groundSnap.ValueRO.IsGrounded);

            velocity.ValueRW.Value = currentVelocity;
        }
    }
}

/// <summary>
/// 接地処理の結果（補正後の位置・速度・接地しているか）。
/// </summary>
public struct GroundSnapResult
{
    /// <summary>
    /// 補正後の位置。
    /// </summary>
    public float3 Position;

    /// <summary>
    /// 補正後の速度。接地した場合、下向きの速度は 0 にする。
    /// </summary>
    public float3 Velocity;

    /// <summary>
    /// 1 なら接地中、0 なら空中。
    /// </summary>
    public byte IsGrounded;
}

/// <summary>
/// 重力と接地の計算処理。
/// Unity Physics に依存しない値の計算だけをまとめ、単体テストしやすくしている。
/// </summary>
public static class PhysicsMath
{
    /// <summary>
    /// 重力加速度を Y 方向の速度に加える。接地中で下向きに動いている場合は、速度を 0 にする。
    /// </summary>
    /// <param name="velocityY">現在の Y 方向の速度。</param>
    /// <param name="acceleration">重力加速度。</param>
    /// <param name="deltaTime">経過時間（秒）。</param>
    /// <param name="isGrounded">0 以外なら接地中。</param>
    /// <returns>新しい Y 方向の速度。</returns>
    public static float ApplyGravity(
        float velocityY,
        float acceleration,
        float deltaTime,
        byte isGrounded)
    {
        if (deltaTime <= 0f || acceleration == 0f)
        {
            return velocityY;
        }

        if (isGrounded != 0 && velocityY <= 0f)
        {
            return 0f;
        }

        return velocityY + acceleration * deltaTime;
    }

    /// <summary>
    /// 足元（Pivot）が地面より下にあれば地面の高さに合わせ、落下速度を 0 にする。
    /// </summary>
    /// <param name="position">補正前の位置。</param>
    /// <param name="velocity">補正前の速度。</param>
    /// <param name="groundY">地面の高さ（ワールド座標の Y）。</param>
    /// <returns>補正後の位置・速度・接地状態。</returns>
    public static GroundSnapResult SnapToGround(float3 position, float3 velocity, float groundY)
    {
        if (position.y > groundY)
        {
            return new GroundSnapResult
            {
                Position = position,
                Velocity = velocity,
                IsGrounded = 0
            };
        }

        if (velocity.y < 0f)
        {
            velocity.y = 0f;
        }

        position.y = groundY;

        return new GroundSnapResult
        {
            Position = position,
            Velocity = velocity,
            IsGrounded = 1
        };
    }

    /// <summary>
    /// 接地判定用の球の下端が地面に触れる高さまで Entity を移動させ、落下速度を 0 にする。
    /// 上向きに動いている場合や、地面から離れすぎている場合は接地とみなさない。
    /// </summary>
    /// <param name="position">補正前の Entity の位置。</param>
    /// <param name="velocity">補正前の速度。</param>
    /// <param name="sensorCenterY">判定用の球の中心の高さ（ワールド座標）。</param>
    /// <param name="sensorRadius">判定用の球の半径（ワールド座標）。</param>
    /// <param name="groundY">地面上の最も近い点の高さ。</param>
    /// <param name="skin">接地とみなす余裕の幅。</param>
    /// <returns>補正後の位置・速度・接地状態。</returns>
    public static GroundSnapResult SnapToGroundFromSensor(
        float3 position,
        float3 velocity,
        float sensorCenterY,
        float sensorRadius,
        float groundY,
        float skin)
    {
        // 球の下端（中心 - 半径）がちょうど地面に触れるときの、Entity 原点の高さ。
        var groundedPositionY = position.y + groundY + sensorRadius - sensorCenterY;

        if (velocity.y > 0f || position.y > groundedPositionY + skin)
        {
            return new GroundSnapResult
            {
                Position = position,
                Velocity = velocity,
                IsGrounded = 0
            };
        }

        if (velocity.y < 0f)
        {
            velocity.y = 0f;
        }

        position.y = groundedPositionY;

        return new GroundSnapResult
        {
            Position = position,
            Velocity = velocity,
            IsGrounded = 1
        };
    }

    /// <summary>
    /// 地面をすり抜けて、最後に記録した地面の高さより大きく落ちてしまった Entity を地面の上に戻す。
    /// （FPS が低いと 1 フレームの移動量が大きくなり、接地判定をすり抜けることがあるため）
    /// </summary>
    /// <param name="position">補正前の位置。</param>
    /// <param name="velocity">補正前の速度。</param>
    /// <param name="groundY">最後に記録した地面の高さ。</param>
    /// <param name="maxBelowGroundY">地面からこの距離以上落ちたら戻す。0 以下なら何もしない。</param>
    /// <returns>補正後の位置・速度・接地状態。</returns>
    public static GroundSnapResult RescueFallenBelowGround(
        float3 position,
        float3 velocity,
        float groundY,
        float maxBelowGroundY)
    {
        var safeMaxBelowGroundY = math.max(0f, maxBelowGroundY);

        if (safeMaxBelowGroundY <= 0f ||
            position.y >= groundY - safeMaxBelowGroundY)
        {
            return new GroundSnapResult
            {
                Position = position,
                Velocity = velocity,
                IsGrounded = 0
            };
        }

        if (velocity.y < 0f)
        {
            velocity.y = 0f;
        }

        position.y = groundY;

        return new GroundSnapResult
        {
            Position = position,
            Velocity = velocity,
            IsGrounded = 1
        };
    }
}
