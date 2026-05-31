using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Attack skill の SFX / VFX をメインスレッドで再生する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(PlayerCombatSystem))]
[UpdateBefore(typeof(SkillLogicSystem))]
public partial class SkillPresentationSystem : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<AttackSkillState>();
        RequireForUpdate<AttackSkillTimingConfig>();
        RequireForUpdate<SkillCastTarget>();
        RequireForUpdate<AttackSkillPresentation>();
    }

    protected override void OnUpdate()
    {
        foreach (var (skillState, timing, castTarget, presentation) in
                 SystemAPI.Query<RefRW<AttackSkillState>, RefRO<AttackSkillTimingConfig>, RefRO<SkillCastTarget>, RefRO<AttackSkillPresentation>>()
                     .WithAll<EquippedSkillTag, AttackSkillSlotTag>())
        {
            if (skillState.ValueRO.IsCasting == 0)
            {
                continue;
            }

            TryPlaySfx(skillState, timing.ValueRO, castTarget.ValueRO, presentation.ValueRO);
            TrySpawnVfx(skillState, timing.ValueRO, castTarget.ValueRO, presentation.ValueRO);
        }
    }

    private static void TryPlaySfx(
        RefRW<AttackSkillState> skillState,
        AttackSkillTimingConfig timing,
        SkillCastTarget castTarget,
        AttackSkillPresentation presentation)
    {
        var sfxClip = presentation.SfxClip.Value;

        if (skillState.ValueRO.SfxPlayed != 0 ||
            timing.HasSfx == 0 ||
            !SkillMath.IsDelayReached(skillState.ValueRO.CastElapsedTime, timing.SfxDelay))
        {
            return;
        }

        if (sfxClip == null)
        {
            skillState.ValueRW.SfxPlayed = 1;
            return;
        }

        if (castTarget.Positions.Length == 0)
        {
            skillState.ValueRW.SfxPlayed = 1;
            return;
        }

        AudioSource.PlayClipAtPoint(
            sfxClip,
            ToVector3(castTarget.Positions[0]),
            math.max(0f, timing.SfxVolume));
        skillState.ValueRW.SfxPlayed = 1;
    }

    private static void TrySpawnVfx(
        RefRW<AttackSkillState> skillState,
        AttackSkillTimingConfig timing,
        SkillCastTarget castTarget,
        AttackSkillPresentation presentation)
    {
        var vfxPrefab = presentation.VfxPrefab.Value;

        if (skillState.ValueRO.VfxSpawned != 0 ||
            timing.HasVfx == 0 ||
            !SkillMath.IsDelayReached(skillState.ValueRO.CastElapsedTime, timing.VfxDelay))
        {
            return;
        }

        if (vfxPrefab == null)
        {
            skillState.ValueRW.VfxSpawned = 1;
            return;
        }

        var vfxScale = SkillMath.CalculateVfxScale(castTarget.AttackRange, timing);
        var vfxDuration = math.max(0f, timing.VfxDuration);

        for (var targetIndex = 0; targetIndex < castTarget.Positions.Length; targetIndex++)
        {
            var vfx = UnityEngine.Object.Instantiate(
                vfxPrefab,
                ToVector3(castTarget.Positions[targetIndex]),
                Quaternion.identity);

            vfx.transform.localScale *= vfxScale;
            UnityEngine.Object.Destroy(vfx, vfxDuration);
        }

        skillState.ValueRW.VfxSpawned = 1;
    }

    private static Vector3 ToVector3(float3 value)
    {
        return new Vector3(value.x, value.y, value.z);
    }
}
