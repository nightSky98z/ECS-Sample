using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 攻撃スキルの SFX と VFX を再生する。
/// AudioSource や GameObject（Managed オブジェクト）を扱うため、Burst を使わない SystemBase で実装し、
/// ダメージ計算などの Burst 対応の System とは分けている。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillLogicSystem))]
[UpdateBefore(typeof(SkillCastCompletionSystem))]
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
        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (!StageRuntimeUtility.IsGameplayPhase(runtimeState.ValueRO.Phase))
            {
                return;
            }
        }

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

    /// <summary>
    /// SFX の再生タイミングに達していれば、最初の攻撃範囲の位置で再生する（1 回の発動につき 1 回）。
    /// </summary>
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

        // 再生できない場合も「再生済み」にして、スキルがクールタイムに移れなくなるのを防ぐ。
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

    /// <summary>
    /// VFX の生成タイミングに達していれば、攻撃範囲ごとに VFX を生成する（一定時間後に自動で削除される）。
    /// </summary>
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

        var vfxDuration = math.max(0f, timing.VfxDuration);

        for (var targetIndex = 0; targetIndex < castTarget.Positions.Length; targetIndex++)
        {
            // ダメージ判定はデータ（SkillCastTarget）で行い、VFX は見た目の大きさを攻撃範囲に合わせるだけ。
            var vfxScale = SkillMath.CalculateVfxScale(
                SkillCastTargetUtility.GetAttackRange(castTarget, targetIndex),
                timing);
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
