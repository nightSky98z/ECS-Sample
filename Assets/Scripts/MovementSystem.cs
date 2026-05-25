using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// MonsterSimpleAi の対象速度を計算する。
/// </summary>
[UpdateBefore(typeof(MovementSystem))]
public partial struct MonsterSimpleAiSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
        state.RequireForUpdate<MonsterSimpleAi>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var hasPlayer = false;
        var playerPosition = float3.zero;

        foreach (var playerTransform in
                 SystemAPI.Query<RefRO<LocalTransform>>()
                     .WithAll<PlayerTag>())
        {
            playerPosition = playerTransform.ValueRO.Position;
            hasPlayer = true;
            break;
        }

        if (!hasPlayer)
        {
            return;
        }

        foreach (var (velocity, transform, speed) in
                 SystemAPI.Query<RefRW<Velocity>, RefRO<LocalTransform>, RefRO<MoveSpeed>>()
                     .WithAll<MonsterTag>()
                     .WithAll<MonsterSimpleAi>())
        {
            var currentVelocity = velocity.ValueRO.Value;
            var chaseVelocity = MonsterSimpleAiMath.CalculateChaseVelocity(
                transform.ValueRO.Position,
                playerPosition,
                speed.ValueRO.Value);

            velocity.ValueRW.Value = new float3(chaseVelocity.x, currentVelocity.y, chaseVelocity.z);
        }
    }
}

/// <summary>
/// Velocity から LocalTransform を更新し、ゲーム用の接触半径で位置を補正する。
/// </summary>
[UpdateAfter(typeof(PhysicsSystem))]
public partial struct MovementSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var deltaTime = SystemAPI.Time.DeltaTime;

        foreach (var (input, velocity, speed, knockback) in
                 SystemAPI.Query<RefRO<PlayerInput>, RefRW<Velocity>, RefRO<MoveSpeed>, RefRW<KnockbackVelocity>>()
                     .WithAll<PlayerTag>())
        {
            var inputMove = input.ValueRO.Move;
            var nextVelocity = MovementMath.ComposeVelocity(inputMove, speed.ValueRO.Value, knockback.ValueRO.Value);
            var currentVelocity = velocity.ValueRO.Value;

            velocity.ValueRW.Value = new float3(nextVelocity.x, currentVelocity.y, nextVelocity.z);
            knockback.ValueRW.Value = MovementMath.DecayVelocity(
                knockback.ValueRO.Value,
                knockback.ValueRO.DecayPerSecond,
                deltaTime);
        }

        foreach (var (transform, velocity, playerRadius, facing) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRW<Velocity>, RefRO<CollisionRadius>, RefRW<FacingDirection>>()
                     .WithAll<PlayerTag>())
        {
            var currentVelocity = velocity.ValueRO.Value;
            var nextPosition = transform.ValueRO.Position + currentVelocity * deltaTime;

            foreach (var (monsterTransform, monsterRadius) in
                     SystemAPI.Query<RefRO<LocalTransform>, RefRO<CollisionRadius>>()
                         .WithAll<MonsterTag>())
            {
                nextPosition = MovementMath.ResolveCirclePenetration(
                    nextPosition,
                    playerRadius.ValueRO.Value,
                    monsterTransform.ValueRO.Position,
                    monsterRadius.ValueRO.Value);
            }

            var nextFacing = MovementMath.CalculateFacingDirection(
                facing.ValueRO.Value,
                currentVelocity);

            transform.ValueRW.Position = nextPosition;
            transform.ValueRW.Rotation = MovementMath.CalculateFacingRotation(nextFacing);
            velocity.ValueRW.Value = currentVelocity;
            facing.ValueRW.Value = nextFacing;
        }

        foreach (var (transform, velocity, monsterRadius, facing) in
                 SystemAPI.Query<RefRW<LocalTransform>, RefRW<Velocity>, RefRO<CollisionRadius>, RefRW<FacingDirection>>()
                     .WithAll<MonsterTag>())
        {
            var currentVelocity = velocity.ValueRO.Value;
            var nextPosition = transform.ValueRO.Position + currentVelocity * deltaTime;

            foreach (var (playerTransform, playerRadius) in
                     SystemAPI.Query<RefRO<LocalTransform>, RefRO<CollisionRadius>>()
                         .WithAll<PlayerTag>())
            {
                nextPosition = MovementMath.ResolveCirclePenetration(
                    nextPosition,
                    monsterRadius.ValueRO.Value,
                    playerTransform.ValueRO.Position,
                    playerRadius.ValueRO.Value);
            }

            var nextFacing = MovementMath.CalculateFacingDirection(
                facing.ValueRO.Value,
                currentVelocity);

            transform.ValueRW.Position = nextPosition;
            transform.ValueRW.Rotation = MovementMath.CalculateFacingRotation(nextFacing);
            velocity.ValueRW.Value = currentVelocity;
            facing.ValueRW.Value = nextFacing;
        }
    }
}

/// <summary>
/// Velocity.y に重力を適用する。
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
        var deltaTime = SystemAPI.Time.DeltaTime;

        foreach (var (velocity, gravity, groundSnap) in
                 SystemAPI.Query<RefRW<Velocity>, RefRO<Gravity>, RefRO<GroundSnap>>())
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
    public float3 Position;
    public float3 Velocity;
    public byte IsGrounded;
}

/// <summary>
/// PhysicsSystem が使う物理計算。
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
}

/// <summary>
/// MovementSystem が使う移動計算。
/// </summary>
public static class MovementMath
{
    public static float3 ComposeVelocity(float2 inputMove, float moveSpeed, float3 knockbackVelocity)
    {
        return new float3(inputMove.x, 0f, inputMove.y) * moveSpeed + knockbackVelocity;
    }

    public static float3 DecayVelocity(float3 velocity, float decayPerSecond, float deltaTime)
    {
        var speedSq = math.lengthsq(velocity);

        if (speedSq <= 0f || decayPerSecond <= 0f || deltaTime <= 0f)
        {
            return velocity;
        }

        var speed = math.sqrt(speedSq);
        var nextSpeed = math.max(0f, speed - decayPerSecond * deltaTime);

        if (nextSpeed <= 0f)
        {
            return float3.zero;
        }

        return velocity * (nextSpeed / speed);
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
    /// XZ 平面の速度から向きを更新する。Y 速度だけでは向きを変えない。
    /// </summary>
    /// <param name="currentFacing">停止時に維持する現在向き。</param>
    /// <param name="velocity">現在速度。x/z だけを参照する。</param>
    /// <returns>次の XZ 平面向き。</returns>
    public static float2 CalculateFacingDirection(float2 currentFacing, float3 velocity)
    {
        var move = new float2(velocity.x, velocity.z);
        var moveLengthSq = math.lengthsq(move);

        if (moveLengthSq <= 0.000001f)
        {
            return currentFacing;
        }

        return move * math.rsqrt(moveLengthSq);
    }

    /// <summary>
    /// XZ 平面の向きを LocalTransform.Rotation に使う Y 軸回転へ変換する。
    /// </summary>
    /// <param name="facing">XZ 平面向き。</param>
    /// <returns>+Z を正面とする Y 軸回転。</returns>
    public static quaternion CalculateFacingRotation(float2 facing)
    {
        var facingLengthSq = math.lengthsq(facing);

        if (facingLengthSq <= 0.000001f)
        {
            return quaternion.identity;
        }

        var normalizedFacing = facing * math.rsqrt(facingLengthSq);

        return quaternion.LookRotationSafe(
            new float3(normalizedFacing.x, 0f, normalizedFacing.y),
            math.up());
    }

    /// <summary>
    /// XZ 平面の円同士が重なっている場合、移動側の位置を外側へ押し戻す。
    /// </summary>
    /// <param name="movingPosition">補正対象の位置。y は補正後も維持される。</param>
    /// <param name="movingRadius">補正対象の接触半径。0 以下なら補正しない。</param>
    /// <param name="blockingPosition">侵入できない相手側の位置。</param>
    /// <param name="blockingRadius">相手側の接触半径。0 以下なら補正しない。</param>
    /// <returns>重なりが解消された補正対象の位置。</returns>
    public static float3 ResolveCirclePenetration(
        float3 movingPosition,
        float movingRadius,
        float3 blockingPosition,
        float blockingRadius)
    {
        var minDistance = movingRadius + blockingRadius;

        if (minDistance <= 0f)
        {
            return movingPosition;
        }

        var delta = new float2(
            movingPosition.x - blockingPosition.x,
            movingPosition.z - blockingPosition.z);
        var distanceSq = math.lengthsq(delta);
        var minDistanceSq = minDistance * minDistance;

        if (distanceSq >= minDistanceSq)
        {
            return movingPosition;
        }

        if (distanceSq <= 0.000001f)
        {
            return new float3(
                blockingPosition.x + minDistance,
                movingPosition.y,
                blockingPosition.z);
        }

        var distance = math.sqrt(distanceSq);
        var normal = delta / distance;

        return new float3(
            blockingPosition.x + normal.x * minDistance,
            movingPosition.y,
            blockingPosition.z + normal.y * minDistance);
    }
}

/// <summary>
/// MonsterSimpleAiSystem が使う移動判断。
/// </summary>
public static class MonsterSimpleAiMath
{
    /// <summary>
    /// 現在位置から target へ向かう XZ 平面上の速度を返す。
    /// </summary>
    /// <param name="monsterPosition">モンスターの現在位置。</param>
    /// <param name="targetPosition">追跡対象の現在位置。</param>
    /// <param name="moveSpeed">移動速度。0 以下なら停止する。</param>
    /// <returns>Y 成分を含まない追跡速度。</returns>
    public static float3 CalculateChaseVelocity(
        float3 monsterPosition,
        float3 targetPosition,
        float moveSpeed)
    {
        if (moveSpeed <= 0f)
        {
            return float3.zero;
        }

        var toTarget = new float2(
            targetPosition.x - monsterPosition.x,
            targetPosition.z - monsterPosition.z);
        var distanceSq = math.lengthsq(toTarget);

        if (distanceSq <= 0.000001f)
        {
            return float3.zero;
        }

        var direction = toTarget * math.rsqrt(distanceSq);

        return new float3(direction.x, 0f, direction.y) * moveSpeed;
    }
}
