using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// SubScene に置く生存ステージ設定。
/// </summary>
public sealed class TimedSurvivalStageAuthoring : MonoBehaviour
{
    [SerializeField]
    [Min(0f)]
    [Tooltip("経過秒数がこの値に到達するとステージクリアになる。0 は自動クリア無効。")]
    private float TimeLimitSeconds = 300f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("初期経過秒数。")]
    private float InitialElapsedSeconds = 0f;

    private void OnValidate()
    {
        TimeLimitSeconds = math.max(0f, TimeLimitSeconds);
        InitialElapsedSeconds = math.max(0f, InitialElapsedSeconds);
    }

    private sealed class Baker : Baker<TimedSurvivalStageAuthoring>
    {
        public override void Bake(TimedSurvivalStageAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var elapsedSeconds = math.max(0f, authoring.InitialElapsedSeconds);
            var timeLimitSeconds = math.max(0f, authoring.TimeLimitSeconds);

            AddComponent(entity, new TimedSurvivalStageProgress
            {
                TimeLimitSeconds = timeLimitSeconds,
                ElapsedSeconds = elapsedSeconds
            });
            AddComponent(entity, new StageSpawnProgress
            {
                Value = StageProgressMath.CalculateTimedSurvivalProgress(
                    elapsedSeconds,
                    timeLimitSeconds)
            });
            AddComponent(entity, new StageClearState
            {
                IsCleared = StageProgressMath.IsTimedSurvivalStageCleared(
                    elapsedSeconds,
                    timeLimitSeconds) ? (byte)1 : (byte)0
            });
        }
    }
}
