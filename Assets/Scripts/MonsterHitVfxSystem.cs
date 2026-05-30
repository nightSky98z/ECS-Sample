using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;

/// <summary>
/// 被弾した Monster の material flash を再生する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SkillLogicSystem))]
[UpdateBefore(typeof(MonsterDestroySystem))]
public partial struct MonsterHitVfxSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterHitVfxConfig>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var deltaTime = SystemAPI.Time.DeltaTime;
        var commandBuffer = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (vfxState, config, entity) in
                 SystemAPI.Query<RefRW<MonsterHitVfxState>, RefRO<MonsterHitVfxConfig>>()
                     .WithAll<MonsterTag>()
                     .WithNone<MonsterDestroyVfxState>()
                     .WithEntityAccess())
        {
            if (vfxState.ValueRO.IsPlaying == 0)
            {
                continue;
            }

            var progress = MonsterHitVfxMath.CalculateProgress(
                vfxState.ValueRO.ElapsedTime,
                config.ValueRO.Duration);
            var baseColor = MonsterHitVfxMath.CalculateBaseColor(
                config.ValueRO.HitBaseColor,
                config.ValueRO.RestBaseColor,
                progress);

            vfxState.ValueRW.ElapsedTime = vfxState.ValueRO.ElapsedTime + deltaTime;
            AddOrSetMaterialColorForLinkedRenderEntities(
                ref state,
                ref commandBuffer,
                entity,
                new URPMaterialPropertyBaseColor
                {
                    Value = baseColor
                });

            if (progress < 1f)
            {
                continue;
            }

            vfxState.ValueRW.ElapsedTime = 0f;
            vfxState.ValueRW.IsPlaying = 0;
        }

        commandBuffer.Playback(state.EntityManager);
        commandBuffer.Dispose();
    }

    private void AddOrSetMaterialColorForLinkedRenderEntities(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity rootEntity,
        URPMaterialPropertyBaseColor color)
    {
        if (!state.EntityManager.HasBuffer<LinkedEntityGroup>(rootEntity))
        {
            AddOrSetMaterialColorForRenderEntity(ref state, ref entityCommandBuffer, rootEntity, color);
            return;
        }

        var linkedEntities = state.EntityManager.GetBuffer<LinkedEntityGroup>(rootEntity);

        for (var linkedEntityIndex = 0; linkedEntityIndex < linkedEntities.Length; linkedEntityIndex++)
        {
            AddOrSetMaterialColorForRenderEntity(
                ref state,
                ref entityCommandBuffer,
                linkedEntities[linkedEntityIndex].Value,
                color);
        }
    }

    private void AddOrSetMaterialColorForRenderEntity(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity entity,
        URPMaterialPropertyBaseColor color)
    {
        if (!state.EntityManager.HasComponent<MaterialMeshInfo>(entity) ||
            !state.EntityManager.HasComponent<URPMaterialPropertyBaseColor>(entity))
        {
            return;
        }

        entityCommandBuffer.SetComponent(entity, color);
    }
}

/// <summary>
/// Monster hit VFX の時間と material color 計算。
/// </summary>
public static class MonsterHitVfxMath
{
    public static float NormalizeDuration(float duration)
    {
        return math.max(0.01f, math.abs(duration));
    }

    public static float CalculateProgress(float elapsedTime, float duration)
    {
        return math.clamp(elapsedTime / NormalizeDuration(duration), 0f, 1f);
    }

    public static float4 CalculateBaseColor(float4 hitColor, float4 restColor, float progress)
    {
        return math.lerp(hitColor, restColor, math.clamp(progress, 0f, 1f));
    }
}
