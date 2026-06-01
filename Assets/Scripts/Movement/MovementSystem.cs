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

        foreach (var (velocity, transform, speed, debuffs) in
                 SystemAPI.Query<RefRW<Velocity>, RefRO<LocalTransform>, RefRO<MoveSpeed>, RefRO<DebuffAggregate>>()
                     .WithAll<MonsterTag>()
                     .WithAll<MonsterSimpleAi>()
                     .WithNone<MonsterDestroyVfxState>())
        {
            var currentVelocity = velocity.ValueRO.Value;
            var moveSpeedMultiplier = debuffs.ValueRO.IsMovementLocked != 0
                ? 0f
                : debuffs.ValueRO.MoveSpeedMultiplier;
            var chaseVelocity = MonsterSimpleAiMath.CalculateChaseVelocity(
                transform.ValueRO.Position,
                playerPosition,
                speed.ValueRO.Value * moveSpeedMultiplier);

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
                         .WithAll<MonsterTag>()
                         .WithNone<MonsterDestroyVfxState>())
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
                     .WithAll<MonsterTag>()
                     .WithNone<MonsterDestroyVfxState>())
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
