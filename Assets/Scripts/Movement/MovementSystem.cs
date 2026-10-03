using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// 単純 AI のモンスターを、プレイヤーに向かってまっすぐ進ませる（水平方向の速度を決める）。
/// 縦方向（Y）の速度は PhysicsSystem が管理しているため、ここでは書き換えない。
/// これにより、AI が重力や接地の処理を壊さないようにしている。
/// デバフによる減速は DebuffAggregate の倍率を掛けるだけで反映される。
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
        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (!StageRuntimeUtility.IsGameplayPhase(runtimeState.ValueRO.Phase))
            {
                return;
            }
        }

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
            var moveSpeedMultiplier = debuffs.ValueRO.MoveSpeedMultiplier;
            var chaseVelocity = MonsterSimpleAiMath.CalculateChaseVelocity(
                transform.ValueRO.Position,
                playerPosition,
                speed.ValueRO.Value * moveSpeedMultiplier);

            velocity.ValueRW.Value = new float3(chaseVelocity.x, currentVelocity.y, chaseVelocity.z);
        }
    }
}

/// <summary>
/// Velocity に従ってプレイヤーとモンスターの位置を動かし、向きを更新する。
/// Rigidbody は使わず、Velocity と CollisionRadius を直接計算することで、大量の Entity でも軽く動くようにしている。
/// 押し戻しの対象はプレイヤーとモンスターの間だけ。木や岩などの障害物との衝突は StaticObstacleCollisionSystem が担当する。
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
        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (!StageRuntimeUtility.IsGameplayPhase(runtimeState.ValueRO.Phase))
            {
                return;
            }
        }

        var deltaTime = SystemAPI.Time.DeltaTime;

        // --- 1. プレイヤーの速度を決める ---
        // 水平方向の速度は、入力とノックバックを合成した値で毎フレーム上書きする。
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

        // --- 2. プレイヤーを移動させ、モンスターと重なっていたら押し戻す ---
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
                // 押し戻しは XZ 平面だけで行い、上下方向には動かさない（地面から浮かないようにする）。
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

        // --- 3. モンスターを移動させ、プレイヤーと重なっていたら押し戻す ---
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
/// 移動の計算処理。
/// 状態を持たない計算だけをまとめた static クラスにして、単体テストしやすくしている。
/// </summary>
public static class MovementMath
{
    /// <summary>
    /// 入力による移動とノックバックを合成し、水平方向の速度を返す。
    /// </summary>
    /// <param name="inputMove">移動入力（XZ、長さは最大 1）。</param>
    /// <param name="moveSpeed">基本の移動速度。</param>
    /// <param name="knockbackVelocity">ノックバックなどで残っている速度。</param>
    /// <returns>合成した水平方向の速度（Y は 0）。</returns>
    public static float3 ComposeVelocity(float2 inputMove, float moveSpeed, float3 knockbackVelocity)
    {
        return new float3(inputMove.x, 0f, inputMove.y) * moveSpeed + knockbackVelocity;
    }

    /// <summary>
    /// 速度の向きを保ったまま、大きさだけを一定量減らす。
    /// </summary>
    /// <param name="velocity">減衰前の速度。</param>
    /// <param name="decayPerSecond">1 秒あたりに減らす速度量。</param>
    /// <param name="deltaTime">経過時間（秒）。</param>
    /// <returns>減衰後の速度。</returns>
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
    /// 水平方向の速度から向きを求める。止まっているとき（落下中を含む）は今の向きを保つ。
    /// </summary>
    /// <param name="currentFacing">今の向き。止まっているときはこの向きを返す。</param>
    /// <param name="velocity">現在の速度。x と z だけを使う。</param>
    /// <returns>新しい向き（XZ 平面）。</returns>
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
    /// XZ 平面の向きを、LocalTransform.Rotation に設定する Y 軸回転に変換する。
    /// </summary>
    /// <param name="facing">XZ 平面の向き。</param>
    /// <returns>+Z を正面とした Y 軸回転。</returns>
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
    /// 2 つの円（XZ 平面）が重なっている場合、動いている側を相手の外側へ押し出す。
    /// </summary>
    /// <param name="movingPosition">押し出す側の位置。y はそのまま保つ。</param>
    /// <param name="movingRadius">押し出す側の半径。</param>
    /// <param name="blockingPosition">相手（動かない側）の位置。</param>
    /// <param name="blockingRadius">相手の半径。</param>
    /// <returns>重なりを解消した位置。</returns>
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

        // 完全に同じ位置にいる場合は押し出す方向が決まらないため、+X 方向へ押し出す。
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
/// 単純 AI の移動計算。
/// </summary>
public static class MonsterSimpleAiMath
{
    /// <summary>
    /// 現在の位置からターゲットへまっすぐ向かう速度（XZ 平面）を返す。
    /// </summary>
    /// <param name="monsterPosition">モンスターの位置。</param>
    /// <param name="targetPosition">追いかける相手の位置。</param>
    /// <param name="moveSpeed">移動速度。0 以下なら止まる。</param>
    /// <returns>追いかける速度（Y は 0）。</returns>
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
