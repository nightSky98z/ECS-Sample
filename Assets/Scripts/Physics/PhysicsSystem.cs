using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// Velocity.y に重力を適用する。
///
/// 水平移動は MovementSystem、接地検出は GroundSensorSystem が担当する。
/// この System は「落下速度を積分する」責務だけに閉じる。
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
/// 地面接地処理の結果。
/// </summary>
public struct GroundSnapResult
{
    /// <summary>
    /// 接地補正後の root 位置。
    /// </summary>
    public float3 Position;

    /// <summary>
    /// 接地補正後の速度。下向き速度は接地時に 0 へ丸める。
    /// </summary>
    public float3 Velocity;

    /// <summary>
    /// 0 = airborne, 1 = grounded。
    /// </summary>
    public byte IsGrounded;
}

/// <summary>
/// PhysicsSystem と接地処理が使う物理計算。
///
/// Unity Physics の query 結果を直接保持せず、テスト可能な値計算だけをここに置く。
/// </summary>
public static class PhysicsMath
{
    /// <summary>
    /// 接地状態を見ながら Velocity.y に重力加速度を適用する。
    /// </summary>
    /// <param name="velocityY">現在の Y 速度。</param>
    /// <param name="acceleration">Y 速度へ加算する重力加速度。</param>
    /// <param name="deltaTime">今回の更新秒数。</param>
    /// <param name="isGrounded">0 以外なら接地中として扱う。</param>
    /// <returns>次の Y 速度。</returns>
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
    /// 足元 pivot の位置を地面高さへ制限し、落下速度を消す。
    /// </summary>
    /// <param name="position">接地判定前の位置。</param>
    /// <param name="velocity">接地判定前の速度。</param>
    /// <param name="groundY">足元 pivot が接地するワールド Y 座標。</param>
    /// <returns>接地補正後の位置、速度、接地状態。</returns>
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
    /// SensorCollider sphere の下端を Ground collider の高さへ合わせ、落下速度を消す。
    /// </summary>
    /// <param name="position">接地補正前の Entity root 位置。</param>
    /// <param name="velocity">接地補正前の速度。</param>
    /// <param name="sensorCenterY">SensorCollider sphere 中心のワールド Y 座標。</param>
    /// <param name="sensorRadius">SensorCollider sphere のワールド半径。</param>
    /// <param name="groundY">Ground collider 上の最接近点 Y 座標。</param>
    /// <param name="skin">接地として許容する余白距離。</param>
    /// <returns>接地補正後の位置、速度、接地状態。</returns>
    public static GroundSnapResult SnapToGroundFromSensor(
        float3 position,
        float3 velocity,
        float sensorCenterY,
        float sensorRadius,
        float groundY,
        float skin)
    {
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
    /// 接地 query をすり抜けて最後の地面高さより深く落ちた Entity を救済する。
    /// </summary>
    /// <param name="position">救済前の位置。</param>
    /// <param name="velocity">救済前の速度。</param>
    /// <param name="groundY">最後に記録した地面高さ。</param>
    /// <param name="maxBelowGroundY">この深さを超えたら救済する。0 以下は救済しない。</param>
    /// <returns>救済後の位置、速度、接地状態。</returns>
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
