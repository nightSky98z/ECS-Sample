using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;

/// <summary>
/// モンスターの被弾演出と死亡演出の見た目（色・大きさ）を毎フレーム更新する。
/// 色はマテリアルを複製せず、Entity ごとの色の Component（URPMaterialPropertyBaseColor）を書き換えて変える。
/// これにより、大量のモンスターがいても描画のまとめ処理（バッチング）が崩れにくい。
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

    /// <summary>
    /// 被弾演出：被弾した瞬間の色から元の色へ、徐々に戻していく。終わったら元の色にして停止する。
    /// </summary>
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

    /// <summary>
    /// 死亡演出：色を変えながら小さくしていく。演出の終了後の処理（再利用・削除）は MonsterDestroySystem が行う。
    /// </summary>
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
/// 見た目の Entity に色を設定する処理（Entities Graphics の色の上書き機能を使う）。
/// </summary>
public static class VFXMaterialUtility
{
    /// <summary>
    /// ルートの Entity と、それに紐付いた子の Entity のうち、描画されるものすべてに色を設定する。
    /// </summary>
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

    /// <summary>
    /// 描画される Entity に色を設定する。描画されない Entity は無視する。
    /// </summary>
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

        // Bake 時に色の Component が付いていない場合でも演出が効くよう、初回だけ追加する。
        entityCommandBuffer.AddComponent(entity, materialColor);
    }
}
