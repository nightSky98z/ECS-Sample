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
        state.RequireForUpdate<AttackSkillState>();

        equippedBuffSkillQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<BuffSkillConfig, SkillSlotComponent, EquippedSkillTag, BuffSkillSlotTag>()
            .Build(ref state);
        monsterQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<MonsterTag, LocalTransform, HealthComponent>()
            .Build(ref state);
    }

    public void OnUpdate(ref SystemState state)
    {
        var localToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(true);
        var healthLookup = SystemAPI.GetComponentLookup<HealthComponent>(false);
        var hitVfxLookup = SystemAPI.GetComponentLookup<MonsterHitVfxState>(false);
        var buffSkills = equippedBuffSkillQuery.ToComponentDataArray<BuffSkillConfig>(Allocator.Temp);
        var buffSlots = equippedBuffSkillQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var monsterEntities = monsterQuery.ToEntityArray(Allocator.Temp);
        var monsterTransforms = monsterQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        foreach (var (config, skillState, slot) in
                 SystemAPI.Query<RefRO<AttackSkillConfig>, RefRW<AttackSkillState>, RefRO<SkillSlotComponent>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>())
        {
            if (skillState.ValueRO.IsTriggered == 0)
            {
                continue;
            }

            var owner = slot.ValueRO.Owner;

            if (!localToWorldLookup.HasComponent(owner))
            {
                skillState.ValueRW.IsTriggered = 0;
                continue;
            }

            var ownerPosition = localToWorldLookup[owner].Position;
            var buffs = SkillSystemUtility.CreateBuffAccumulatorForOwner(
                owner,
                buffSkills,
                buffSlots);

            switch (config.ValueRO.LogicId)
            {
                case 0:
                    var random = new Unity.Mathematics.Random(
                        SkillSystemUtility.CreateTargetSelectionSeed(
                            config.ValueRO.Id,
                            ownerPosition,
                            targetSelectionSequence++));

                    _ = SkillSystemUtility.ExecuteTargetCenteredCircleAttack(
                        config.ValueRO,
                        ref skillState.ValueRW,
                        ownerPosition,
                        buffs,
                        ref random,
                        monsterEntities,
                        monsterTransforms,
                        healthLookup,
                        hitVfxLookup);
                    break;
                default:
                    skillState.ValueRW.IsTriggered = 0;
                    break;
            }
        }

        monsterTransforms.Dispose();
        monsterEntities.Dispose();
        buffSlots.Dispose();
        buffSkills.Dispose();
    }
}

/// <summary>
/// Skill system が使う配列ベースの実行 helper。
/// </summary>
public static class SkillSystemUtility
{
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

    public static int ExecuteTargetCenteredCircleAttack(
        AttackSkillConfig config,
        ref AttackSkillState skillState,
        float3 ownerPosition,
        BuffAccumulator buffs,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup)
    {
        var targetRange = SkillMath.CalculateEffectiveTargetRange(config, buffs);

        if (!TryFindDensityBiasedRandomMonster(
                ownerPosition,
                targetRange,
                PlayerCombatConstants.TargetSelectionDirectionBucketCount,
                PlayerCombatConstants.TargetSelectionDistanceWeight,
                ref random,
                monsterEntities,
                monsterTransforms,
                healthLookup,
                out var targetPosition))
        {
            skillState.IsTriggered = 0;
            return 0;
        }

        var attackRange = SkillMath.CalculateEffectiveAttackRange(config, buffs);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        SkillAttackRangeDebugEvents.Record(targetPosition, attackRange);
#endif

        var damage = SkillMath.CalculateDamageToHp(
            SkillMath.CalculateEffectiveDamage(config, skillState, buffs));
        var attackRangeSq = attackRange * attackRange;

        var hitCount = 0;

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
                -damage);
            PlayMonsterHitVfx(monsterEntity, hitVfxLookup);
            hitCount++;
        }

        skillState.IsTriggered = 0;
        skillState.IsCooltime = 1;
        skillState.Timer = 0f;

        return hitCount;
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
