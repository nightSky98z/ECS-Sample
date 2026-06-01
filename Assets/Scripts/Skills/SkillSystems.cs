using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;

/// <summary>
/// 装備済み attack skill の cooltime を更新する。
/// Cooldown 中の slot だけを処理し、終了した slot を ready 状態へ戻す。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct SkillCooltimeSystem : ISystem
{
    private EntityQuery equippedBuffSkillQuery;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AttackSkillConfig>();
        state.RequireForUpdate<AttackSkillState>();

        equippedBuffSkillQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<BuffSkillConfig, SkillSlotComponent, EquippedSkillTag, BuffSkillSlotTag>()
            .Build(ref state);
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
        var buffSkills = equippedBuffSkillQuery.ToComponentDataArray<BuffSkillConfig>(Allocator.Temp);
        var buffSlots = equippedBuffSkillQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);

        foreach (var (config, skillState, slot) in
                 SystemAPI.Query<RefRO<AttackSkillConfig>, RefRW<AttackSkillState>, RefRO<SkillSlotComponent>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>())
        {
            if (skillState.ValueRO.IsCooltime == 0)
            {
                continue;
            }

            var buffs = SkillSystemUtility.CreateBuffAccumulatorForOwner(
                slot.ValueRO.Owner,
                buffSkills,
                buffSlots);
            var cooltime = SkillMath.CalculateEffectiveCooltime(config.ValueRO, buffs);
            var nextTimer = skillState.ValueRO.Timer + deltaTime;

            if (nextTimer < cooltime)
            {
                skillState.ValueRW.Timer = nextTimer;
                continue;
            }

            skillState.ValueRW.Timer = 0f;
            skillState.ValueRW.IsCooltime = 0;
        }

        buffSlots.Dispose();
        buffSkills.Dispose();
    }
}

/// <summary>
/// プレイヤーが持つ ready attack skill を 1 つ発動予約する。
/// 実際の target 決定や damage は SkillLogicSystem に任せ、ここでは IsTriggered だけを立てる。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillCooltimeSystem))]
public partial struct PlayerCombatSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
        state.RequireForUpdate<AttackSkillState>();
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

        var playerEntity = Entity.Null;

        foreach (var (_, entity) in
                 SystemAPI.Query<RefRO<PlayerTag>>()
                     .WithEntityAccess())
        {
            playerEntity = entity;
            break;
        }

        if (playerEntity == Entity.Null)
        {
            return;
        }

        var skillStateLookup = SystemAPI.GetComponentLookup<AttackSkillState>(false);
        var selectedSkillEntity = Entity.Null;
        var selectedSlotIndex = int.MaxValue;

        foreach (var (_, skillState, slot, skillEntity) in
                 SystemAPI.Query<RefRO<AttackSkillConfig>, RefRO<AttackSkillState>, RefRO<SkillSlotComponent>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>()
                     .WithEntityAccess())
        {
            if (slot.ValueRO.Owner != playerEntity ||
                skillState.ValueRO.IsCooltime != 0 ||
                skillState.ValueRO.IsTriggered != 0 ||
                skillState.ValueRO.IsCasting != 0 ||
                slot.ValueRO.SlotIndex >= selectedSlotIndex)
            {
                continue;
            }

            selectedSkillEntity = skillEntity;
            selectedSlotIndex = slot.ValueRO.SlotIndex;
        }

        if (selectedSkillEntity != Entity.Null)
        {
            var selectedSkill = skillStateLookup[selectedSkillEntity];

            selectedSkill.IsTriggered = 1;
            skillStateLookup[selectedSkillEntity] = selectedSkill;
        }
    }
}

/// <summary>
/// Casting 中の attack skill に対して、共通の発動経過時間を進める。
/// 経過時間更新を独立させることで、damage / SFX / VFX が同じ時刻を参照できる。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(PlayerCombatSystem))]
public partial struct SkillCastElapsedTimeSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AttackSkillState>();
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

        foreach (var skillState in
                 SystemAPI.Query<RefRW<AttackSkillState>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>())
        {
            skillState.ValueRW = SkillMath.AdvanceCastElapsedTime(
                skillState.ValueRO,
                deltaTime);
        }
    }
}

/// <summary>
/// 発動予約された attack skill の logic を実行する。
/// Triggered slot から SkillCastTarget を確定し、DamageDelay 到達後に monster へ直接 HP 変更を適用する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillCastElapsedTimeSystem))]
[UpdateBefore(typeof(SkillPresentationSystem))]
public partial struct SkillLogicSystem : ISystem
{
    private EntityQuery equippedBuffSkillQuery;
    private EntityQuery monsterQuery;
    private uint targetSelectionSequence;

#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
    [BurstCompile]
#endif
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AttackSkillConfig>();
        state.RequireForUpdate<AttackSkillAdvancedConfig>();
        state.RequireForUpdate<AttackSkillTimingConfig>();
        state.RequireForUpdate<AttackSkillState>();
        state.RequireForUpdate<SkillCastTarget>();

        equippedBuffSkillQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<BuffSkillConfig, SkillSlotComponent, EquippedSkillTag, BuffSkillSlotTag>()
            .Build(ref state);
        monsterQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<MonsterTag, LocalTransform, HealthComponent>()
            .Build(ref state);
    }

#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
    [BurstCompile]
#endif
    public void OnUpdate(ref SystemState state)
    {
        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (!StageRuntimeUtility.IsGameplayPhase(runtimeState.ValueRO.Phase))
            {
                return;
            }
        }

        var localTransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true);
        var facingLookup = SystemAPI.GetComponentLookup<FacingDirection>(true);
        var healthLookup = SystemAPI.GetComponentLookup<HealthComponent>(false);
        var hitVfxLookup = SystemAPI.GetComponentLookup<MonsterHitVfxState>(false);
        var debuffRuntimeLookup = SystemAPI.GetComponentLookup<DebuffRuntimeState>(false);
        var experienceLookup = SystemAPI.GetComponentLookup<ExperienceComponent>(true);
        var levelStatsLookup = SystemAPI.GetComponentLookup<PlayerLevelStats>(true);
        var skillDebuffLookup = SystemAPI.GetBufferLookup<SkillDebuffSpec>(true);
        var buffSkills = equippedBuffSkillQuery.ToComponentDataArray<BuffSkillConfig>(Allocator.Temp);
        var buffSlots = equippedBuffSkillQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var monsterEntities = monsterQuery.ToEntityArray(Allocator.Temp);
        var monsterTransforms = monsterQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        foreach (var (config, advancedConfig, timing, skillState, slot, castTarget, skillEntity) in
                 SystemAPI.Query<RefRO<AttackSkillConfig>, RefRO<AttackSkillAdvancedConfig>, RefRO<AttackSkillTimingConfig>, RefRW<AttackSkillState>, RefRO<SkillSlotComponent>, RefRW<SkillCastTarget>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>()
                     .WithEntityAccess())
        {
            var skillStateValue = skillState.ValueRO;
            var castTargetValue = castTarget.ValueRO;

            if (skillStateValue.IsTriggered != 0)
            {
                TryStartSkillCast(
                    config.ValueRO,
                    advancedConfig.ValueRO,
                    ref skillStateValue,
                    ref castTargetValue,
                    slot.ValueRO.Owner,
                    localTransformLookup,
                    facingLookup,
                    experienceLookup,
                    levelStatsLookup,
                    buffSkills,
                    buffSlots,
                    monsterEntities,
                    monsterTransforms,
                    healthLookup);
            }

            if (skillStateValue.IsCasting == 0)
            {
                skillState.ValueRW = skillStateValue;
                castTarget.ValueRW = castTargetValue;
                continue;
            }

            var damageApplyTotalCount = math.max(1, castTargetValue.RepeatCount);

            if (SkillMath.ShouldApplySkillDamage(
                    skillStateValue,
                    timing.ValueRO,
                    castTargetValue))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                for (var targetIndex = 0; targetIndex < castTargetValue.Positions.Length; targetIndex++)
                {
                    SkillAttackRangeDebugEvents.Record(
                        castTargetValue.Positions[targetIndex],
                        SkillCastTargetUtility.GetAttackRange(castTargetValue, targetIndex));
                }
#endif

                var random = new Unity.Mathematics.Random(
                    SkillSystemUtility.CreateTargetSelectionSeed(
                        config.ValueRO.Id,
                        castTargetValue.Positions.Length > 0 ? castTargetValue.Positions[0] : float3.zero,
                        targetSelectionSequence++));
                var hasDebuffBuffer = skillDebuffLookup.HasBuffer(skillEntity);

                if (hasDebuffBuffer)
                {
                    SkillSystemUtility.ApplyTargetCenteredCircleAttack(
                        castTargetValue,
                        monsterEntities,
                        monsterTransforms,
                        healthLookup,
                        hitVfxLookup,
                        debuffRuntimeLookup,
                        skillDebuffLookup[skillEntity],
                        ref random);
                }
                else
                {
                    SkillSystemUtility.ApplyTargetCenteredCircleAttack(
                        castTargetValue,
                        monsterEntities,
                        monsterTransforms,
                        healthLookup,
                        hitVfxLookup);
                }

                skillStateValue.DamageApplyCount++;
                skillStateValue.DamageApplied = skillStateValue.DamageApplyCount >= damageApplyTotalCount
                    ? (byte)1
                    : (byte)0;
            }

            if (skillStateValue.StartedThisFrame != 0)
            {
                skillStateValue.StartedThisFrame = 0;
                skillState.ValueRW = skillStateValue;
                castTarget.ValueRW = castTargetValue;
                continue;
            }

            skillState.ValueRW = skillStateValue;
            castTarget.ValueRW = castTargetValue;
        }

        monsterTransforms.Dispose();
        monsterEntities.Dispose();
        buffSlots.Dispose();
        buffSkills.Dispose();
    }

    private void TryStartSkillCast(
        AttackSkillConfig config,
        AttackSkillAdvancedConfig advancedConfig,
        ref AttackSkillState skillState,
        ref SkillCastTarget castTarget,
        Entity owner,
        ComponentLookup<LocalTransform> localTransformLookup,
        ComponentLookup<FacingDirection> facingLookup,
        ComponentLookup<ExperienceComponent> experienceLookup,
        ComponentLookup<PlayerLevelStats> levelStatsLookup,
        NativeArray<BuffSkillConfig> buffSkills,
        NativeArray<SkillSlotComponent> buffSlots,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup)
    {
        // Trigger は単発イベントとして扱い、ここで消費する。
        skillState.IsTriggered = 0;

        if (!localTransformLookup.HasComponent(owner))
        {
            skillState.IsCasting = 0;
            return;
        }

        var ownerPosition = localTransformLookup[owner].Position;
        var ownerFacing = SkillSystemUtility.GetOwnerFacing(owner, facingLookup);
        var buffs = SkillSystemUtility.CreateBuffAccumulatorForOwner(
            owner,
            buffSkills,
            buffSlots);
        var playerLevelDamageRate = SkillSystemUtility.CalculatePlayerLevelDamageRate(
            owner,
            experienceLookup,
            levelStatsLookup);

        switch (config.LogicId)
        {
            case 0:
            case 1:
            case 2:
            case 3:
            case 4:
            case 5:
            case 6:
            case 7:
                var random = new Unity.Mathematics.Random(
                    SkillSystemUtility.CreateTargetSelectionSeed(
                        config.Id,
                        ownerPosition,
                        targetSelectionSequence++));

                if (!SkillSystemUtility.TryPrepareTargetCenteredCircleAttack(
                        config,
                        advancedConfig,
                        skillState,
                        ownerPosition,
                        ownerFacing,
                        buffs,
                        playerLevelDamageRate,
                        ref random,
                        monsterEntities,
                        monsterTransforms,
                        healthLookup,
                        out castTarget))
                {
                    skillState.IsCasting = 0;
                    return;
                }

                SkillSystemUtility.StartCasting(ref skillState);
                skillState.DamageApplyTotalCount = math.max(1, castTarget.RepeatCount);
                return;
            default:
                skillState.IsCasting = 0;
                return;
        }
    }
}

/// <summary>
/// ダメージと演出が終わった attack skill を cooltime 状態へ遷移させる。
/// Managed presentation は別 system が担当するため、この system は完了フラグだけを見る。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillPresentationSystem))]
public partial struct SkillCastCompletionSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AttackSkillState>();
        state.RequireForUpdate<AttackSkillTimingConfig>();
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

        var presentationLookup = SystemAPI.GetComponentLookup<AttackSkillPresentation>(true);

        foreach (var (skillState, timing, skillEntity) in
                 SystemAPI.Query<RefRW<AttackSkillState>, RefRO<AttackSkillTimingConfig>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>()
                     .WithEntityAccess())
        {
            if (skillState.ValueRO.IsCasting == 0 ||
                skillState.ValueRO.DamageApplied == 0 ||
                (presentationLookup.HasComponent(skillEntity) &&
                 !SkillMath.IsPresentationComplete(skillState.ValueRO, timing.ValueRO)))
            {
                continue;
            }

            var skillStateValue = skillState.ValueRO;

            SkillSystemUtility.StartCooltime(ref skillStateValue);
            skillState.ValueRW = skillStateValue;
        }
    }
}

/// <summary>
/// Monster の active debuff を更新し、他 system が読む集約値へ変換する。
/// ActiveDebuff 配列を毎フレーム畳み込み、Movement / AI が直接読める倍率 component を作る。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillLogicSystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
[UpdateBefore(typeof(MonsterPathFollowSystem))]
public partial struct DebuffSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<DebuffRuntimeState>();
        state.RequireForUpdate<DebuffAggregate>();
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
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var freezeLookup = SystemAPI.GetComponentLookup<FreezeTag>(true);
        var freezeComponentLookup = SystemAPI.GetComponentLookup<FreezeComponent>(true);
        var velocityLookup = SystemAPI.GetComponentLookup<Velocity>(true);

        foreach (var (runtime, aggregate, health, entity) in
                 SystemAPI.Query<RefRW<DebuffRuntimeState>, RefRW<DebuffAggregate>, RefRW<HealthComponent>>()
                     .WithEntityAccess())
        {
            var activeDebuffs = runtime.ValueRO.ActiveDebuffs;
            var nextDebuffs = default(DebuffRuntimeState);
            var nextAggregate = DebuffMath.CreateNeutralAggregate();
            var currentHealth = health.ValueRO;
            var nextFreezeDuration = 0f;

            for (var debuffIndex = 0; debuffIndex < activeDebuffs.Length; debuffIndex++)
            {
                var debuff = activeDebuffs[debuffIndex];

                debuff.RemainingTime -= deltaTime;

                if (debuff.RemainingTime <= 0f)
                {
                    continue;
                }

                if (debuff.Kind == DebuffKind.Freeze)
                {
                    nextFreezeDuration = math.max(nextFreezeDuration, debuff.RemainingTime);
                    continue;
                }

                if (debuff.Kind == DebuffKind.DamageOverTime)
                {
                    var damage = DebuffMath.CalculateDotDamage(
                        debuff,
                        deltaTime,
                        out var nextTickTimer);

                    debuff.TickTimer = nextTickTimer;

                    if (damage > 0)
                    {
                        currentHealth = HealthMath.ApplyHealthDelta(currentHealth, -damage);
                    }
                }

                nextAggregate = DebuffMath.ApplyToAggregate(nextAggregate, debuff);

                if (nextDebuffs.ActiveDebuffs.Length < DebuffConstants.MaxActiveDebuffCount)
                {
                    nextDebuffs.ActiveDebuffs.Add(debuff);
                }
            }

            runtime.ValueRW = nextDebuffs;
            aggregate.ValueRW = nextAggregate;
            health.ValueRW = currentHealth;

            if (nextFreezeDuration > 0f)
            {
                AddOrRefreshFreeze(
                    ref entityCommandBuffer,
                    freezeLookup,
                    freezeComponentLookup,
                    velocityLookup,
                    entity,
                    nextFreezeDuration);
            }
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

    private static void AddOrRefreshFreeze(
        ref EntityCommandBuffer entityCommandBuffer,
        ComponentLookup<FreezeTag> freezeLookup,
        ComponentLookup<FreezeComponent> freezeComponentLookup,
        ComponentLookup<Velocity> velocityLookup,
        Entity entity,
        float duration)
    {
        var nextFreezeDuration = math.max(0f, duration);

        if (nextFreezeDuration <= 0f)
        {
            return;
        }

        if (!freezeLookup.HasComponent(entity))
        {
            entityCommandBuffer.AddComponent<FreezeTag>(entity);
        }

        if (freezeComponentLookup.HasComponent(entity))
        {
            var currentFreeze = freezeComponentLookup[entity];
            var currentRemainingTime = math.max(
                0f,
                currentFreeze.ShouldFreezeTime - currentFreeze.Timer);

            currentFreeze.ShouldFreezeTime = math.max(currentRemainingTime, nextFreezeDuration);
            currentFreeze.Timer = 0f;
            entityCommandBuffer.SetComponent(entity, currentFreeze);
            return;
        }

        var restoreVelocity = velocityLookup.HasComponent(entity)
            ? velocityLookup[entity]
            : new Velocity
            {
                Value = float3.zero
            };

        entityCommandBuffer.AddComponent(entity, new FreezeComponent
        {
            ShouldFreezeTime = nextFreezeDuration,
            Timer = 0f,
            RestoreVelocity = restoreVelocity
        });
    }
}

/// <summary>
/// Paralyze の回復カーブを移動速度倍率へ反映する。
/// Freeze と違い Velocity は外さず、DebuffAggregate へ soft lock として掛け合わせる。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DebuffSystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
[UpdateBefore(typeof(MonsterPathFollowSystem))]
public partial struct ParalyzeSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<DebuffRuntimeState>();
        state.RequireForUpdate<DebuffAggregate>();
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

        foreach (var (runtime, aggregate) in
                 SystemAPI.Query<RefRO<DebuffRuntimeState>, RefRW<DebuffAggregate>>())
        {
            var paralyzeMoveSpeedMultiplier = DebuffMath.CalculateParalyzeMoveSpeedMultiplier(runtime.ValueRO);

            if (paralyzeMoveSpeedMultiplier >= 1f)
            {
                continue;
            }

            aggregate.ValueRW.MoveSpeedMultiplier *= paralyzeMoveSpeedMultiplier;
        }
    }
}

/// <summary>
/// FreezeTag が付いた Entity を移動処理から外す。
/// Velocity component を削除することで、Movement / AI / PathFollow の query に入らない状態にする。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DebuffSystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
[UpdateBefore(typeof(MonsterPathFollowSystem))]
public partial struct FreezeSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<FreezeTag>();
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

        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (_, entity) in
                 SystemAPI.Query<RefRO<FreezeComponent>>()
                     .WithAll<FreezeTag, Velocity>()
                     .WithNone<MonsterDestroyVfxState>()
                     .WithEntityAccess())
        {
            entityCommandBuffer.RemoveComponent<Velocity>(entity);
            // HINT: 氷結専用 VFX / SFX component を追加したら、ここで null チェックして再生開始する。
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}

/// <summary>
/// 氷結時間が終わった Entity を通常移動へ戻す。
/// FreezeComponent の timer を進め、期限切れで FreezeTag / FreezeComponent を削除して Velocity を復元する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(FreezeSystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
[UpdateBefore(typeof(MonsterPathFollowSystem))]
public partial struct FreezeClearSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<FreezeTag>();
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
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);
        var velocityLookup = SystemAPI.GetComponentLookup<Velocity>(true);

        foreach (var (freezeComponent, entity) in
                 SystemAPI.Query<RefRW<FreezeComponent>>()
                     .WithAll<FreezeTag>()
                     .WithNone<MonsterDestroyVfxState>()
                     .WithEntityAccess())
        {
            var nextTimer = freezeComponent.ValueRO.Timer + math.max(0f, deltaTime);

            if (nextTimer < math.max(0f, freezeComponent.ValueRO.ShouldFreezeTime))
            {
                freezeComponent.ValueRW.Timer = nextTimer;
                continue;
            }

            if (!velocityLookup.HasComponent(entity))
            {
                entityCommandBuffer.AddComponent(entity, freezeComponent.ValueRO.RestoreVelocity);
            }

            entityCommandBuffer.RemoveComponent<FreezeTag>(entity);
            entityCommandBuffer.RemoveComponent<FreezeComponent>(entity);
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}

/// <summary>
/// Skill system が使う配列ベースの実行 helper。
/// Entity 構造変更を行わず、受け取った NativeArray / ComponentLookup に対して明示的に読み書きする。
/// </summary>
public static class SkillSystemUtility
{
    private const float MinDuplicateTargetDistanceSq = 0.0001f;

    public static BuffAccumulator CreateBuffAccumulatorForOwner(
        Entity owner,
        NativeArray<BuffSkillConfig> buffSkills,
        NativeArray<SkillSlotComponent> buffSlots)
    {
        var accumulator = SkillMath.CreateBuffAccumulator();

        for (var buffIndex = 0; buffIndex < buffSkills.Length; buffIndex++)
        {
            if (buffSlots[buffIndex].Owner != owner)
            {
                continue;
            }

            accumulator = SkillMath.ApplyBuff(accumulator, buffSkills[buffIndex]);
        }

        return accumulator;
    }

    public static float2 GetOwnerFacing(
        Entity owner,
        ComponentLookup<FacingDirection> facingLookup)
    {
        if (!facingLookup.HasComponent(owner))
        {
            return new float2(0f, 1f);
        }

        var facing = facingLookup[owner].Value;
        var facingLengthSq = math.lengthsq(facing);

        if (facingLengthSq <= 0.000001f)
        {
            return new float2(0f, 1f);
        }

        return facing * math.rsqrt(facingLengthSq);
    }

    public static float2 NormalizeDirection(float2 direction)
    {
        var lengthSq = math.lengthsq(direction);

        if (lengthSq <= 0.000001f)
        {
            return new float2(0f, 1f);
        }

        return direction * math.rsqrt(lengthSq);
    }

    public static bool IsPointInForwardSector(
        float3 point,
        float3 origin,
        float2 direction,
        float length,
        float angleCos)
    {
        var safeLength = math.max(0f, length);
        var delta = point.xz - origin.xz;
        var distanceSq = math.lengthsq(delta);

        if (distanceSq > safeLength * safeLength)
        {
            return false;
        }

        if (distanceSq <= 0.000001f)
        {
            return true;
        }

        var normalizedDelta = delta * math.rsqrt(distanceSq);
        var normalizedDirection = NormalizeDirection(direction);

        return math.dot(normalizedDelta, normalizedDirection) >= math.clamp(angleCos, -1f, 1f);
    }

    public static bool IsPointInPiercingLine(
        float3 point,
        float3 origin,
        float2 direction,
        float length,
        float width)
    {
        var normalizedDirection = NormalizeDirection(direction);
        var delta = point.xz - origin.xz;
        var forwardDistance = math.dot(delta, normalizedDirection);

        if (forwardDistance < 0f || forwardDistance > math.max(0f, length))
        {
            return false;
        }

        var halfWidth = math.max(0f, width) * 0.5f;
        var closestPointDelta = delta - normalizedDirection * forwardDistance;

        return math.lengthsq(closestPointDelta) <= halfWidth * halfWidth;
    }

    public static void StartCasting(ref AttackSkillState skillState)
    {
        skillState.Timer = 0f;
        skillState.CastElapsedTime = 0f;
        skillState.DamageApplyCount = 0;
        skillState.IsTriggered = 0;
        skillState.IsCasting = 1;
        skillState.IsCooltime = 0;
        skillState.DamageApplied = 0;
        skillState.SfxPlayed = 0;
        skillState.VfxSpawned = 0;
        skillState.StartedThisFrame = 1;
    }

    public static void StartCooltime(ref AttackSkillState skillState)
    {
        skillState.Timer = 0f;
        skillState.CastElapsedTime = 0f;
        skillState.DamageApplyCount = 0;
        skillState.DamageApplyTotalCount = 0;
        skillState.IsTriggered = 0;
        skillState.IsCasting = 0;
        skillState.IsCooltime = 1;
        skillState.DamageApplied = 0;
        skillState.SfxPlayed = 0;
        skillState.VfxSpawned = 0;
        skillState.StartedThisFrame = 0;
    }

    public static bool TryPrepareTargetCenteredCircleAttack(
        AttackSkillConfig config,
        AttackSkillAdvancedConfig advancedConfig,
        AttackSkillState skillState,
        float3 ownerPosition,
        float2 ownerFacing,
        BuffAccumulator buffs,
        float playerLevelDamageRate,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        out SkillCastTarget castTarget)
    {
        var targetRange = SkillMath.CalculateEffectiveTargetRange(config, buffs);
        var targetCount = SkillMath.CalculateAttackCircleCount(
            skillState.Level,
            config.BaseTargetCount,
            config.TargetCountLevelWeight,
            config.MaxTargetCount,
            config.TargetCountRoundMode);
        var attackRange = SkillMath.CalculateEffectiveAttackRange(config, buffs);
        var damage = SkillMath.CalculateDamageToHp(
            SkillMath.CalculateEffectiveDamage(config, skillState, buffs, playerLevelDamageRate));

        castTarget = new SkillCastTarget
        {
            AttackRange = attackRange,
            Damage = damage,
            RepeatCount = 1,
            RepeatInterval = 0f
        };

        switch (config.LogicId)
        {
            case 1:
                return PrepareForwardSectorAttack(
                    advancedConfig,
                    ownerPosition,
                    ownerFacing,
                    targetRange,
                    attackRange,
                    damage,
                    ref castTarget);
            case 2:
                return PreparePiercingLineAttack(
                    advancedConfig,
                    ownerPosition,
                    ownerFacing,
                    targetRange,
                    attackRange,
                    damage,
                    ref castTarget);
            case 3:
                return PrepareSelfCenteredAreaAttack(
                    ownerPosition,
                    attackRange,
                    damage,
                    ref castTarget);
            case 4:
                return PrepareSpreadCircleAttack(
                    config,
                    advancedConfig,
                    skillState,
                    ownerPosition,
                    targetRange,
                    attackRange,
                    damage,
                    ref random,
                    monsterEntities,
                    monsterTransforms,
                    healthLookup,
                    ref castTarget);
            case 5:
                return PrepareChainCircleAttack(
                    config,
                    advancedConfig,
                    skillState,
                    ownerPosition,
                    targetRange,
                    attackRange,
                    damage,
                    ref random,
                    monsterEntities,
                    monsterTransforms,
                    healthLookup,
                    ref castTarget);
            case 6:
                return PrepareRandomGroundCircleAttack(
                    config,
                    advancedConfig,
                    skillState,
                    ownerPosition,
                    targetRange,
                    attackRange,
                    damage,
                    ref random,
                    ref castTarget);
            case 7:
                if (!PrepareTargetCenteredCircleAttack(
                        ownerPosition,
                        targetRange,
                        targetCount,
                        attackRange,
                        damage,
                        ref random,
                        monsterEntities,
                        monsterTransforms,
                        healthLookup,
                        ref castTarget))
                {
                    return false;
                }

                castTarget.RepeatCount = SkillMath.CalculateAttackCircleCount(
                    skillState.Level,
                    advancedConfig.RepeatBaseCount,
                    advancedConfig.RepeatCountLevelWeight,
                    advancedConfig.RepeatMaxCount,
                    config.TargetCountRoundMode);
                castTarget.RepeatInterval = math.max(0f, advancedConfig.RepeatInterval);
                return true;
            case 0:
            default:
                return PrepareTargetCenteredCircleAttack(
                    ownerPosition,
                    targetRange,
                    targetCount,
                    attackRange,
                    damage,
                    ref random,
                    monsterEntities,
                    monsterTransforms,
                    healthLookup,
                    ref castTarget);
        }
    }

    public static int ApplyTargetCenteredCircleAttack(
        SkillCastTarget castTarget,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup)
    {
        var random = new Unity.Mathematics.Random(1u);

        return ApplyTargetCenteredCircleAttack(
            castTarget,
            monsterEntities,
            monsterTransforms,
            healthLookup,
            hitVfxLookup,
            default,
            default,
            ref random,
            hasDebuffs: 0);
    }

    public static int ApplyTargetCenteredCircleAttack(
        SkillCastTarget castTarget,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup,
        ComponentLookup<DebuffRuntimeState> debuffRuntimeLookup,
        DynamicBuffer<SkillDebuffSpec> debuffSpecs,
        ref Unity.Mathematics.Random random)
    {
        return ApplyTargetCenteredCircleAttack(
            castTarget,
            monsterEntities,
            monsterTransforms,
            healthLookup,
            hitVfxLookup,
            debuffRuntimeLookup,
            debuffSpecs,
            ref random,
            hasDebuffs: 1);
    }

    private static int ApplyTargetCenteredCircleAttack(
        SkillCastTarget castTarget,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup,
        ComponentLookup<DebuffRuntimeState> debuffRuntimeLookup,
        DynamicBuffer<SkillDebuffSpec> debuffSpecs,
        ref Unity.Mathematics.Random random,
        byte hasDebuffs)
    {
        if (castTarget.Shape == AttackSkillCastShape.ForwardSector)
        {
            return ApplyForwardSectorAttack(
                castTarget,
                monsterEntities,
                monsterTransforms,
                healthLookup,
                hitVfxLookup,
                debuffRuntimeLookup,
                debuffSpecs,
                ref random,
                hasDebuffs);
        }

        if (castTarget.Shape == AttackSkillCastShape.PiercingLine)
        {
            return ApplyPiercingLineAttack(
                castTarget,
                monsterEntities,
                monsterTransforms,
                healthLookup,
                hitVfxLookup,
                debuffRuntimeLookup,
                debuffSpecs,
                ref random,
                hasDebuffs);
        }

        var hitCount = 0;

        for (var targetIndex = 0; targetIndex < castTarget.Positions.Length; targetIndex++)
        {
            var targetPosition = castTarget.Positions[targetIndex];
            var attackRange = SkillCastTargetUtility.GetAttackRange(castTarget, targetIndex);
            var attackRangeSq = attackRange * attackRange;
            var damage = SkillCastTargetUtility.GetDamage(castTarget, targetIndex);

            for (var monsterIndex = 0; monsterIndex < monsterTransforms.Length; monsterIndex++)
            {
                var monsterEntity = monsterEntities[monsterIndex];

                if (!healthLookup.HasComponent(monsterEntity))
                {
                    continue;
                }

                var currentHealth = healthLookup[monsterEntity];

                if (HealthMath.IsDead(currentHealth))
                {
                    continue;
                }

                var delta = monsterTransforms[monsterIndex].Position.xz - targetPosition.xz;

                if (math.lengthsq(delta) > attackRangeSq)
                {
                    continue;
                }

                healthLookup[monsterEntity] = HealthMath.ApplyHealthDelta(
                    currentHealth,
                    -math.max(0, damage));
                ApplyDebuffsToMonster(
                    monsterEntity,
                    debuffRuntimeLookup,
                    debuffSpecs,
                    hasDebuffs,
                    ref random);
                PlayMonsterHitVfx(monsterEntity, hitVfxLookup);
                hitCount++;
            }
        }

        return hitCount;
    }

    private static int ApplyForwardSectorAttack(
        SkillCastTarget castTarget,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup,
        ComponentLookup<DebuffRuntimeState> debuffRuntimeLookup,
        DynamicBuffer<SkillDebuffSpec> debuffSpecs,
        ref Unity.Mathematics.Random random,
        byte hasDebuffs)
    {
        var hitCount = 0;
        var origin = castTarget.Origin;
        var direction = SkillSystemUtility.NormalizeDirection(castTarget.Direction);
        var length = math.max(0f, castTarget.ShapeLength);
        var angleCos = math.clamp(castTarget.ShapeAngleCos, -1f, 1f);

        for (var monsterIndex = 0; monsterIndex < monsterTransforms.Length; monsterIndex++)
        {
            var monsterEntity = monsterEntities[monsterIndex];

            if (!healthLookup.HasComponent(monsterEntity))
            {
                continue;
            }

            var currentHealth = healthLookup[monsterEntity];

            if (HealthMath.IsDead(currentHealth))
            {
                continue;
            }

            if (!IsPointInForwardSector(
                    monsterTransforms[monsterIndex].Position,
                    origin,
                    direction,
                    length,
                    angleCos))
            {
                continue;
            }

            healthLookup[monsterEntity] = HealthMath.ApplyHealthDelta(
                currentHealth,
                -math.max(0, castTarget.Damage));
            ApplyDebuffsToMonster(
                monsterEntity,
                debuffRuntimeLookup,
                debuffSpecs,
                hasDebuffs,
                ref random);
            PlayMonsterHitVfx(monsterEntity, hitVfxLookup);
            hitCount++;
        }

        return hitCount;
    }

    private static int ApplyPiercingLineAttack(
        SkillCastTarget castTarget,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup,
        ComponentLookup<DebuffRuntimeState> debuffRuntimeLookup,
        DynamicBuffer<SkillDebuffSpec> debuffSpecs,
        ref Unity.Mathematics.Random random,
        byte hasDebuffs)
    {
        var hitCount = 0;
        var origin = castTarget.Origin;
        var direction = SkillSystemUtility.NormalizeDirection(castTarget.Direction);
        var length = math.max(0f, castTarget.ShapeLength);

        for (var monsterIndex = 0; monsterIndex < monsterTransforms.Length; monsterIndex++)
        {
            var monsterEntity = monsterEntities[monsterIndex];

            if (!healthLookup.HasComponent(monsterEntity))
            {
                continue;
            }

            var currentHealth = healthLookup[monsterEntity];

            if (HealthMath.IsDead(currentHealth))
            {
                continue;
            }

            if (!IsPointInPiercingLine(
                    monsterTransforms[monsterIndex].Position,
                    origin,
                    direction,
                    length,
                    castTarget.ShapeWidth))
            {
                continue;
            }

            healthLookup[monsterEntity] = HealthMath.ApplyHealthDelta(
                currentHealth,
                -math.max(0, castTarget.Damage));
            ApplyDebuffsToMonster(
                monsterEntity,
                debuffRuntimeLookup,
                debuffSpecs,
                hasDebuffs,
                ref random);
            PlayMonsterHitVfx(monsterEntity, hitVfxLookup);
            hitCount++;
        }

        return hitCount;
    }

    private static void ApplyDebuffsToMonster(
        Entity monsterEntity,
        ComponentLookup<DebuffRuntimeState> debuffRuntimeLookup,
        DynamicBuffer<SkillDebuffSpec> debuffSpecs,
        byte hasDebuffs,
        ref Unity.Mathematics.Random random)
    {
        if (hasDebuffs == 0 ||
            !debuffRuntimeLookup.HasComponent(monsterEntity))
        {
            return;
        }

        var runtime = debuffRuntimeLookup[monsterEntity];

        for (var debuffIndex = 0; debuffIndex < debuffSpecs.Length; debuffIndex++)
        {
            var spec = DebuffMath.NormalizeSpec(debuffSpecs[debuffIndex]);

            if (!DebuffMath.ShouldApply(spec.Chance, ref random))
            {
                continue;
            }

            ApplyDebuffSpec(ref runtime, spec);
        }

        debuffRuntimeLookup[monsterEntity] = runtime;
    }

    private static void ApplyDebuffSpec(
        ref DebuffRuntimeState runtime,
        SkillDebuffSpec spec)
    {
        var activeDebuff = DebuffMath.CreateActiveDebuff(spec);

        if (activeDebuff.RemainingTime <= 0f)
        {
            return;
        }

        if (spec.StackPolicy != DebuffStackPolicy.StackIndependent)
        {
            for (var debuffIndex = 0; debuffIndex < runtime.ActiveDebuffs.Length; debuffIndex++)
            {
                var currentDebuff = runtime.ActiveDebuffs[debuffIndex];

                if (currentDebuff.DebuffId != activeDebuff.DebuffId)
                {
                    continue;
                }

                if (spec.StackPolicy == DebuffStackPolicy.ReplaceIfStronger &&
                    !DebuffMath.IsReplacementStronger(currentDebuff, activeDebuff))
                {
                    currentDebuff.RemainingTime = math.max(currentDebuff.RemainingTime, activeDebuff.RemainingTime);
                    runtime.ActiveDebuffs[debuffIndex] = currentDebuff;
                    return;
                }

                runtime.ActiveDebuffs[debuffIndex] = activeDebuff;
                return;
            }
        }

        if (runtime.ActiveDebuffs.Length >= DebuffConstants.MaxActiveDebuffCount)
        {
            return;
        }

        runtime.ActiveDebuffs.Add(activeDebuff);
    }

    private static bool PrepareTargetCenteredCircleAttack(
        float3 ownerPosition,
        float targetRange,
        int targetCount,
        float attackRange,
        int damage,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ref SkillCastTarget castTarget)
    {
        if (!TryFindDensityBiasedRandomMonsterPositions(
                ownerPosition,
                targetRange,
                PlayerCombatConstants.TargetSelectionDirectionBucketCount,
                PlayerCombatConstants.TargetSelectionDistanceWeight,
                targetCount,
                ref random,
                monsterEntities,
                monsterTransforms,
                healthLookup,
                out var targetPositions))
        {
            return false;
        }

        for (var targetIndex = 0; targetIndex < targetPositions.Length; targetIndex++)
        {
            if (!SkillCastTargetUtility.AddCircle(
                    ref castTarget,
                    targetPositions[targetIndex],
                    attackRange,
                    damage))
            {
                break;
            }
        }

        return castTarget.Positions.Length > 0;
    }

    private static bool PrepareForwardSectorAttack(
        AttackSkillAdvancedConfig advancedConfig,
        float3 ownerPosition,
        float2 ownerFacing,
        float targetRange,
        float attackRange,
        int damage,
        ref SkillCastTarget castTarget)
    {
        var direction = NormalizeDirection(ownerFacing);
        var length = math.max(0f, targetRange);
        var angleDegrees = math.clamp(advancedConfig.ForwardSectorAngleDegrees, 1f, 360f);
        var halfAngleRadians = math.radians(angleDegrees * 0.5f);

        castTarget.Shape = AttackSkillCastShape.ForwardSector;
        castTarget.Origin = ownerPosition;
        castTarget.Direction = direction;
        castTarget.ShapeLength = length;
        castTarget.ShapeWidth = attackRange;
        castTarget.ShapeAngleCos = math.cos(halfAngleRadians);
        castTarget.AttackRange = length;
        castTarget.Damage = damage;

        return SkillCastTargetUtility.AddCircle(
            ref castTarget,
            ownerPosition + new float3(direction.x, 0f, direction.y) * (length * 0.5f),
            length * 0.5f,
            damage);
    }

    private static bool PreparePiercingLineAttack(
        AttackSkillAdvancedConfig advancedConfig,
        float3 ownerPosition,
        float2 ownerFacing,
        float targetRange,
        float attackRange,
        int damage,
        ref SkillCastTarget castTarget)
    {
        var direction = NormalizeDirection(ownerFacing);
        var length = math.max(0f, targetRange);
        var lineWidth = advancedConfig.LineWidth > 0f
            ? advancedConfig.LineWidth
            : math.max(0f, attackRange);

        castTarget.Shape = AttackSkillCastShape.PiercingLine;
        castTarget.Origin = ownerPosition;
        castTarget.Direction = direction;
        castTarget.ShapeLength = length;
        castTarget.ShapeWidth = lineWidth;
        castTarget.ShapeAngleCos = 1f;
        castTarget.AttackRange = lineWidth;
        castTarget.Damage = damage;

        return SkillCastTargetUtility.AddCircle(
            ref castTarget,
            ownerPosition + new float3(direction.x, 0f, direction.y) * (length * 0.5f),
            math.max(lineWidth * 0.5f, 0.01f),
            damage);
    }

    private static bool PrepareSelfCenteredAreaAttack(
        float3 ownerPosition,
        float attackRange,
        int damage,
        ref SkillCastTarget castTarget)
    {
        castTarget.Shape = AttackSkillCastShape.CircleList;
        castTarget.Origin = ownerPosition;
        castTarget.Direction = new float2(0f, 1f);
        castTarget.ShapeLength = attackRange;
        castTarget.ShapeWidth = attackRange;
        castTarget.ShapeAngleCos = 1f;

        return SkillCastTargetUtility.AddCircle(
            ref castTarget,
            ownerPosition,
            attackRange,
            damage);
    }

    private static bool PrepareSpreadCircleAttack(
        AttackSkillConfig config,
        AttackSkillAdvancedConfig advancedConfig,
        AttackSkillState skillState,
        float3 ownerPosition,
        float targetRange,
        float attackRange,
        int damage,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ref SkillCastTarget castTarget)
    {
        var primaryCount = SkillMath.CalculateAttackCircleCount(
            skillState.Level,
            config.BaseTargetCount,
            config.TargetCountLevelWeight,
            config.MaxTargetCount,
            config.TargetCountRoundMode);

        if (!TryFindDensityBiasedRandomMonsterPositions(
                ownerPosition,
                targetRange,
                PlayerCombatConstants.TargetSelectionDirectionBucketCount,
                PlayerCombatConstants.TargetSelectionDistanceWeight,
                primaryCount,
                ref random,
                monsterEntities,
                monsterTransforms,
                healthLookup,
                out var primaryPositions))
        {
            return false;
        }

        for (var targetIndex = 0; targetIndex < primaryPositions.Length; targetIndex++)
        {
            SkillCastTargetUtility.AddCircle(
                ref castTarget,
                primaryPositions[targetIndex],
                attackRange,
                damage);
        }

        var secondaryCount = SkillMath.CalculateAttackCircleCount(
            skillState.Level,
            advancedConfig.SecondaryBaseCount,
            advancedConfig.SecondaryCountLevelWeight,
            advancedConfig.SecondaryMaxCount,
            config.TargetCountRoundMode);
        var secondaryRadius = math.max(0f, advancedConfig.SecondaryTargetRange);
        var secondaryAttackRange = attackRange * math.max(0f, advancedConfig.SecondaryAttackRangeMultiplier);

        for (var spreadIndex = 0; spreadIndex < secondaryCount; spreadIndex++)
        {
            var sourcePosition = primaryPositions[spreadIndex % primaryPositions.Length];
            var spreadOffset = CreateRandomCircleOffset(secondaryRadius, ref random);

            if (!SkillCastTargetUtility.AddCircle(
                    ref castTarget,
                    sourcePosition + spreadOffset,
                    secondaryAttackRange,
                    damage))
            {
                break;
            }
        }

        return castTarget.Positions.Length > 0;
    }

    private static bool PrepareChainCircleAttack(
        AttackSkillConfig config,
        AttackSkillAdvancedConfig advancedConfig,
        AttackSkillState skillState,
        float3 ownerPosition,
        float targetRange,
        float attackRange,
        int damage,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ref SkillCastTarget castTarget)
    {
        var primaryCount = SkillMath.CalculateAttackCircleCount(
            skillState.Level,
            config.BaseTargetCount,
            config.TargetCountLevelWeight,
            config.MaxTargetCount,
            config.TargetCountRoundMode);

        if (!TryFindDensityBiasedRandomMonsterPositions(
                ownerPosition,
                targetRange,
                PlayerCombatConstants.TargetSelectionDirectionBucketCount,
                PlayerCombatConstants.TargetSelectionDistanceWeight,
                primaryCount,
                ref random,
                monsterEntities,
                monsterTransforms,
                healthLookup,
                out var primaryPositions))
        {
            return false;
        }

        var excludedPositions = default(FixedList512Bytes<float3>);

        for (var targetIndex = 0; targetIndex < primaryPositions.Length; targetIndex++)
        {
            var primaryPosition = primaryPositions[targetIndex];

            SkillCastTargetUtility.AddCircle(ref castTarget, primaryPosition, attackRange, damage);
            AddExcludedTargetPosition(ref excludedPositions, primaryPosition);
        }

        var secondaryCount = SkillMath.CalculateAttackCircleCount(
            skillState.Level,
            advancedConfig.SecondaryBaseCount,
            advancedConfig.SecondaryCountLevelWeight,
            advancedConfig.SecondaryMaxCount,
            config.TargetCountRoundMode);
        var secondaryAttackRange = attackRange * math.max(0f, advancedConfig.SecondaryAttackRangeMultiplier);

        for (var chainIndex = 0; chainIndex < secondaryCount; chainIndex++)
        {
            var chainOrigin = primaryPositions[chainIndex % primaryPositions.Length];

            if (!TryFindDensityBiasedRandomMonster(
                    chainOrigin,
                    advancedConfig.SecondaryTargetRange,
                    PlayerCombatConstants.TargetSelectionDirectionBucketCount,
                    PlayerCombatConstants.TargetSelectionDistanceWeight,
                    excludedPositions,
                    ref random,
                    monsterEntities,
                    monsterTransforms,
                    healthLookup,
                    out var chainedPosition))
            {
                break;
            }

            if (!SkillCastTargetUtility.AddCircle(
                    ref castTarget,
                    chainedPosition,
                    secondaryAttackRange,
                    damage))
            {
                break;
            }

            AddExcludedTargetPosition(ref excludedPositions, chainedPosition);
        }

        return castTarget.Positions.Length > 0;
    }

    private static void AddExcludedTargetPosition(
        ref FixedList512Bytes<float3> excludedPositions,
        float3 position)
    {
        if (excludedPositions.Length >= PlayerCombatConstants.MaxAttackCircleCount)
        {
            return;
        }

        excludedPositions.Add(position);
    }

    private static bool PrepareRandomGroundCircleAttack(
        AttackSkillConfig config,
        AttackSkillAdvancedConfig advancedConfig,
        AttackSkillState skillState,
        float3 ownerPosition,
        float targetRange,
        float attackRange,
        int damage,
        ref Unity.Mathematics.Random random,
        ref SkillCastTarget castTarget)
    {
        var targetCount = SkillMath.CalculateAttackCircleCount(
            skillState.Level,
            config.BaseTargetCount,
            config.TargetCountLevelWeight,
            config.MaxTargetCount,
            config.TargetCountRoundMode);
        var randomGroundRadius = advancedConfig.RandomGroundRadius > 0f
            ? advancedConfig.RandomGroundRadius
            : targetRange;

        for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            if (!SkillCastTargetUtility.AddCircle(
                    ref castTarget,
                    ownerPosition + CreateRandomCircleOffset(randomGroundRadius, ref random),
                    attackRange,
                    damage))
            {
                break;
            }
        }

        return castTarget.Positions.Length > 0;
    }

    private static float3 CreateRandomCircleOffset(
        float radius,
        ref Unity.Mathematics.Random random)
    {
        var safeRadius = math.max(0f, radius);
        var angle = random.NextFloat(0f, math.PI * 2f);
        var distance = math.sqrt(random.NextFloat()) * safeRadius;

        return new float3(
            math.cos(angle) * distance,
            0f,
            math.sin(angle) * distance);
    }

    public static int ExecuteTargetCenteredCircleAttack(
        AttackSkillConfig config,
        ref AttackSkillState skillState,
        float3 ownerPosition,
        BuffAccumulator buffs,
        float playerLevelDamageRate,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup)
    {
        if (!TryPrepareTargetCenteredCircleAttack(
                config,
                SkillDefaults.CreateDefaultAdvancedConfig(),
                skillState,
                ownerPosition,
                new float2(0f, 1f),
                buffs,
                playerLevelDamageRate,
                ref random,
                monsterEntities,
                monsterTransforms,
                healthLookup,
                out var castTarget))
        {
            skillState.IsTriggered = 0;
            return 0;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        for (var targetIndex = 0; targetIndex < castTarget.Positions.Length; targetIndex++)
        {
            SkillAttackRangeDebugEvents.Record(
                castTarget.Positions[targetIndex],
                SkillCastTargetUtility.GetAttackRange(castTarget, targetIndex));
        }
#endif

        var hitCount = ApplyTargetCenteredCircleAttack(
            castTarget,
            monsterEntities,
            monsterTransforms,
            healthLookup,
            hitVfxLookup);

        skillState.IsTriggered = 0;
        skillState.IsCooltime = 1;
        skillState.Timer = 0f;

        return hitCount;
    }

    public static float CalculatePlayerLevelDamageRate(
        Entity owner,
        ComponentLookup<ExperienceComponent> experienceLookup,
        ComponentLookup<PlayerLevelStats> levelStatsLookup)
    {
        if (!experienceLookup.HasComponent(owner) ||
            !levelStatsLookup.HasComponent(owner))
        {
            return 1f;
        }

        return ExperienceMath.CalculatePlayerSkillDamageRate(
            levelStatsLookup[owner],
            experienceLookup[owner].Level);
    }

    private static void PlayMonsterHitVfx(
        Entity monsterEntity,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup)
    {
        if (!hitVfxLookup.HasComponent(monsterEntity))
        {
            return;
        }

        var hitVfx = hitVfxLookup[monsterEntity];

        hitVfx.ElapsedTime = 0f;
        hitVfx.IsPlaying = 1;
        hitVfxLookup[monsterEntity] = hitVfx;
    }

    public static uint CreateTargetSelectionSeed(
        int skillId,
        float3 ownerPosition,
        uint sequence)
    {
        var hash = math.hash(new uint4(
            (uint)skillId,
            (uint)math.floor(ownerPosition.x),
            (uint)math.floor(ownerPosition.z),
            sequence));

        if (hash == 0u)
        {
            return 1u;
        }

        return hash;
    }

    public static bool TryFindDensityBiasedRandomMonster(
        float3 ownerPosition,
        float targetRange,
        int directionBucketCount,
        float distanceWeight,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        NativeArray<HealthComponent> monsterHealths,
        out float3 targetPosition)
    {
        var bucketCount = math.max(1, directionBucketCount);
        var bucketWeights = new NativeArray<float>(bucketCount, Allocator.Temp);
        var targetRangeSq = targetRange * targetRange;
        var found = false;

        for (var monsterIndex = 0; monsterIndex < monsterTransforms.Length; monsterIndex++)
        {
            if (monsterIndex >= monsterHealths.Length ||
                HealthMath.IsDead(monsterHealths[monsterIndex]))
            {
                continue;
            }

            var monsterPosition = monsterTransforms[monsterIndex].Position;
            var delta = monsterPosition.xz - ownerPosition.xz;
            var distanceSq = math.lengthsq(delta);

            if (distanceSq > targetRangeSq)
            {
                continue;
            }

            var bucketIndex = SkillMath.CalculateDirectionBucketIndex(delta, bucketCount);
            var selectionWeight = SkillMath.CalculateTargetSelectionWeight(
                distanceSq,
                targetRangeSq,
                distanceWeight);

            bucketWeights[bucketIndex] += selectionWeight;
            found = true;
        }

        if (!found)
        {
            bucketWeights.Dispose();
            targetPosition = float3.zero;
            return false;
        }

        var selectedBucket = SelectWeightedBucket(bucketWeights, ref random);
        var totalSelectedWeight = 0f;
        var selectedPosition = float3.zero;

        for (var monsterIndex = 0; monsterIndex < monsterTransforms.Length; monsterIndex++)
        {
            if (monsterIndex >= monsterHealths.Length ||
                HealthMath.IsDead(monsterHealths[monsterIndex]))
            {
                continue;
            }

            var monsterPosition = monsterTransforms[monsterIndex].Position;
            var delta = monsterPosition.xz - ownerPosition.xz;
            var distanceSq = math.lengthsq(delta);

            if (distanceSq > targetRangeSq ||
                SkillMath.CalculateDirectionBucketIndex(delta, bucketCount) != selectedBucket)
            {
                continue;
            }

            var selectionWeight = SkillMath.CalculateTargetSelectionWeight(
                distanceSq,
                targetRangeSq,
                distanceWeight);

            totalSelectedWeight += selectionWeight;

            if (random.NextFloat(totalSelectedWeight) < selectionWeight)
            {
                selectedPosition = monsterPosition;
            }
        }

        bucketWeights.Dispose();
        targetPosition = selectedPosition;
        return totalSelectedWeight > 0f;
    }

    public static bool TryFindDensityBiasedRandomMonster(
        float3 ownerPosition,
        float targetRange,
        int directionBucketCount,
        float distanceWeight,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        out float3 targetPosition)
    {
        return TryFindDensityBiasedRandomMonster(
            ownerPosition,
            targetRange,
            directionBucketCount,
            distanceWeight,
            default,
            ref random,
            monsterEntities,
            monsterTransforms,
            healthLookup,
            out targetPosition);
    }

    public static bool TryFindDensityBiasedRandomMonsterPositions(
        float3 ownerPosition,
        float targetRange,
        int directionBucketCount,
        float distanceWeight,
        int targetCount,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        out FixedList512Bytes<float3> targetPositions)
    {
        targetPositions = default;

        var safeTargetCount = math.clamp(
            targetCount,
            0,
            PlayerCombatConstants.MaxAttackCircleCount);

        if (safeTargetCount == 0)
        {
            return false;
        }

        var bucketCount = math.max(1, directionBucketCount);
        var bucketWeights = new NativeArray<float>(bucketCount, Allocator.Temp);
        var candidatePositions = new NativeList<float3>(Allocator.Temp);
        var candidateBucketIndices = new NativeList<int>(Allocator.Temp);
        var candidateWeights = new NativeList<float>(Allocator.Temp);
        var targetRangeSq = targetRange * targetRange;
        var monsterCount = math.min(monsterEntities.Length, monsterTransforms.Length);
        var remainingWeight = 0f;

        for (var monsterIndex = 0; monsterIndex < monsterCount; monsterIndex++)
        {
            var monsterEntity = monsterEntities[monsterIndex];

            if (!healthLookup.HasComponent(monsterEntity) ||
                HealthMath.IsDead(healthLookup[monsterEntity]))
            {
                continue;
            }

            var monsterPosition = monsterTransforms[monsterIndex].Position;
            var delta = monsterPosition.xz - ownerPosition.xz;
            var distanceSq = math.lengthsq(delta);

            if (distanceSq > targetRangeSq)
            {
                continue;
            }

            var bucketIndex = SkillMath.CalculateDirectionBucketIndex(delta, bucketCount);
            var selectionWeight = SkillMath.CalculateTargetSelectionWeight(
                distanceSq,
                targetRangeSq,
                distanceWeight);

            candidatePositions.Add(monsterPosition);
            candidateBucketIndices.Add(bucketIndex);
            candidateWeights.Add(selectionWeight);
            bucketWeights[bucketIndex] += selectionWeight;
            remainingWeight += selectionWeight;
        }

        if (candidatePositions.Length == 0)
        {
            candidateWeights.Dispose();
            candidateBucketIndices.Dispose();
            candidatePositions.Dispose();
            bucketWeights.Dispose();
            return false;
        }

        for (var targetIndex = 0; targetIndex < safeTargetCount; targetIndex++)
        {
            if (remainingWeight <= 0f)
            {
                break;
            }

            var selectedBucket = SelectWeightedBucket(bucketWeights, ref random);
            var selectedCandidateIndex = -1;
            var totalSelectedWeight = 0f;

            for (var candidateIndex = 0; candidateIndex < candidatePositions.Length; candidateIndex++)
            {
                var candidateWeight = candidateWeights[candidateIndex];

                if (candidateWeight <= 0f ||
                    candidateBucketIndices[candidateIndex] != selectedBucket)
                {
                    continue;
                }

                totalSelectedWeight += candidateWeight;

                if (random.NextFloat(totalSelectedWeight) < candidateWeight)
                {
                    selectedCandidateIndex = candidateIndex;
                }
            }

            if (selectedCandidateIndex < 0)
            {
                break;
            }

            var selectedPosition = candidatePositions[selectedCandidateIndex];

            targetPositions.Add(selectedPosition);

            for (var candidateIndex = 0; candidateIndex < candidatePositions.Length; candidateIndex++)
            {
                var candidateWeight = candidateWeights[candidateIndex];

                if (candidateWeight <= 0f ||
                    math.lengthsq(candidatePositions[candidateIndex].xz - selectedPosition.xz) > MinDuplicateTargetDistanceSq)
                {
                    continue;
                }

                var bucketIndex = candidateBucketIndices[candidateIndex];

                candidateWeights[candidateIndex] = 0f;
                bucketWeights[bucketIndex] = math.max(0f, bucketWeights[bucketIndex] - candidateWeight);
                remainingWeight = math.max(0f, remainingWeight - candidateWeight);
            }
        }

        candidateWeights.Dispose();
        candidateBucketIndices.Dispose();
        candidatePositions.Dispose();
        bucketWeights.Dispose();
        return targetPositions.Length > 0;
    }

    public static bool TryFindDensityBiasedRandomMonster(
        float3 ownerPosition,
        float targetRange,
        int directionBucketCount,
        float distanceWeight,
        FixedList512Bytes<float3> excludedPositions,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        out float3 targetPosition)
    {
        var bucketCount = math.max(1, directionBucketCount);
        var bucketWeights = new NativeArray<float>(bucketCount, Allocator.Temp);
        var targetRangeSq = targetRange * targetRange;
        var found = false;

        for (var monsterIndex = 0; monsterIndex < monsterTransforms.Length; monsterIndex++)
        {
            var monsterEntity = monsterEntities[monsterIndex];

            if (!healthLookup.HasComponent(monsterEntity) ||
                HealthMath.IsDead(healthLookup[monsterEntity]))
            {
                continue;
            }

            var monsterPosition = monsterTransforms[monsterIndex].Position;

            if (IsExcludedTargetPosition(monsterPosition, excludedPositions))
            {
                continue;
            }

            var delta = monsterPosition.xz - ownerPosition.xz;
            var distanceSq = math.lengthsq(delta);

            if (distanceSq > targetRangeSq)
            {
                continue;
            }

            var bucketIndex = SkillMath.CalculateDirectionBucketIndex(delta, bucketCount);
            var selectionWeight = SkillMath.CalculateTargetSelectionWeight(
                distanceSq,
                targetRangeSq,
                distanceWeight);

            bucketWeights[bucketIndex] += selectionWeight;
            found = true;
        }

        if (!found)
        {
            bucketWeights.Dispose();
            targetPosition = float3.zero;
            return false;
        }

        var selectedBucket = SelectWeightedBucket(bucketWeights, ref random);
        var totalSelectedWeight = 0f;
        var selectedPosition = float3.zero;

        for (var monsterIndex = 0; monsterIndex < monsterTransforms.Length; monsterIndex++)
        {
            var monsterEntity = monsterEntities[monsterIndex];

            if (!healthLookup.HasComponent(monsterEntity) ||
                HealthMath.IsDead(healthLookup[monsterEntity]))
            {
                continue;
            }

            var monsterPosition = monsterTransforms[monsterIndex].Position;

            if (IsExcludedTargetPosition(monsterPosition, excludedPositions))
            {
                continue;
            }

            var delta = monsterPosition.xz - ownerPosition.xz;
            var distanceSq = math.lengthsq(delta);

            if (distanceSq > targetRangeSq ||
                SkillMath.CalculateDirectionBucketIndex(delta, bucketCount) != selectedBucket)
            {
                continue;
            }

            var selectionWeight = SkillMath.CalculateTargetSelectionWeight(
                distanceSq,
                targetRangeSq,
                distanceWeight);

            totalSelectedWeight += selectionWeight;

            if (random.NextFloat(totalSelectedWeight) < selectionWeight)
            {
                selectedPosition = monsterPosition;
            }
        }

        bucketWeights.Dispose();
        targetPosition = selectedPosition;
        return totalSelectedWeight > 0f;
    }

    private static bool IsExcludedTargetPosition(
        float3 position,
        FixedList512Bytes<float3> excludedPositions)
    {
        for (var targetIndex = 0; targetIndex < excludedPositions.Length; targetIndex++)
        {
            if (math.lengthsq(position.xz - excludedPositions[targetIndex].xz) <= MinDuplicateTargetDistanceSq)
            {
                return true;
            }
        }

        return false;
    }

    private static int SelectWeightedBucket(
        NativeArray<float> bucketWeights,
        ref Unity.Mathematics.Random random)
    {
        var totalWeight = 0f;

        for (var bucketIndex = 0; bucketIndex < bucketWeights.Length; bucketIndex++)
        {
            totalWeight += math.max(0f, bucketWeights[bucketIndex]);
        }

        if (totalWeight <= 0f)
        {
            return 0;
        }

        var roll = random.NextFloat(totalWeight);
        var accumulatedWeight = 0f;

        for (var bucketIndex = 0; bucketIndex < bucketWeights.Length; bucketIndex++)
        {
            accumulatedWeight += math.max(0f, bucketWeights[bucketIndex]);

            if (roll < accumulatedWeight)
            {
                return bucketIndex;
            }
        }

        return math.max(0, bucketWeights.Length - 1);
    }
}
