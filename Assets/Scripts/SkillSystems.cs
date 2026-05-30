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
        state.RequireForUpdate<AttackSkillComponent>();

        equippedBuffSkillQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<BuffSkillComponent, SkillSlotComponent, EquippedSkillTag, BuffSkillSlotTag>()
            .Build(ref state);
    }

    public void OnUpdate(ref SystemState state)
    {
        var deltaTime = SystemAPI.Time.DeltaTime;
        var buffSkills = equippedBuffSkillQuery.ToComponentDataArray<BuffSkillComponent>(Allocator.Temp);
        var buffSlots = equippedBuffSkillQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);

        foreach (var (skill, slot) in
                 SystemAPI.Query<RefRW<AttackSkillComponent>, RefRO<SkillSlotComponent>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>())
        {
            if (skill.ValueRO.IsCooltime == 0)
            {
                continue;
            }

            var buffs = SkillSystemUtility.CreateBuffAccumulatorForOwner(
                slot.ValueRO.Owner,
                buffSkills,
                buffSlots);
            var cooltime = SkillMath.CalculateEffectiveCooltime(skill.ValueRO, buffs);
            var nextTimer = skill.ValueRO.Timer + deltaTime;

            if (nextTimer < cooltime)
            {
                skill.ValueRW.Timer = nextTimer;
                continue;
            }

            skill.ValueRW.Timer = 0f;
            skill.ValueRW.IsCooltime = 0;
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
        state.RequireForUpdate<AttackSkillComponent>();
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

        var skillLookup = SystemAPI.GetComponentLookup<AttackSkillComponent>(false);
        var selectedSkillEntity = Entity.Null;
        var selectedSlotIndex = int.MaxValue;

        foreach (var (skill, slot, skillEntity) in
                 SystemAPI.Query<RefRO<AttackSkillComponent>, RefRO<SkillSlotComponent>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>()
                     .WithEntityAccess())
        {
            if (slot.ValueRO.Owner != playerEntity ||
                skill.ValueRO.IsCooltime != 0 ||
                skill.ValueRO.IsTriggered != 0 ||
                slot.ValueRO.SlotIndex >= selectedSlotIndex)
            {
                continue;
            }

            selectedSkillEntity = skillEntity;
            selectedSlotIndex = slot.ValueRO.SlotIndex;
        }

        if (selectedSkillEntity != Entity.Null)
        {
            var selectedSkill = skillLookup[selectedSkillEntity];

            selectedSkill.IsTriggered = 1;
            skillLookup[selectedSkillEntity] = selectedSkill;
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
        state.RequireForUpdate<AttackSkillComponent>();

        equippedBuffSkillQuery = new EntityQueryBuilder(Allocator.Temp)
            .WithAll<BuffSkillComponent, SkillSlotComponent, EquippedSkillTag, BuffSkillSlotTag>()
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
        var buffSkills = equippedBuffSkillQuery.ToComponentDataArray<BuffSkillComponent>(Allocator.Temp);
        var buffSlots = equippedBuffSkillQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var monsterEntities = monsterQuery.ToEntityArray(Allocator.Temp);
        var monsterTransforms = monsterQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        foreach (var (skill, slot) in
                 SystemAPI.Query<RefRW<AttackSkillComponent>, RefRO<SkillSlotComponent>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>())
        {
            if (skill.ValueRO.IsTriggered == 0)
            {
                continue;
            }

            var owner = slot.ValueRO.Owner;

            if (!localToWorldLookup.HasComponent(owner))
            {
                skill.ValueRW.IsTriggered = 0;
                continue;
            }

            var ownerPosition = localToWorldLookup[owner].Position;
            var buffs = SkillSystemUtility.CreateBuffAccumulatorForOwner(
                owner,
                buffSkills,
                buffSlots);

            switch (skill.ValueRO.LogicId)
            {
                case 0:
                    var random = new Unity.Mathematics.Random(
                        SkillSystemUtility.CreateTargetSelectionSeed(
                            skill.ValueRO.Id,
                            ownerPosition,
                            targetSelectionSequence++));

                    _ = SkillSystemUtility.ExecuteTargetCenteredCircleAttack(
                        ref skill.ValueRW,
                        ownerPosition,
                        buffs,
                        ref random,
                        monsterEntities,
                        monsterTransforms,
                        healthLookup,
                        hitVfxLookup);
                    break;
                default:
                    skill.ValueRW.IsTriggered = 0;
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
        NativeArray<BuffSkillComponent> buffSkills,
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
        ref AttackSkillComponent skill,
        float3 ownerPosition,
        BuffAccumulator buffs,
        ref Unity.Mathematics.Random random,
        NativeArray<Entity> monsterEntities,
        NativeArray<LocalTransform> monsterTransforms,
        ComponentLookup<HealthComponent> healthLookup,
        ComponentLookup<MonsterHitVfxState> hitVfxLookup)
    {
        var targetRange = SkillMath.CalculateEffectiveTargetRange(skill, buffs);

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
            skill.IsTriggered = 0;
            return 0;
        }

        var attackRange = SkillMath.CalculateEffectiveAttackRange(skill, buffs);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        SkillAttackRangeDebugEvents.Record(targetPosition, attackRange);
#endif

        var damage = SkillMath.CalculateDamageToHp(
            SkillMath.CalculateEffectiveDamage(skill, buffs));
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

        skill.IsTriggered = 0;
        skill.IsCooltime = 1;
        skill.Timer = 0f;

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
