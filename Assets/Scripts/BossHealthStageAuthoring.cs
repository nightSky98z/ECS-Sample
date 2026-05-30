using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// SubScene に置く Boss ステージ設定。
/// </summary>
public sealed class BossHealthStageAuthoring : MonoBehaviour
{
    [SerializeField]
    [Tooltip("Optional boss GameObject. If omitted, another system can write BossEntity later.")]
    private GameObject Boss;

    [SerializeField]
    [Min(1)]
    [Tooltip("Boss max HP used to calculate progress.")]
    private int MaxHp = 1000;

    [SerializeField]
    [Min(0)]
    [Tooltip("Initial remaining HP.")]
    private int InitialCurrentHp = 1000;

    private void OnValidate()
    {
        MaxHp = math.max(1, MaxHp);
        InitialCurrentHp = math.clamp(InitialCurrentHp, 0, MaxHp);
    }

    private sealed class Baker : Baker<BossHealthStageAuthoring>
    {
        public override void Bake(BossHealthStageAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            var maxHp = math.max(1, authoring.MaxHp);
            var currentHp = math.clamp(authoring.InitialCurrentHp, 0, maxHp);

            AddComponent(entity, new BossHealthStageProgress
            {
                BossEntity = authoring.Boss == null
                    ? Entity.Null
                    : GetEntity(authoring.Boss, TransformUsageFlags.Dynamic),
                MaxHp = maxHp,
                CurrentHp = currentHp
            });
            AddComponent(entity, new StageSpawnProgress
            {
                Value = StageProgressMath.CalculateBossHealthProgress(currentHp, maxHp)
            });
            AddComponent(entity, new StageClearState
            {
                IsCleared = StageProgressMath.IsBossStageCleared(currentHp) ? (byte)1 : (byte)0
            });
        }
    }
}
