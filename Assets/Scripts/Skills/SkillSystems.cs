using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;

// =============================================================================
// スキル実行パイプライン
//
//   SkillCooltimeSystem        クールタイムを進め、終わったスキルを「発動可能」に戻す
//        ↓
//   PlayerCombatSystem         発動可能なスキルを 1 つ選び、発動予約（IsTriggered）を立てる
//        ↓
//   SkillCastElapsedTimeSystem 発動中スキルの経過時間を進める（ダメージ・演出の共通時計）
//        ↓
//   SkillLogicSystem           ターゲットを決定し、規定時間にダメージとデバフを適用する
//        ↓
//   SkillPresentationSystem    VFX / SFX を再生する（Managed 処理なので別 System に分離）
//        ↓
//   SkillCastCompletionSystem  ダメージと演出が終わったスキルをクールタイムへ戻す
//
// 各段階を独立した System にすることで、処理順を属性で明示でき、
// 個別にテスト・デバッグしやすくしている。
// =============================================================================

/// <summary>
/// 装備中の攻撃スキルのクールタイムを進める。
/// クールタイム中のスロットだけを処理し、時間が経過したら発動可能な状態へ戻す。
/// バフスキルによるクールタイム短縮もここで反映する。
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
/// プレイヤーの発動可能な攻撃スキルを 1 つ選び、発動予約する。
/// 複数が発動可能な場合はスロット番号が小さいものを優先する。
/// ターゲット決定やダメージ処理は SkillLogicSystem に任せ、ここでは IsTriggered を立てるだけにしている。
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
/// 発動中の攻撃スキルの経過時間を進める。
/// 経過時間の更新を独立した System にすることで、ダメージ・SFX・VFX が同じ時刻を参照でき、タイミングがずれない。
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
/// 発動予約された攻撃スキルの本体処理。
/// 1. 発動予約があれば、攻撃形状とターゲット位置（SkillCastTarget）を確定する
/// 2. 経過時間がダメージ発生タイミングに達したら、範囲内のモンスターへダメージとデバフを適用する
/// 連続攻撃スキルは、規定回数に達するまで 2 を繰り返す。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillCastElapsedTimeSystem))]
[UpdateBefore(typeof(SkillPresentationSystem))]
public partial struct SkillLogicSystem : ISystem
{
    private EntityQuery equippedBuffSkillQuery;
    private EntityQuery monsterQuery;
    // 乱数 seed を毎回変えるための通し番号。
    private uint targetSelectionSequence;

    // エディタ / 開発ビルドではデバッグ表示用の Managed 処理を呼ぶため、Burst はリリースビルドのみ有効にする。
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

        // ターゲット探索用に、モンスターの位置をフレーム開始時にまとめて配列化しておく。
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

            // --- 1. 発動予約があれば、攻撃形状とターゲットを確定する ---
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

            // --- 2. ダメージ発生タイミングに達したら、範囲内のモンスターへダメージを適用する ---
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

                // 連続攻撃スキルは、規定回数に達した時点で「ダメージ適用済み」にする。
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

    /// <summary>
    /// 発動予約を消費し、スキルの攻撃形状とターゲットを確定して発動状態へ移す。
    /// 範囲内にターゲットがいない場合は発動せず、次のフレームで再度予約されるのを待つ。
    /// </summary>
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
        // 発動予約は 1 回限りのイベントとして扱い、ここで消費する。
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

        // LogicId 0〜7 は AttackSkillLogicKind に対応する。形状ごとの分岐は TryPrepareTargetCenteredCircleAttack 内で行う。
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
/// ダメージと演出の両方が終わった攻撃スキルを、クールタイム状態へ移す。
/// 演出（Managed 処理）は SkillPresentationSystem が担当するため、この System は完了したかどうかだけを判定する。
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
/// モンスターにかかっているデバフの残り時間を進め、効果を 1 つの集約値（DebuffAggregate）にまとめる。
/// 移動や AI はこの集約値（移動速度倍率など）を読むだけでよく、個々のデバフの種類を知らなくて済む。
/// 継続ダメージはここで HP に反映し、氷結は FreezeComponent の付与に変換する。
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

                // 期限切れのデバフは次の配列へコピーしないことで削除する。
                if (debuff.RemainingTime <= 0f)
                {
                    continue;
                }

                // 氷結は倍率ではなく「移動処理から外す」効果なので、集約せずに専用 Component へ渡す。
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

    /// <summary>
    /// 氷結を新しく付与するか、すでに氷結中なら残り時間の長いほうで延長する。
    /// 解除時に元の速度へ戻せるよう、付与する時点の Velocity を保存しておく。
    /// </summary>
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
/// 麻痺の効果を移動速度倍率に反映する（時間とともに徐々に回復する）。
/// 氷結と違って Velocity は外さず、DebuffAggregate の倍率に掛け合わせることで「動きにくい」状態を表す。
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
/// 氷結した Entity を移動処理から外す。
/// Velocity Component を取り除くと、移動・AI・経路追従の各 System の Query に一致しなくなるため、
/// それらの System に「氷結中なら動かない」という分岐を書かずに済む。
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
        }

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }
}

/// <summary>
/// 氷結時間が終わった Entity を通常の移動に戻す。
/// 氷結の経過時間を進め、時間切れになったら氷結用の Component を外し、保存しておいた Velocity を付け直す。
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
/// スキル System から呼ばれる処理をまとめたクラス（ターゲット選択、攻撃形状の判定、ダメージ適用など）。
/// Entity の構造変更は行わず、引数で受け取った NativeArray / ComponentLookup だけを読み書きするため、
/// System の外から単体テストしやすい。
/// </summary>
public static class SkillSystemUtility
{
    // これより近い 2 点は同じターゲットとみなす（距離の 2 乗）。
    private const float MinDuplicateTargetDistanceSq = 0.0001f;

    /// <summary>
    /// 指定した所有者が装備しているバフスキルの効果を合算する。
    /// </summary>
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

    /// <summary>
    /// 所有者の向きを正規化して返す。向きが取得できない場合は +Z 方向を返す。
    /// </summary>
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

    /// <summary>
    /// XZ 方向ベクトルを正規化する。長さがほぼ 0 の場合は +Z 方向を返す。
    /// </summary>
    public static float2 NormalizeDirection(float2 direction)
    {
        var lengthSq = math.lengthsq(direction);

        if (lengthSq <= 0.000001f)
        {
            return new float2(0f, 1f);
        }

        return direction * math.rsqrt(lengthSq);
    }

    /// <summary>
    /// 点が扇形（原点・向き・半径・半角の cos で定義）の内側にあるかを XZ 平面で判定する。
    /// 角度の比較は内積と cos で行い、三角関数の計算を避けている。
    /// </summary>
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

    /// <summary>
    /// 点が直線状の攻撃範囲（原点から向きに沿った長さ length・幅 width の長方形）に入っているかを XZ 平面で判定する。
    /// </summary>
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

        // 直線上の最も近い点との距離が、幅の半分以内なら範囲内。
        var halfWidth = math.max(0f, width) * 0.5f;
        var closestPointDelta = delta - normalizedDirection * forwardDistance;

        return math.lengthsq(closestPointDelta) <= halfWidth * halfWidth;
    }

    /// <summary>
    /// スキルの状態を「発動中」に切り替え、発動ごとの進行フラグをリセットする。
    /// </summary>
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

    /// <summary>
    /// スキルの状態を「クールタイム中」に切り替え、発動中に使ったフラグをすべてリセットする。
    /// </summary>
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

    /// <summary>
    /// スキル設定・レベル・バフから攻撃範囲とダメージを計算し、LogicId に応じた形状で SkillCastTarget を組み立てる。
    /// 1 つもターゲットを作れなかった場合は false を返す。
    /// </summary>
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

        // 攻撃形状ごとの準備処理へ振り分ける（番号は AttackSkillLogicKind に対応）。
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
                // 同じ場所への連続攻撃：通常の円形攻撃と同じターゲットを使い、回数と間隔だけを追加する。
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

    /// <summary>
    /// 確定済みの攻撃範囲内にいるモンスターへダメージを与える（デバフなし）。
    /// </summary>
    /// <returns>ダメージを与えた回数。</returns>
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

    /// <summary>
    /// 確定済みの攻撃範囲内にいるモンスターへ、ダメージとスキルに設定されたデバフを与える。
    /// </summary>
    /// <returns>ダメージを与えた回数。</returns>
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
        // 扇形・直線は専用の判定を使い、それ以外は「円のリスト」として判定する。
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

        // 円ごとに判定するため、円が重なる場所にいるモンスターは重なった数だけヒットする。
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

    /// <summary>
    /// 前方の扇形の範囲内にいるモンスターへダメージを与える。
    /// </summary>
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

    /// <summary>
    /// 直線上（貫通）の範囲内にいるモンスターへダメージを与える。
    /// </summary>
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

    /// <summary>
    /// スキルに設定されたデバフを、それぞれの発生確率で判定してモンスターに付与する。
    /// </summary>
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

    /// <summary>
    /// デバフを 1 つ付与する。同じ DebuffId がすでにある場合は、StackPolicy に従って
    /// 「上書き」「強いほうを残す」「別枠で重ねる」のいずれかで処理する。
    /// </summary>
    private static void ApplyDebuffSpec(
        ref DebuffRuntimeState runtime,
        SkillDebuffSpec spec)
    {
        var activeDebuff = DebuffMath.CreateActiveDebuff(spec);

        if (activeDebuff.RemainingTime <= 0f)
        {
            return;
        }

        // 同じデバフがすでにかかっている場合の処理。
        if (spec.StackPolicy != DebuffStackPolicy.StackIndependent)
        {
            for (var debuffIndex = 0; debuffIndex < runtime.ActiveDebuffs.Length; debuffIndex++)
            {
                var currentDebuff = runtime.ActiveDebuffs[debuffIndex];

                if (currentDebuff.DebuffId != activeDebuff.DebuffId)
                {
                    continue;
                }

                // 新しいほうが弱い場合は効果を上書きせず、残り時間だけ延長する。
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

        // 固定長配列なので、上限に達したら新しいデバフは付与しない。
        if (runtime.ActiveDebuffs.Length >= DebuffConstants.MaxActiveDebuffCount)
        {
            return;
        }

        runtime.ActiveDebuffs.Add(activeDebuff);
    }

    /// <summary>
    /// [0] 対象中心の円形範囲攻撃：範囲内のモンスターから複数を選び、それぞれの位置に円形の攻撃範囲を置く。
    /// </summary>
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

    /// <summary>
    /// [1] 前方扇形攻撃：所有者の向きを中心にした扇形を攻撃範囲にする。
    /// </summary>
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

        // 演出とデバッグ表示用に、扇形の中心付近に代表の円を 1 つ登録する（ダメージ判定は扇形で行う）。
        return SkillCastTargetUtility.AddCircle(
            ref castTarget,
            ownerPosition + new float3(direction.x, 0f, direction.y) * (length * 0.5f),
            length * 0.5f,
            damage);
    }

    /// <summary>
    /// [2] 直線貫通攻撃：所有者の向きに伸びる長方形を攻撃範囲にする。
    /// </summary>
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

        // 演出とデバッグ表示用の代表位置（ダメージ判定は長方形で行う）。
        return SkillCastTargetUtility.AddCircle(
            ref castTarget,
            ownerPosition + new float3(direction.x, 0f, direction.y) * (length * 0.5f),
            math.max(lineWidth * 0.5f, 0.01f),
            damage);
    }

    /// <summary>
    /// [3] 自分中心 AoE：所有者の位置に円形の攻撃範囲を 1 つ置く。ターゲットがいなくても発動する。
    /// </summary>
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

    /// <summary>
    /// [4] 拡散爆発：まず主ターゲットに円を置き、その周囲のランダムな位置に小さな円（二次爆発）を追加する。
    /// </summary>
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

        // 二次爆発は主ターゲットに順番に割り振る。
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

    /// <summary>
    /// [5] 連鎖攻撃（雷のイメージ）：主ターゲットから近くの別のモンスターへ攻撃を連鎖させる。
    /// 一度当たった位置は除外リストに入れ、同じ敵に連鎖しないようにする。
    /// </summary>
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

    /// <summary>
    /// 連鎖済みの位置を除外リストに追加する。固定長リストのため上限を超えた分は無視する。
    /// </summary>
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

    /// <summary>
    /// [6] ランダム落下：所有者の周囲のランダムな地点に円形の攻撃範囲を置く。ターゲットがいなくても発動する。
    /// </summary>
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

    /// <summary>
    /// 半径 radius の円の内側から、一様に分布するランダムな XZ オフセットを返す。
    /// </summary>
    private static float3 CreateRandomCircleOffset(
        float radius,
        ref Unity.Mathematics.Random random)
    {
        var safeRadius = math.max(0f, radius);
        var angle = random.NextFloat(0f, math.PI * 2f);
        // 距離に sqrt をかけないと、点が円の中心付近に偏ってしまう。
        var distance = math.sqrt(random.NextFloat()) * safeRadius;

        return new float3(
            math.cos(angle) * distance,
            0f,
            math.sin(angle) * distance);
    }

    /// <summary>
    /// ターゲットの決定からダメージ適用までを 1 回の呼び出しでまとめて行う（主にテスト用）。
    /// 実際のゲーム中は、発動タイミングとダメージのタイミングを分けるため SkillLogicSystem が段階的に処理する。
    /// </summary>
    /// <returns>ダメージを与えた回数。</returns>
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

    /// <summary>
    /// プレイヤーのレベルに応じたスキルダメージ倍率を返す。レベル情報がない場合は 1 倍。
    /// </summary>
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

    /// <summary>
    /// モンスターの被弾 VFX を最初から再生し直す。実際の再生は VFX 側の System が行う。
    /// </summary>
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

    /// <summary>
    /// ターゲット選択に使う乱数の seed を作る。スキル ID・位置・通し番号からハッシュを作り、
    /// 発動ごとに異なりつつ再現性のある乱数にする。
    /// </summary>
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

        // Unity.Mathematics.Random は seed 0 を受け付けないため 1 に置き換える。
        if (hash == 0u)
        {
            return 1u;
        }

        return hash;
    }

    /// <summary>
    /// 範囲内のモンスターから、密度に応じた確率で 1 体を選ぶ。
    /// 1. 周囲を方向ごとのグループ（バケット）に分け、グループごとに重みを合計する
    /// 2. 重みに比例してグループを 1 つ選ぶ（敵が多い方向ほど選ばれやすい）
    /// 3. そのグループの中から、重みに比例して 1 体を選ぶ
    /// 敵が密集している方向を狙いやすくしつつ、毎回同じ敵ばかり狙わないようにしている。
    /// </summary>
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

        // 1. 方向ごとのグループに重みを集計する。
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

        // 2. グループを 1 つ選ぶ。
        var selectedBucket = SelectWeightedBucket(bucketWeights, ref random);
        var totalSelectedWeight = 0f;
        var selectedPosition = float3.zero;

        // 3. 選んだグループの中から 1 体を選ぶ。
        //    リザーバーサンプリングで、配列を 1 回走査するだけで重みに比例した選択を行う。
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

    /// <summary>
    /// TryFindDensityBiasedRandomMonster の ComponentLookup 版（除外する位置なし）。
    /// </summary>
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

    /// <summary>
    /// 密度に応じた選び方（TryFindDensityBiasedRandomMonster と同じ考え方）で、重複しないターゲットを最大 targetCount 体選ぶ。
    /// 候補の一覧を 1 回だけ作り、選んだ候補の重みを 0 にしていくことで、毎回全モンスターを走査し直さずに済む。
    /// </summary>
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

        // 範囲内の生きているモンスターを候補として集め、方向グループごとの重みを集計する。
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

        // 必要な数だけ「グループを選ぶ → その中から 1 体を選ぶ」を繰り返す。
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

            // 選んだ候補（と同じ位置にいる候補）の重みを 0 にして、次回以降に選ばれないようにする。
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

    /// <summary>
    /// 密度に応じた選び方で 1 体を選ぶ。excludedPositions にある位置のモンスターは候補から除外する（連鎖攻撃用）。
    /// </summary>
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

    /// <summary>
    /// 位置が除外リストのいずれかとほぼ同じ場所にあるかを判定する。
    /// </summary>
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

    /// <summary>
    /// 重みに比例した確率でグループ（バケット）を 1 つ選ぶ。重みがすべて 0 の場合は 0 番を返す。
    /// </summary>
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
