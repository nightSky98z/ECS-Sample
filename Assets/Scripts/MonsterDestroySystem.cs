using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

/// <summary>
/// HP が 0 以下になった Monster を death VFX 状態へ移行し、通常 monster は再利用、特殊 monster は削除する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillLogicSystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
public partial struct MonsterDestroySystem : ISystem
{
    private uint deathRecycleSequence;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterTag>();
        state.RequireForUpdate<MonsterDestroyVfxConfig>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var startCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var killedMonsterCount = 0;

        foreach (var (health, config, transform, entity) in
                 SystemAPI.Query<RefRO<HealthComponent>, RefRO<MonsterDestroyVfxConfig>, RefRO<LocalTransform>>()
                     .WithAll<MonsterTag>()
                     .WithNone<MonsterDestroyVfxState>()
                     .WithEntityAccess())
        {
            if (health.ValueRO.CurrentHp > 0)
            {
                continue;
            }

            var originalCollisionRadius = state.EntityManager.HasComponent<CollisionRadius>(entity)
                ? state.EntityManager.GetComponentData<CollisionRadius>(entity).Value
                : 0f;

            startCommandBuffer.AddComponent(entity, new MonsterDestroyVfxState
            {
                ElapsedTime = 0f,
                OriginalScale = transform.ValueRO.Scale,
                OriginalCollisionRadius = originalCollisionRadius
            });
            killedMonsterCount++;

            if (state.EntityManager.HasComponent<Velocity>(entity))
            {
                if (state.EntityManager.HasComponent<MonsterRecycleTag>(entity))
                {
                    startCommandBuffer.SetComponent(entity, new Velocity
                    {
                        Value = float3.zero
                    });
                }
                else
                {
                    startCommandBuffer.RemoveComponent<Velocity>(entity);
                }
            }

            AddOrSetMaterialColorForLinkedRenderEntities(
                ref state,
                ref startCommandBuffer,
                entity,
                new MonsterMaterialBaseColor
                {
                    Value = config.ValueRO.StartBaseColor
                });

            if (state.EntityManager.HasComponent<CollisionRadius>(entity))
            {
                startCommandBuffer.SetComponent(entity, new CollisionRadius
                {
                    Value = 0f
                });
            }
        }

        if (killedMonsterCount > 0)
        {
            AddKillCount(ref state, killedMonsterCount);
        }

        startCommandBuffer.Playback(state.EntityManager);
        startCommandBuffer.Dispose();

        var deltaTime = SystemAPI.Time.DeltaTime;
        var updateCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var hasRecycleContext = TryGetDeathRecycleContext(
            ref state,
            out var recycleConfig,
            out var playerPosition,
            out var groundY);
        var recycleRandom = new Unity.Mathematics.Random(1u);

        if (hasRecycleContext)
        {
            recycleRandom = new Unity.Mathematics.Random(
                MonsterSpawnDirectorUtility.CreateTimedSpawnSeed(
                    recycleConfig.WorldSeed,
                    playerPosition,
                    deathRecycleSequence++));
        }

        foreach (var (vfxState, config, transform, entity) in
                 SystemAPI.Query<RefRW<MonsterDestroyVfxState>, RefRO<MonsterDestroyVfxConfig>, RefRW<LocalTransform>>()
                     .WithAll<MonsterTag>()
                     .WithEntityAccess())
        {
            var nextElapsedTime = vfxState.ValueRO.ElapsedTime + deltaTime;
            var progress = MonsterDestroyVfxMath.CalculateProgress(
                nextElapsedTime,
                config.ValueRO.Duration);
            var baseColor = MonsterDestroyVfxMath.CalculateBaseColor(
                config.ValueRO.StartBaseColor,
                config.ValueRO.EndBaseColor,
                progress);

            vfxState.ValueRW.ElapsedTime = nextElapsedTime;
            transform.ValueRW.Scale = MonsterDestroyVfxMath.CalculateScale(
                vfxState.ValueRO.OriginalScale,
                config.ValueRO.EndScale,
                progress);
            AddOrSetMaterialColorForLinkedRenderEntities(
                ref state,
                ref updateCommandBuffer,
                entity,
                new MonsterMaterialBaseColor
                {
                    Value = baseColor
                });

            if (progress >= 1f)
            {
                if (hasRecycleContext &&
                    state.EntityManager.HasComponent<MonsterRecycleTag>(entity))
                {
                    RecycleMonsterAfterDeathVfx(
                        ref state,
                        ref updateCommandBuffer,
                        entity,
                        ref transform.ValueRW,
                        vfxState.ValueRO,
                        recycleConfig,
                        playerPosition,
                        groundY,
                        ref recycleRandom);
                    continue;
                }

                DestroyLinkedEntityGroup(ref state, ref updateCommandBuffer, entity);
            }
        }

        updateCommandBuffer.Playback(state.EntityManager);
        updateCommandBuffer.Dispose();
    }

    private void AddKillCount(ref SystemState state, int killedMonsterCount)
    {
        foreach (var stage in SystemAPI.Query<RefRW<KillCountStageProgress>>())
        {
            stage.ValueRW.CurrentKillCount = StageProgressMath.AddKillCount(
                stage.ValueRO.CurrentKillCount,
                killedMonsterCount);
        }
    }

    private bool TryGetDeathRecycleContext(
        ref SystemState state,
        out MonsterSpawnDirectorConfig config,
        out float3 playerPosition,
        out float groundY)
    {
        config = default;
        playerPosition = float3.zero;
        groundY = 0f;
        var hasConfig = false;

        foreach (var configValue in SystemAPI.Query<RefRO<MonsterSpawnDirectorConfig>>())
        {
            config = configValue.ValueRO;
            hasConfig = true;
            break;
        }

        if (!hasConfig)
        {
            return false;
        }

        foreach (var (transform, entity) in
                 SystemAPI.Query<RefRO<LocalTransform>>()
                     .WithAll<PlayerTag>()
                     .WithEntityAccess())
        {
            playerPosition = transform.ValueRO.Position;
            groundY = playerPosition.y;

            if (state.EntityManager.HasComponent<GroundSnap>(entity))
            {
                groundY = state.EntityManager.GetComponentData<GroundSnap>(entity).GroundY;
            }

            return true;
        }

        return false;
    }

    private void RecycleMonsterAfterDeathVfx(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity entity,
        ref LocalTransform transform,
        MonsterDestroyVfxState vfxState,
        MonsterSpawnDirectorConfig recycleConfig,
        float3 playerPosition,
        float groundY,
        ref Unity.Mathematics.Random random)
    {
        var recyclePosition = MonsterSpawnDirectorUtility.CalculatePlayerRingSpawnPosition(
            playerPosition,
            groundY,
            recycleConfig.MinSpawnDistanceFromPlayer,
            recycleConfig.MaxSpawnDistanceFromPlayer,
            ref random);

        transform = CalculateRecycleTransform(
            ref state,
            entity,
            recyclePosition,
            transform.Rotation,
            vfxState.OriginalScale);
        ResetRecycledMonsterRuntimeState(
            ref state,
            ref entityCommandBuffer,
            entity,
            groundY,
            vfxState.OriginalCollisionRadius);
    }

    private static LocalTransform CalculateRecycleTransform(
        ref SystemState state,
        Entity entity,
        float3 groundPosition,
        quaternion rotation,
        float scale)
    {
        var position = groundPosition;

        if (state.EntityManager.HasComponent<GroundSensor>(entity))
        {
            position = MonsterSpawnDirectorUtility.CalculateGroundedSpawnPosition(
                groundPosition,
                state.EntityManager.GetComponentData<GroundSensor>(entity),
                rotation,
                scale);
        }

        return LocalTransform.FromPositionRotationScale(position, rotation, scale);
    }

    private void ResetRecycledMonsterRuntimeState(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity entity,
        float groundY,
        float originalCollisionRadius)
    {
        if (state.EntityManager.HasComponent<HealthComponent>(entity))
        {
            var health = state.EntityManager.GetComponentData<HealthComponent>(entity);

            health.CurrentHp = health.MaxHp;
            entityCommandBuffer.SetComponent(entity, health);
        }

        if (state.EntityManager.HasComponent<Velocity>(entity))
        {
            entityCommandBuffer.SetComponent(entity, new Velocity
            {
                Value = float3.zero
            });
        }
        else
        {
            entityCommandBuffer.AddComponent(entity, new Velocity
            {
                Value = float3.zero
            });
        }

        if (state.EntityManager.HasComponent<CollisionRadius>(entity))
        {
            entityCommandBuffer.SetComponent(entity, new CollisionRadius
            {
                Value = originalCollisionRadius
            });
        }

        if (state.EntityManager.HasComponent<GroundSnap>(entity))
        {
            entityCommandBuffer.SetComponent(entity, new GroundSnap
            {
                GroundY = groundY,
                IsGrounded = 1
            });
        }

        if (state.EntityManager.HasComponent<MonsterHitVfxState>(entity))
        {
            entityCommandBuffer.SetComponent(entity, new MonsterHitVfxState
            {
                ElapsedTime = 0f,
                IsPlaying = 0
            });
        }

        if (state.EntityManager.HasComponent<MonsterHitVfxConfig>(entity))
        {
            AddOrSetMaterialColorForLinkedRenderEntities(
                ref state,
                ref entityCommandBuffer,
                entity,
                new MonsterMaterialBaseColor
                {
                    Value = state.EntityManager.GetComponentData<MonsterHitVfxConfig>(entity).RestBaseColor
                });
        }

        entityCommandBuffer.RemoveComponent<MonsterDestroyVfxState>(entity);
    }

    private void AddOrSetMaterialColorForLinkedRenderEntities(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity rootEntity,
        MonsterMaterialBaseColor color)
    {
        if (!state.EntityManager.HasBuffer<LinkedEntityGroup>(rootEntity))
        {
            AddOrSetMaterialColorForRenderEntity(ref state, ref entityCommandBuffer, rootEntity, color);
            return;
        }

        var linkedEntities = state.EntityManager.GetBuffer<LinkedEntityGroup>(rootEntity);

        for (var linkedEntityIndex = 0; linkedEntityIndex < linkedEntities.Length; linkedEntityIndex++)
        {
            AddOrSetMaterialColorForRenderEntity(
                ref state,
                ref entityCommandBuffer,
                linkedEntities[linkedEntityIndex].Value,
                color);
        }
    }

    private void AddOrSetMaterialColorForRenderEntity(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity entity,
        MonsterMaterialBaseColor color)
    {
        if (!state.EntityManager.HasComponent<MaterialMeshInfo>(entity))
        {
            return;
        }

        if (state.EntityManager.HasComponent<MonsterMaterialBaseColor>(entity))
        {
            entityCommandBuffer.SetComponent(entity, color);
        }
    }

    private void DestroyLinkedEntityGroup(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity rootEntity)
    {
        if (!state.EntityManager.HasBuffer<LinkedEntityGroup>(rootEntity))
        {
            entityCommandBuffer.DestroyEntity(rootEntity);
            return;
        }

        var linkedEntities = state.EntityManager.GetBuffer<LinkedEntityGroup>(rootEntity);

        for (var linkedEntityIndex = 0; linkedEntityIndex < linkedEntities.Length; linkedEntityIndex++)
        {
            entityCommandBuffer.DestroyEntity(linkedEntities[linkedEntityIndex].Value);
        }
    }
}

/// <summary>
/// Monster death VFX の時間、scale、material color 計算。
/// </summary>
public static class MonsterDestroyVfxMath
{
    public static float NormalizeDuration(float duration)
    {
        return math.max(0.01f, math.abs(duration));
    }

    public static float CalculateProgress(float elapsedTime, float duration)
    {
        return math.clamp(elapsedTime / NormalizeDuration(duration), 0f, 1f);
    }

    public static float CalculateScale(float originalScale, float endScale, float progress)
    {
        var safeProgress = math.clamp(progress, 0f, 1f);
        var safeOriginalScale = math.max(0f, originalScale);
        var safeEndScale = math.max(0f, endScale);

        return math.lerp(safeOriginalScale, safeEndScale, safeProgress);
    }

    public static float4 CalculateBaseColor(float4 startColor, float4 endColor, float progress)
    {
        return math.lerp(startColor, endColor, math.clamp(progress, 0f, 1f));
    }
}
