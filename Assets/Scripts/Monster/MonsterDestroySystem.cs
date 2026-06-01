using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// HP が 0 以下になった Monster を death VFX 状態へ移行し、通常 monster は再利用、特殊 monster は削除する。
///
/// 死亡開始時は structural change を EntityCommandBuffer に積み、Velocity / CollisionRadius を外すか無効化して
/// AI、移動、衝突処理から切り離す。通常 monster は Entity を破棄せず、VFX 完了後に再配置して再利用する。
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
        var gainedExperience = 0L;
        var experienceRewardLookup = SystemAPI.GetComponentLookup<ExperienceReward>(true);

        foreach (var (health, transform, entity) in
                 SystemAPI.Query<RefRO<HealthComponent>, RefRO<LocalTransform>>()
                     .WithAll<MonsterTag, MonsterDestroyVfxConfig>()
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

            if (experienceRewardLookup.HasComponent(entity))
            {
                gainedExperience += ExperienceMath.NormalizeReward(experienceRewardLookup[entity].Value);
            }

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

        if (gainedExperience > 0L)
        {
            AddExperienceToPlayers(
                ref state,
                gainedExperience > int.MaxValue ? int.MaxValue : (int)gainedExperience);
        }

        startCommandBuffer.Playback(state.EntityManager);
        startCommandBuffer.Dispose();

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
            var progress = MonsterDestroyVfxMath.CalculateProgress(
                vfxState.ValueRO.ElapsedTime,
                config.ValueRO.Duration);

            if (progress < 1f)
            {
                continue;
            }

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

        updateCommandBuffer.Playback(state.EntityManager);
        updateCommandBuffer.Dispose();
    }

    private void AddExperienceToPlayers(ref SystemState state, int gainedExperience)
    {
        if (gainedExperience <= 0)
        {
            return;
        }

        var levelConfigLookup = SystemAPI.GetComponentLookup<ExperienceLevelConfig>(true);

        foreach (var (experience, entity) in
                 SystemAPI.Query<RefRW<ExperienceComponent>>()
                     .WithAll<PlayerTag>()
                     .WithEntityAccess())
        {
            var levelConfig = levelConfigLookup.HasComponent(entity)
                ? levelConfigLookup[entity]
                : ExperienceMath.CreateDefaultLevelConfig();
            var nextExperience = ExperienceMath.AddExperience(
                experience.ValueRO,
                gainedExperience,
                levelConfig);

            experience.ValueRW.CurrentExperience = nextExperience.CurrentExperience;
            experience.ValueRW.RequiredExperience = nextExperience.RequiredExperience;
            experience.ValueRW.Level = nextExperience.Level;
        }
    }

    /// <summary>
    /// 討伐数ステージが存在する場合だけ、死亡確定した通常 monster 数を進行度へ加算する。
    /// </summary>
    private void AddKillCount(ref SystemState state, int killedMonsterCount)
    {
        foreach (var stage in SystemAPI.Query<RefRW<KillCountStageProgress>>())
        {
            stage.ValueRW.CurrentKillCount = StageProgressMath.AddKillCount(
                stage.ValueRO.CurrentKillCount,
                killedMonsterCount);
        }
    }

    /// <summary>
    /// 死亡 VFX 後の再配置に必要な spawn 設定、player 位置、地面高さを取得する。
    /// </summary>
    /// <returns>通常 monster を recycle できるだけの情報が揃っていれば true。</returns>
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

    /// <summary>
    /// 死亡 VFX が終わった通常 monster を、player 周辺の画面外 ring に戻す。
    /// </summary>
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

    /// <summary>
    /// GroundSensor を持つ prefab の root が、sensor 下端で地面に接する位置を計算する。
    /// </summary>
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

    /// <summary>
    /// recycle した monster を次の生存サイクルへ戻すため、runtime 状態を初期値へ戻す。
    /// </summary>
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

        if (state.EntityManager.HasComponent<DebuffRuntimeState>(entity))
        {
            entityCommandBuffer.SetComponent(entity, new DebuffRuntimeState
            {
                ActiveDebuffs = default
            });
        }

        if (state.EntityManager.HasComponent<DebuffAggregate>(entity))
        {
            entityCommandBuffer.SetComponent(entity, DebuffMath.CreateNeutralAggregate());
        }

        if (state.EntityManager.HasComponent<FreezeTag>(entity))
        {
            entityCommandBuffer.RemoveComponent<FreezeTag>(entity);
        }

        if (state.EntityManager.HasComponent<FreezeComponent>(entity))
        {
            entityCommandBuffer.RemoveComponent<FreezeComponent>(entity);
        }

        if (state.EntityManager.HasComponent<MonsterHitVfxConfig>(entity))
        {
            // 被弾色や死亡色が残らないように、linked render entity の base color も戻す。
            VFXMaterialUtility.AddOrSetBaseColorForLinkedRenderEntities(
                ref state,
                ref entityCommandBuffer,
                entity,
                state.EntityManager.GetComponentData<MonsterHitVfxConfig>(entity).RestBaseColor);
        }

        entityCommandBuffer.RemoveComponent<MonsterDestroyVfxState>(entity);
    }

    /// <summary>
    /// root と linked render / collider entity をまとめて破棄する。
    /// </summary>
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
///
/// VFX の runtime 状態を持たない純粋計算にして、System から独立して検証できるようにする。
/// </summary>
public static class MonsterDestroyVfxMath
{
    /// <summary>
    /// VFX duration を 0 より大きい値へ正規化する。
    /// </summary>
    public static float NormalizeDuration(float duration)
    {
        return math.max(0.01f, math.abs(duration));
    }

    /// <summary>
    /// elapsed / duration から 0..1 の再生率を返す。
    /// </summary>
    public static float CalculateProgress(float elapsedTime, float duration)
    {
        return math.clamp(elapsedTime / NormalizeDuration(duration), 0f, 1f);
    }

    /// <summary>
    /// 死亡 VFX 中の scale を線形補間で計算する。
    /// </summary>
    public static float CalculateScale(float originalScale, float endScale, float progress)
    {
        var safeProgress = math.clamp(progress, 0f, 1f);
        var safeOriginalScale = math.max(0f, originalScale);
        var safeEndScale = math.max(0f, endScale);

        return math.lerp(safeOriginalScale, safeEndScale, safeProgress);
    }

    /// <summary>
    /// 死亡 VFX 中の base color を線形補間で計算する。
    /// </summary>
    public static float4 CalculateBaseColor(float4 startColor, float4 endColor, float progress)
    {
        return math.lerp(startColor, endColor, math.clamp(progress, 0f, 1f));
    }
}
