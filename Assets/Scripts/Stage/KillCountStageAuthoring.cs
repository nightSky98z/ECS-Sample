using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// SubScene に置く討伐数ステージ設定。
/// </summary>
public sealed class KillCountStageAuthoring : MonoBehaviour
{
    [SerializeField]
    [Min(0)]
    [Tooltip("現在の討伐数がこの値に到達するとステージクリアになる。0 は自動クリア無効。")]
    private int TargetKillCount = 100;

    [SerializeField]
    [Min(0)]
    [Tooltip("初期討伐数。")]
    private int InitialKillCount = 0;

    private void OnValidate()
    {
        TargetKillCount = math.max(0, TargetKillCount);
        InitialKillCount = math.max(0, InitialKillCount);
    }

    private sealed class Baker : Baker<KillCountStageAuthoring>
    {
        public override void Bake(KillCountStageAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var currentKillCount = math.max(0, authoring.InitialKillCount);
            var targetKillCount = math.max(0, authoring.TargetKillCount);

            AddComponent(entity, new KillCountStageProgress
            {
                TargetKillCount = targetKillCount,
                CurrentKillCount = currentKillCount
            });
            AddComponent(entity, new StageSpawnProgress
            {
                Value = StageProgressMath.CalculateKillCountProgress(
                    currentKillCount,
                    targetKillCount)
            });
            AddComponent(entity, new StageClearState
            {
                IsCleared = StageProgressMath.IsKillCountStageCleared(
                    currentKillCount,
                    targetKillCount) ? (byte)1 : (byte)0
            });
        }
    }
}
