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
        Assert.AreEqual(6, PlayerCombatConstants.MaxSkillLevel);
        Assert.AreEqual(0.6f, PlayerCombatConstants.TargetSelectionDistanceWeight);
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

        Assert.AreEqual(0, skill.Config.Id);
        Assert.AreEqual(0, skill.Config.LogicId);
        Assert.AreEqual(1, skill.Config.BaseTargetCount);
        Assert.AreEqual(0f, skill.Config.TargetCountLevelWeight);
        Assert.AreEqual(PlayerCombatConstants.MaxAttackCircleCount, skill.Config.MaxTargetCount);
        Assert.AreEqual(SkillCountRoundMode.Floor, skill.Config.TargetCountRoundMode);
        Assert.AreEqual(0, skill.State.IsCooltime);
        Assert.AreEqual(0, skill.State.IsTriggered);
        Assert.AreEqual(0, skill.State.IsCasting);
        Assert.AreEqual(0f, skill.State.Timer);
        Assert.AreEqual(0f, skill.Timing.DamageDelay);
        Assert.AreEqual(0f, skill.Timing.SfxDelay);
        Assert.AreEqual(1f, skill.Timing.SfxVolume);
        Assert.AreEqual(0f, skill.Timing.VfxDelay);
        Assert.AreEqual(1f, skill.Timing.VfxDuration);
        Assert.AreEqual(1f, skill.Timing.VfxPrefabRadius);
        Assert.AreEqual(0f, skill.Timing.VfxDisplayRadius);
        Assert.AreEqual(0, skill.Timing.HasSfx);
        Assert.AreEqual(0, skill.Timing.HasVfx);
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

        Assert.AreEqual(4, skill.Config.Id);
        Assert.AreEqual(25f, skill.Config.BaseDamage);
        Assert.AreEqual(0.75f, skill.Config.Cooltime);
        Assert.AreEqual(18f, skill.Config.BaseTargetRange);
        Assert.AreEqual(6f, skill.Config.BaseAttackRange);
        Assert.AreEqual(2, skill.State.Level);
        Assert.AreEqual(0, skill.Config.LogicId);
        Assert.AreEqual(0, skill.State.IsCooltime);
        Assert.AreEqual(0, skill.State.IsTriggered);
    }

    [Test]
    public void AttackSkillAuthoringUtilityCreatesTimingData()
    {
        var skill = AttackSkillAuthoringUtility.CreateAttackSkill(
            id: 6,
            baseDamage: 30f,
            cooltime: 1.25f,
            baseTargetRange: 24f,
            baseAttackRange: 5f,
            level: 3,
            logicId: 0,
            damageDelay: 0.2f,
            sfxDelay: 0.05f,
            sfxVolume: 0.75f,
            vfxDelay: 0.1f,
            vfxDuration: 0.8f,
            vfxPrefabRadius: 2f,
            vfxDisplayRadius: 6f);

        Assert.AreEqual(0.2f, skill.Timing.DamageDelay);
        Assert.AreEqual(0.05f, skill.Timing.SfxDelay);
        Assert.AreEqual(0.75f, skill.Timing.SfxVolume);
        Assert.AreEqual(0.1f, skill.Timing.VfxDelay);
        Assert.AreEqual(0.8f, skill.Timing.VfxDuration);
        Assert.AreEqual(2f, skill.Timing.VfxPrefabRadius);
        Assert.AreEqual(6f, skill.Timing.VfxDisplayRadius);
        Assert.AreEqual(0, skill.Timing.HasSfx);
        Assert.AreEqual(0, skill.Timing.HasVfx);
        Assert.AreEqual(0, skill.State.IsCasting);
        Assert.AreEqual(0, skill.State.DamageApplied);
        Assert.AreEqual(0, skill.State.SfxPlayed);
        Assert.AreEqual(0, skill.State.VfxSpawned);
    }

    [Test]
    public void SkillPresentationCompletionUsesExplicitPresentationFlags()
    {
        var state = new AttackSkillState
        {
            SfxPlayed = 0,
            VfxSpawned = 0
        };
        var timing = new AttackSkillTimingConfig
        {
            HasSfx = 1,
            HasVfx = 1
        };

        Assert.IsFalse(SkillMath.IsPresentationComplete(state, timing));

        state.SfxPlayed = 1;
        Assert.IsFalse(SkillMath.IsPresentationComplete(state, timing));

        state.VfxSpawned = 1;
        Assert.IsTrue(SkillMath.IsPresentationComplete(state, timing));

        timing.HasSfx = 0;
        timing.HasVfx = 0;
        state.SfxPlayed = 0;
        state.VfxSpawned = 0;
        Assert.IsTrue(SkillMath.IsPresentationComplete(state, timing));
    }

    [Test]
    public void SkillVfxScaleUsesDisplayRadiusWhenConfigured()
    {
        var timing = new AttackSkillTimingConfig
        {
            VfxPrefabRadius = 2f,
            VfxDisplayRadius = 6f
        };

        Assert.AreEqual(3f, SkillMath.CalculateVfxScale(4f, timing), 0.0001f);

        timing.VfxDisplayRadius = 0f;
        Assert.AreEqual(2f, SkillMath.CalculateVfxScale(4f, timing), 0.0001f);

        timing.VfxPrefabRadius = 0f;
        Assert.Greater(SkillMath.CalculateVfxScale(4f, timing), 0f);
    }

    [Test]
    public void AttackSkillMathAppliesLevelAndBuffMultipliers()
    {
        var config = new AttackSkillConfig
        {
            BaseDamage = 10f,
            Cooltime = 2f,
            BaseTargetRange = 20f,
            BaseAttackRange = 3f,
        };
        var skillState = new AttackSkillState
        {
            Level = 3
        };
        var buffs = SkillMath.CreateBuffAccumulator();

        buffs = SkillMath.ApplyBuff(buffs, new BuffSkillConfig
        {
            Multiplier = 2f,
            Target = BuffTargetStatus.Damage
        });
        buffs = SkillMath.ApplyBuff(buffs, new BuffSkillConfig
        {
            Multiplier = 0.5f,
            Target = BuffTargetStatus.Cooltime
        });
        buffs = SkillMath.ApplyBuff(buffs, new BuffSkillConfig
        {
            Multiplier = 1.5f,
            Target = BuffTargetStatus.AttackRange
        });
        buffs = SkillMath.ApplyBuff(buffs, new BuffSkillConfig
        {
            Multiplier = 1.25f,
            Target = BuffTargetStatus.TargetRange
        });

        Assert.AreEqual(24f, SkillMath.CalculateEffectiveDamage(config, skillState, buffs), 0.0001f);
        Assert.AreEqual(36f, SkillMath.CalculateEffectiveDamage(config, skillState, buffs, 1.5f), 0.0001f);
        Assert.AreEqual(1f, SkillMath.CalculateEffectiveCooltime(config, buffs), 0.0001f);
        Assert.AreEqual(25f, SkillMath.CalculateEffectiveTargetRange(config, buffs), 0.0001f);
        Assert.AreEqual(4.5f, SkillMath.CalculateEffectiveAttackRange(config, buffs), 0.0001f);
        Assert.AreEqual(24, SkillMath.CalculateDamageToHp(23.1f));
    }

    [Test]
    public void AttackCircleCountUsesBaseCountAtLevelOne()
    {
        Assert.AreEqual(
            2,
            SkillMath.CalculateAttackCircleCount(
                level: 1,
                baseTargetCount: 2,
                targetCountLevelWeight: 0.5f,
                maxTargetCount: 10,
                roundMode: SkillCountRoundMode.Floor));
    }

    [Test]
    public void AttackCircleCountSupportsRoundingModesAndCap()
    {
        Assert.AreEqual(
            2,
            SkillMath.CalculateAttackCircleCount(
                level: 2,
                baseTargetCount: 1,
                targetCountLevelWeight: 0.5f,
                maxTargetCount: 10,
                roundMode: SkillCountRoundMode.Round));
        Assert.AreEqual(
            1,
            SkillMath.CalculateAttackCircleCount(
                level: 2,
                baseTargetCount: 1,
                targetCountLevelWeight: 0.5f,
                maxTargetCount: 10,
                roundMode: SkillCountRoundMode.Floor));
        Assert.AreEqual(
            2,
            SkillMath.CalculateAttackCircleCount(
                level: 2,
                baseTargetCount: 1,
                targetCountLevelWeight: 0.5f,
                maxTargetCount: 10,
                roundMode: SkillCountRoundMode.Ceil));
        Assert.AreEqual(
            3,
            SkillMath.CalculateAttackCircleCount(
                level: 99,
                baseTargetCount: 1,
                targetCountLevelWeight: 1f,
                maxTargetCount: 3,
                roundMode: SkillCountRoundMode.Floor));
    }

    [Test]
    public void SkillCastTargetStoresMultipleAttackCirclePositions()
    {
        var castTarget = new SkillCastTarget
        {
            AttackRange = 4f,
            Damage = 10
        };

        castTarget.Positions.Add(new float3(1f, 0f, 2f));
        castTarget.Positions.Add(new float3(3f, 0f, 4f));

        Assert.AreEqual(2, castTarget.Positions.Length);
        Assert.AreEqual(new float3(1f, 0f, 2f), castTarget.Positions[0]);
        Assert.AreEqual(new float3(3f, 0f, 4f), castTarget.Positions[1]);
    }

    [Test]
    public void PlayerBakerCreatesAttackAndBuffSlotEntities()
    {
        var source = File.ReadAllText("Assets/Scripts/Player/PlayerEntity.cs");

        StringAssert.Contains("CreateAdditionalEntity", source);
        StringAssert.Contains("DefaultAttackSkillEntity", source);
        StringAssert.Contains("DependsOn(authoring.DefaultAttackSkillEntity)", source);
        StringAssert.Contains("IAttackSkillDefinitionAuthoring", source);
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
        var source = File.ReadAllText("Assets/Scripts/Skills/SkillSystems.cs");
        var recordIndex = source.IndexOf("SkillAttackRangeDebugEvents.Record");
        var hitLoopIndex = source.IndexOf("for (var monsterIndex");

        StringAssert.Contains("EquippedSkillTag", source);
        StringAssert.Contains("AttackSkillSlotTag", source);
        StringAssert.Contains("BuffSkillSlotTag", source);
        StringAssert.Contains("SystemAPI.Query<RefRO<AttackSkillConfig>, RefRO<AttackSkillState>, RefRO<SkillSlotComponent>>", source);
        StringAssert.Contains("AttackSkillConfig", source);
        StringAssert.Contains("AttackSkillTimingConfig", source);
        StringAssert.Contains("AttackSkillState", source);
        StringAssert.Contains("SkillCastTarget", source);
        StringAssert.Contains("ExperienceComponent", source);
        StringAssert.Contains("PlayerLevelStats", source);
        StringAssert.Contains("CalculatePlayerSkillDamageRate", source);
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
        var source = File.ReadAllText("Assets/Scripts/Skills/AttackSkillAuthoring.cs");

        StringAssert.Contains("SkillEntity", source);
        StringAssert.Contains("CreateAttackSkill", source);
        StringAssert.Contains("SfxClip", source);
        StringAssert.Contains("VfxPrefab", source);
        StringAssert.Contains("AttackSkillPresentation", source);
        Assert.IsFalse(source.Contains("AddComponent(entity, definition.State)"));
        Assert.IsFalse(source.Contains("SkillSlotComponent"));
        Assert.IsFalse(source.Contains("EquippedSkillTag"));
        Assert.IsFalse(source.Contains("AttackSkillSlotTag"));
    }

    [Test]
    public void SkillPresentationSystemUsesUnityObjectRefsForAudioAndVfx()
    {
        var source = File.ReadAllText("Assets/Scripts/Skills/SkillPresentationSystem.cs");
        var componentSource = File.ReadAllText("Assets/Scripts/Skills/SkillComponents.cs");

        StringAssert.Contains("AttackSkillPresentation", source);
        StringAssert.Contains("AudioSource.PlayClipAtPoint", source);
        StringAssert.Contains("UnityEngine.Object.Instantiate", source);
        StringAssert.Contains("UnityEngine.Object.Destroy", source);
        StringAssert.Contains("CalculateVfxScale", source);
        StringAssert.Contains("localScale", source);
        StringAssert.Contains("AttackSkillTimingConfig", source);
        StringAssert.Contains("SkillCastTarget", source);
        StringAssert.Contains("UnityObjectRef<AudioClip>", componentSource);
        StringAssert.Contains("UnityObjectRef<GameObject>", componentSource);
    }

    [Test]
    public void DefaultAttackSkillEntityPrefabIsEditableAndAssignedToPlayer()
    {
        var prefabPath = "Assets/Prefab/SkillEntity_DefaultCircleAttack.prefab";
        var skillPrefab = File.ReadAllText(prefabPath);
        var playerPrefab = File.ReadAllText("Assets/Prefab/Player.prefab");

        StringAssert.Contains("SkillEntity_瞬時インパクトSkill", skillPrefab);
        StringAssert.Contains("InstantImpactSkillAuthoring", skillPrefab);
        StringAssert.Contains("BaseTargetCount", skillPrefab);
        StringAssert.Contains("TargetCountLevelWeight", skillPrefab);
        StringAssert.Contains("MaxTargetCount", skillPrefab);
        StringAssert.Contains("TargetCountRoundMode", skillPrefab);
        StringAssert.Contains("VfxPrefabRadius", skillPrefab);
        StringAssert.Contains("VfxDisplayRadius", skillPrefab);
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
