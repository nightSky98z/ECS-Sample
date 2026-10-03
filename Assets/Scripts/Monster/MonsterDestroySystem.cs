using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// HP が 0 になったモンスターの死亡処理。
/// 1. 死亡演出を開始し、移動・当たり判定の対象から外す。あわせて経験値と討伐数を加算する
/// 2. 演出が終わったら、通常のモンスターはプレイヤーの周囲に再配置して使い回し、特殊な敵は削除する
///
/// Entity の構造変更（Component の付け外し）は EntityCommandBuffer にまとめ、ループの後で一括して反映する。
/// 通常のモンスターは Destroy / Instantiate を繰り返さずに再利用することで、大量の敵がいても負荷を抑えている。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillLogicSystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
public partial struct MonsterDestroySystem : ISystem
{
    // 再配置位置の乱数 seed を毎回変えるための通し番号。
    private uint deathRecycleSequence;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterTag>();
        state.RequireForUpdate<MonsterDestroyVfxConfig>();
    }

    public void OnUpdate(ref SystemState state)
    {
        // --- 1. 新しく HP が 0 になったモンスターの死亡演出を開始する ---
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

            // 移動を止める。再利用するモンスターは、後でまた使うので Component を外さず速度を 0 にするだけにする。
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

            // 当たり判定の半径を 0 にして、プレイヤーを押し戻さないようにする。
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

        // --- 2. 死亡演出が終わったモンスターを、再利用または削除する ---
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

    /// <summary>
    /// このフレームで倒したモンスターの経験値の合計を、プレイヤーに加算する（レベルアップは LevelUpSystem が行う）。
    /// </summary>
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
    /// 討伐数がクリア条件のステージであれば、倒したモンスターの数を進行度に加算する。
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
    /// 再配置に必要な情報（出現の設定・プレイヤーの位置・地面の高さ）を取得する。
    /// </summary>
    /// <returns>再配置に必要な情報がそろっていれば true。</returns>
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
    /// 死亡演出が終わったモンスターを、プレイヤーを囲むリング状の範囲（画面外）に再配置する。
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
    /// 再配置先の Transform を計算する。接地判定用の球があれば、その下端がちょうど地面に触れる高さに合わせる。
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
    /// 再利用するモンスターの状態（HP・速度・当たり判定・デバフ・色など）を初期値に戻す。
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
            // 被弾時や死亡時の色が残らないように、見た目の Entity の色も元に戻す。
            VFXMaterialUtility.AddOrSetBaseColorForLinkedRenderEntities(
                ref state,
                ref entityCommandBuffer,
                entity,
                state.EntityManager.GetComponentData<MonsterHitVfxConfig>(entity).RestBaseColor);
        }

        entityCommandBuffer.RemoveComponent<MonsterDestroyVfxState>(entity);
    }

    /// <summary>
    /// ルートの Entity と、それに紐付いた子の Entity（見た目・Collider など）をまとめて削除する。
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
/// モンスターの死亡演出の計算処理（時間・大きさ・色）。
/// 状態を持たない計算だけにすることで、System とは別に単体テストできるようにしている。
/// </summary>
public static class MonsterDestroyVfxMath
{
    /// <summary>
    /// 演出の長さが 0 以下にならないように補正する（0 で割るのを防ぐ）。
    /// </summary>
    public static float NormalizeDuration(float duration)
    {
        return math.max(0.01f, math.abs(duration));
    }

    /// <summary>
    /// 経過時間から、演出の進み具合（0〜1）を返す。
    /// </summary>
    public static float CalculateProgress(float elapsedTime, float duration)
    {
        return math.clamp(elapsedTime / NormalizeDuration(duration), 0f, 1f);
    }

    /// <summary>
    /// 進み具合に応じて、元の大きさから終了時の大きさへ線形補間した大きさを返す。
    /// </summary>
    public static float CalculateScale(float originalScale, float endScale, float progress)
    {
        var safeProgress = math.clamp(progress, 0f, 1f);
        var safeOriginalScale = math.max(0f, originalScale);
        var safeEndScale = math.max(0f, endScale);

        return math.lerp(safeOriginalScale, safeEndScale, safeProgress);
    }

    /// <summary>
    /// 進み具合に応じて、開始時の色から終了時の色へ線形補間した色を返す。
    /// </summary>
    public static float4 CalculateBaseColor(float4 startColor, float4 endColor, float progress)
    {
        return math.lerp(startColor, endColor, math.clamp(progress, 0f, 1f));
    }
}
