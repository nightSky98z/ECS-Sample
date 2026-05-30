using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

/// <summary>
/// Runtime VFX の見た目更新をまとめて処理する。
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(MonsterDestroySystem))]
[UpdateBefore(typeof(MonsterSimpleAiSystem))]
public partial struct VFXPlaySystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MonsterTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var deltaTime = SystemAPI.Time.DeltaTime;
        var entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

        PlayMonsterHitVfx(ref state, ref entityCommandBuffer, deltaTime);
        PlayMonsterDestroyVfx(ref state, ref entityCommandBuffer, deltaTime);

        entityCommandBuffer.Playback(state.EntityManager);
        entityCommandBuffer.Dispose();
    }

    private void PlayMonsterHitVfx(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        float deltaTime)
    {
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

            var nextElapsedTime = vfxState.ValueRO.ElapsedTime + deltaTime;
            var nextProgress = MonsterHitVfxMath.CalculateProgress(
                nextElapsedTime,
                config.ValueRO.Duration);
            var baseColor = nextProgress >= 1f
                ? config.ValueRO.RestBaseColor
                : MonsterHitVfxMath.CalculateBaseColor(
                    config.ValueRO.HitBaseColor,
                    config.ValueRO.RestBaseColor,
                    MonsterHitVfxMath.CalculateProgress(
                        vfxState.ValueRO.ElapsedTime,
                        config.ValueRO.Duration));

            VFXMaterialUtility.AddOrSetBaseColorForLinkedRenderEntities(
                ref state,
                ref entityCommandBuffer,
                entity,
                baseColor);

            if (nextProgress >= 1f)
            {
                vfxState.ValueRW.ElapsedTime = 0f;
                vfxState.ValueRW.IsPlaying = 0;
                continue;
            }

            vfxState.ValueRW.ElapsedTime = nextElapsedTime;
        }
    }

    private void PlayMonsterDestroyVfx(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        float deltaTime)
    {
        foreach (var (vfxState, config, transform, entity) in
                 SystemAPI.Query<RefRW<MonsterDestroyVfxState>, RefRO<MonsterDestroyVfxConfig>, RefRW<LocalTransform>>()
                     .WithAll<MonsterTag>()
                     .WithEntityAccess())
        {
            var nextElapsedTime = vfxState.ValueRO.ElapsedTime + deltaTime;
            var progress = MonsterDestroyVfxMath.CalculateProgress(
                nextElapsedTime,
                config.ValueRO.Duration);
            var baseColor = MonsterDestroyVfxMath.CalculateBaseColor(
                config.ValueRO.StartBaseColor,
                config.ValueRO.EndBaseColor,
                progress);

            vfxState.ValueRW.ElapsedTime = nextElapsedTime;
            transform.ValueRW.Scale = MonsterDestroyVfxMath.CalculateScale(
                vfxState.ValueRO.OriginalScale,
                config.ValueRO.EndScale,
                progress);

            VFXMaterialUtility.AddOrSetBaseColorForLinkedRenderEntities(
                ref state,
                ref entityCommandBuffer,
                entity,
                baseColor);
        }
    }
}

/// <summary>
/// Entity Graphics の BaseColor override を render Entity に適用する helper。
/// </summary>
public static class VFXMaterialUtility
{
    public static void AddOrSetBaseColorForLinkedRenderEntities(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity rootEntity,
        float4 color)
    {
        if (!state.EntityManager.HasBuffer<LinkedEntityGroup>(rootEntity))
        {
            AddOrSetBaseColorForRenderEntity(ref state, ref entityCommandBuffer, rootEntity, color);
            return;
        }

        var linkedEntities = state.EntityManager.GetBuffer<LinkedEntityGroup>(rootEntity);

        for (var linkedEntityIndex = 0; linkedEntityIndex < linkedEntities.Length; linkedEntityIndex++)
        {
            AddOrSetBaseColorForRenderEntity(
                ref state,
                ref entityCommandBuffer,
                linkedEntities[linkedEntityIndex].Value,
                color);
        }
    }

    private static void AddOrSetBaseColorForRenderEntity(
        ref SystemState state,
        ref EntityCommandBuffer entityCommandBuffer,
        Entity entity,
        float4 color)
    {
        if (!state.EntityManager.HasComponent<MaterialMeshInfo>(entity))
        {
            return;
        }

        var materialColor = new URPMaterialPropertyBaseColor
        {
            Value = color
        };

        if (state.EntityManager.HasComponent<URPMaterialPropertyBaseColor>(entity))
        {
            entityCommandBuffer.SetComponent(entity, materialColor);
            return;
        }

        // Baker 側で override が付かない renderer でも VFX が無音で失敗しないよう、初回だけ追加する。
        entityCommandBuffer.AddComponent(entity, materialColor);
    }
}
