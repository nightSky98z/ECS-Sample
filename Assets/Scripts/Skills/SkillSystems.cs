using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;

/// <summary>
/// 装備済み attack skill の cooltime を更新する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct SkillCooltimeSystem : ISystem
{
    private EntityQuery equippedBuffSkillQuery;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AttackSkillConfig>();
        state.RequireForUpdate<AttackSkillState>();

        equippedBuffSkillQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<BuffSkillConfig, SkillSlotComponent, EquippedSkillTag, BuffSkillSlotTag>()
            .Build(ref state);
    }

    public void OnUpdate(ref SystemState state)
    {
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
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillCooltimeSystem))]
public partial struct PlayerCombatSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
        state.RequireForUpdate<AttackSkillState>();
    }

    public void OnUpdate(ref SystemState state)
    {
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
/// 発動予約された attack skill の logic を実行する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(PlayerCombatSystem))]
public partial struct SkillLogicSystem : ISystem
{
    private EntityQuery equippedBuffSkillQuery;
    private EntityQuery monsterQuery;
    private uint targetSelectionSequence;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<AttackSkillConfig>();
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

    public void OnUpdate(ref SystemState state)
    {
        var deltaTime = SystemAPI.Time.DeltaTime;
        var localToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true);
        var healthLookup = SystemAPI.GetComponentLookup<HealthComponent>(false);
        var hitVfxLookup = SystemAPI.GetComponentLookup<MonsterHitVfxState>(false);
        var experienceLookup = SystemAPI.GetComponentLookup<ExperienceComponent>(true);
        var levelStatsLookup = SystemAPI.GetComponentLookup<PlayerLevelStats>(true);
        var presentationLookup = SystemAPI.GetComponentLookup<AttackSkillPresentation>(true);
        var buffSkills = equippedBuffSkillQuery.ToComponentDataArray<BuffSkillConfig>(Allocator.Temp);
        var buffSlots = equippedBuffSkillQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var monsterEntities = monsterQuery.ToEntityArray(Allocator.Temp);
        var monsterTransforms = monsterQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        foreach (var (config, timing, skillState, slot, castTarget, skillEntity) in
                 SystemAPI.Query<RefRO<AttackSkillConfig>, RefRO<AttackSkillTimingConfig>, RefRW<AttackSkillState>, RefRO<SkillSlotComponent>, RefRW<SkillCastTarget>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>()
                     .WithEntityAccess())
        {
            var skillStateValue = skillState.ValueRO;
            var castTargetValue = castTarget.ValueRO;

            if (skillStateValue.IsTriggered != 0)
            {
                TryStartSkillCast(
                    config.ValueRO,
                    ref skillStateValue,
                    ref castTargetValue,
                    slot.ValueRO.Owner,
                    localToWorldLookup,
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

            skillStateValue.CastElapsedTime += deltaTime;

            if (skillStateValue.DamageApplied == 0 &&
                SkillMath.IsDelayReached(skillStateValue.CastElapsedTime, timing.ValueRO.DamageDelay))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                for (var targetIndex = 0; targetIndex < castTargetValue.Positions.Length; targetIndex++)
                {
                    SkillAttackRangeDebugEvents.Record(
                        castTargetValue.Positions[targetIndex],
                        castTargetValue.AttackRange);
                }
#endif

                SkillSystemUtility.ApplyTargetCenteredCircleAttack(
                    castTargetValue,
                    monsterEntities,
                    monsterTransforms,
                    healthLookup,
                    hitVfxLookup);
                skillStateValue.DamageApplied = 1;
            }

            if (skillStateValue.StartedThisFrame != 0)
            {
                skillStateValue.StartedThisFrame = 0;
                skillState.ValueRW = skillStateValue;
                castTarget.ValueRW = castTargetValue;
                continue;
            }

            if (skillStateValue.DamageApplied != 0 &&
                (!presentationLookup.HasComponent(skillEntity) ||
                 SkillMath.IsPresentationComplete(skillStateValue, timing.ValueRO)))
            {
                SkillSystemUtility.StartCooltime(ref skillStateValue);
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
        ref AttackSkillState skillState,
        ref SkillCastTarget castTarget,
        Entity owner,
        ComponentLookup<LocalToWorld> localToWorldLookup,
        ComponentLookup<ExperienceComponent> experienceLookup,
        ComponentLookup<PlayerLevelStats> levelStatsLookup,
        NativeArray<BuffSkillConfig> buffSkills,
        NativeArray<SkillSlotComponent> buffSlots,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup)
    {
        skillState.IsTriggered = 0;

        if (!localToWorldLookup.HasComponent(owner))
        {
            skillState.IsCasting = 0;
            return;
        }

        var ownerPosition = localToWorldLookup[owner].Position;
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
                var random = new Unity.Mathematics.Random(
                    SkillSystemUtility.CreateTargetSelectionSeed(
                        config.Id,
                        ownerPosition,
                        targetSelectionSequence++));

                if (!SkillSystemUtility.TryPrepareTargetCenteredCircleAttack(
                        config,
                        skillState,
                        ownerPosition,
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
                return;
            default:
                skillState.IsCasting = 0;
                return;
        }
    }
}

/// <summary>
/// Skill system が使う配列ベースの実行 helper。
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

    public static void StartCasting(ref AttackSkillState skillState)
    {
        skillState.Timer = 0f;
        skillState.CastElapsedTime = 0f;
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
        skillState.IsTriggered = 0;
        skillState.IsCasting = 0;
        skillState.IsCooltime = 1;
        skillState.StartedThisFrame = 0;
    }

    public static bool TryPrepareTargetCenteredCircleAttack(
        AttackSkillConfig config,
        AttackSkillState skillState,
        float3 ownerPosition,
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
            castTarget = default;
            return false;
        }

        var attackRange = SkillMath.CalculateEffectiveAttackRange(config, buffs);
        var damage = SkillMath.CalculateDamageToHp(
            SkillMath.CalculateEffectiveDamage(config, skillState, buffs, playerLevelDamageRate));

        castTarget = new SkillCastTarget
        {
            Positions = targetPositions,
            AttackRange = attackRange,
            Damage = damage
        };
        return true;
    }

    public static int ApplyTargetCenteredCircleAttack(
        SkillCastTarget castTarget,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup)
    {
        var attackRangeSq = castTarget.AttackRange * castTarget.AttackRange;
        var hitCount = 0;

        for (var targetIndex = 0; targetIndex < castTarget.Positions.Length; targetIndex++)
        {
            var targetPosition = castTarget.Positions[targetIndex];

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
                    -math.max(0, castTarget.Damage));
                PlayMonsterHitVfx(monsterEntity, hitVfxLookup);
                hitCount++;
            }
        }

        return hitCount;
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
                skillState,
                ownerPosition,
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
                castTarget.AttackRange);
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
