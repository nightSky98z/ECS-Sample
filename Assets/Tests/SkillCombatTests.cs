using NUnit.Framework;
using Unity.Collections;
using System.IO;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

public sealed class SkillCombatTests
{
    [Test]
    public void PlayerCombatConstantsKeepSlotCountsExplicit()
    {
        Assert.AreEqual(5, PlayerCombatConstants.MaxAttackSkillCount);
        Assert.AreEqual(5, PlayerCombatConstants.MaxBuffSkillCount);
        Assert.AreEqual(10, PlayerCombatConstants.MaxSkillCount);
    }

    [Test]
    public void DefaultAttackSkillStartsReadyAndUsesLogicZero()
    {
        var skill = SkillDefaults.CreateDefaultAttackSkill(
            0,
            12f,
            1.5f,
            30f,
            4f,
            1,
            0);

        Assert.AreEqual(0, skill.Id);
        Assert.AreEqual(0, skill.LogicId);
        Assert.AreEqual(0, skill.IsCooltime);
        Assert.AreEqual(0, skill.IsTriggered);
        Assert.AreEqual(0f, skill.Timer);
    }

    [Test]
    public void AttackSkillAuthoringUtilityCreatesEditableSkillData()
    {
        var skill = AttackSkillAuthoringUtility.CreateAttackSkill(
            id: 4,
            baseDamage: 25f,
            cooltime: 0.75f,
            baseTargetRange: 18f,
            baseAttackRange: 6f,
            level: 2,
            logicId: 0);

        Assert.AreEqual(4, skill.Id);
        Assert.AreEqual(25f, skill.BaseDamage);
        Assert.AreEqual(0.75f, skill.Cooltime);
        Assert.AreEqual(18f, skill.BaseTargetRange);
        Assert.AreEqual(6f, skill.BaseAttackRange);
        Assert.AreEqual(2, skill.Level);
        Assert.AreEqual(0, skill.LogicId);
        Assert.AreEqual(0, skill.IsCooltime);
        Assert.AreEqual(0, skill.IsTriggered);
    }

    [Test]
    public void AttackSkillMathAppliesLevelAndBuffMultipliers()
    {
        var skill = new AttackSkillComponent
        {
            BaseDamage = 10f,
            Cooltime = 2f,
            BaseTargetRange = 20f,
            BaseAttackRange = 3f,
            Level = 3
        };
        var buffs = SkillMath.CreateBuffAccumulator();

        buffs = SkillMath.ApplyBuff(buffs, new BuffSkillComponent
        {
            Multiplier = 2f,
            Target = BuffTargetStatus.Damage
        });
        buffs = SkillMath.ApplyBuff(buffs, new BuffSkillComponent
        {
            Multiplier = 0.5f,
            Target = BuffTargetStatus.Cooltime
        });
        buffs = SkillMath.ApplyBuff(buffs, new BuffSkillComponent
        {
            Multiplier = 1.5f,
            Target = BuffTargetStatus.AttackRange
        });
        buffs = SkillMath.ApplyBuff(buffs, new BuffSkillComponent
        {
            Multiplier = 1.25f,
            Target = BuffTargetStatus.TargetRange
        });

        Assert.AreEqual(24f, SkillMath.CalculateEffectiveDamage(skill, buffs), 0.0001f);
        Assert.AreEqual(1f, SkillMath.CalculateEffectiveCooltime(skill, buffs), 0.0001f);
        Assert.AreEqual(25f, SkillMath.CalculateEffectiveTargetRange(skill, buffs), 0.0001f);
        Assert.AreEqual(4.5f, SkillMath.CalculateEffectiveAttackRange(skill, buffs), 0.0001f);
        Assert.AreEqual(24, SkillMath.CalculateDamageToHp(23.1f));
    }

    [Test]
    public void PlayerBakerCreatesAttackAndBuffSlotEntities()
    {
        var source = File.ReadAllText("Assets/Scripts/PlayerEntity.cs");

        StringAssert.Contains("CreateAdditionalEntity", source);
        StringAssert.Contains("DefaultAttackSkillEntity", source);
        StringAssert.Contains("CreateDefaultAttackSkill", source);
        StringAssert.Contains("PlayerCombatConstants.MaxAttackSkillCount", source);
        StringAssert.Contains("PlayerCombatConstants.MaxBuffSkillCount", source);
        StringAssert.Contains("AttackSkillSlotTag", source);
        StringAssert.Contains("BuffSkillSlotTag", source);
        StringAssert.Contains("EquippedSkillTag", source);
    }

    [Test]
    public void SkillSystemsOperateOnEquippedSlotsAndDirectHealth()
    {
        var source = File.ReadAllText("Assets/Scripts/SkillSystems.cs");
        var recordIndex = source.IndexOf("SkillAttackRangeDebugEvents.Record");
        var hitLoopIndex = source.IndexOf("for (var monsterIndex");

        StringAssert.Contains("EquippedSkillTag", source);
        StringAssert.Contains("AttackSkillSlotTag", source);
        StringAssert.Contains("BuffSkillSlotTag", source);
        StringAssert.Contains("HealthMath.ApplyHealthDelta", source);
        StringAssert.Contains("MonsterHitVfxState", source);
        StringAssert.Contains("hitCount", source);
        StringAssert.Contains("return hitCount", source);
        StringAssert.Contains("MonsterTag", source);
        Assert.GreaterOrEqual(recordIndex, 0);
        Assert.Greater(hitLoopIndex, recordIndex);
        Assert.IsFalse(source.Contains("EntityCommandBuffer"));
        Assert.IsFalse(source.Contains("Request"));
    }

    [Test]
    public void SkillEntityDefinitionIsNotASlot()
    {
        var source = File.ReadAllText("Assets/Scripts/AttackSkillAuthoring.cs");

        StringAssert.Contains("SkillEntity", source);
        StringAssert.Contains("CreateAttackSkill", source);
        Assert.IsFalse(source.Contains("SkillSlotComponent"));
        Assert.IsFalse(source.Contains("EquippedSkillTag"));
        Assert.IsFalse(source.Contains("AttackSkillSlotTag"));
    }

    [Test]
    public void DefaultAttackSkillEntityPrefabIsEditableAndAssignedToPlayer()
    {
        var prefabPath = "Assets/Prefab/SkillEntity_DefaultCircleAttack.prefab";
        var skillPrefab = File.ReadAllText(prefabPath);
        var playerPrefab = File.ReadAllText("Assets/Prefab/Player.prefab");

        StringAssert.Contains("SkillEntity_DefaultCircleAttack", skillPrefab);
        StringAssert.Contains("AttackSkillAuthoring", skillPrefab);
        StringAssert.Contains("BaseDamage", skillPrefab);
        StringAssert.Contains("DefaultAttackSkillEntity", playerPrefab);
        StringAssert.Contains("e36a6cb0231c4d948b5e88aa9102c316", playerPrefab);
    }

    [Test]
    public void SkillSlotKeepsOwnerAndSlotIndexAsData()
    {
        var slot = new SkillSlotComponent
        {
            Owner = Entity.Null,
            SlotIndex = 4
        };

        Assert.AreEqual(Entity.Null, slot.Owner);
        Assert.AreEqual(4, slot.SlotIndex);
    }

    [Test]
    public void DensityBiasedTargetSelectionPrefersDirectionWithMoreMonsters()
    {
        var ownerPosition = float3.zero;
        var monsterEntities = new NativeArray<Entity>(6, Allocator.Temp);
        var monsterTransforms = new NativeArray<LocalTransform>(6, Allocator.Temp);
        var monsterHealths = new NativeArray<HealthComponent>(6, Allocator.Temp);

        monsterEntities[0] = Entity.Null;
        monsterTransforms[0] = LocalTransform.FromPosition(new float3(2f, 0f, 0f));
        monsterHealths[0] = HealthMath.CreateHealth(10, 10);

        for (var monsterIndex = 1; monsterIndex < monsterEntities.Length; monsterIndex++)
        {
            monsterEntities[monsterIndex] = Entity.Null;
            monsterTransforms[monsterIndex] = LocalTransform.FromPosition(new float3(
                0f,
                0f,
                8f + monsterIndex * 0.15f));
            monsterHealths[monsterIndex] = HealthMath.CreateHealth(10, 10);
        }

        var denseDirectionHitCount = 0;

        for (uint seed = 1; seed <= 64; seed++)
        {
            var random = new Unity.Mathematics.Random(seed);

            Assert.IsTrue(SkillSystemUtility.TryFindDensityBiasedRandomMonster(
                ownerPosition,
                targetRange: 30f,
                directionBucketCount: 8,
                distanceWeight: 0.35f,
                ref random,
                monsterEntities,
                monsterTransforms,
                monsterHealths,
                out var targetPosition));

            if (targetPosition.z > 0f)
            {
                denseDirectionHitCount++;
            }
        }

        Assert.Greater(denseDirectionHitCount, 48);

        monsterHealths.Dispose();
        monsterTransforms.Dispose();
        monsterEntities.Dispose();
    }

    [Test]
    public void DensityBiasedTargetSelectionUsesDistanceAsSecondaryWeight()
    {
        var ownerPosition = float3.zero;
        var monsterEntities = new NativeArray<Entity>(4, Allocator.Temp);
        var monsterTransforms = new NativeArray<LocalTransform>(4, Allocator.Temp);
        var monsterHealths = new NativeArray<HealthComponent>(4, Allocator.Temp);

        monsterEntities[0] = Entity.Null;
        monsterTransforms[0] = LocalTransform.FromPosition(new float3(4f, 0f, 0f));
        monsterHealths[0] = HealthMath.CreateHealth(10, 10);
        monsterEntities[1] = Entity.Null;
        monsterTransforms[1] = LocalTransform.FromPosition(new float3(5f, 0f, 0f));
        monsterHealths[1] = HealthMath.CreateHealth(10, 10);
        monsterEntities[2] = Entity.Null;
        monsterTransforms[2] = LocalTransform.FromPosition(new float3(-24f, 0f, 0f));
        monsterHealths[2] = HealthMath.CreateHealth(10, 10);
        monsterEntities[3] = Entity.Null;
        monsterTransforms[3] = LocalTransform.FromPosition(new float3(-25f, 0f, 0f));
        monsterHealths[3] = HealthMath.CreateHealth(10, 10);

        var nearDirectionHitCount = 0;

        for (uint seed = 1; seed <= 96; seed++)
        {
            var random = new Unity.Mathematics.Random(seed);

            Assert.IsTrue(SkillSystemUtility.TryFindDensityBiasedRandomMonster(
                ownerPosition,
                targetRange: 30f,
                directionBucketCount: 4,
                distanceWeight: 2f,
                ref random,
                monsterEntities,
                monsterTransforms,
                monsterHealths,
                out var targetPosition));

            if (targetPosition.x > 0f)
            {
                nearDirectionHitCount++;
            }
        }

        Assert.Greater(nearDirectionHitCount, 56);

        monsterHealths.Dispose();
        monsterTransforms.Dispose();
        monsterEntities.Dispose();
    }
}
