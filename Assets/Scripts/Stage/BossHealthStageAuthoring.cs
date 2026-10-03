using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 「ボスを倒すとクリア」のステージの設定。サブシーンに置く。
/// </summary>
public sealed class BossHealthStageAuthoring : MonoBehaviour
{
    [SerializeField]
    [Tooltip("任意のボス GameObject。未指定の場合、別のシステムが後で BossEntity を書き込める。")]
    private GameObject Boss;

    [SerializeField]
    [Min(1)]
    [Tooltip("進行度計算に使うボス最大 HP。")]
    private int MaxHp = 1000;

    [SerializeField]
    [Min(0)]
    [Tooltip("初期残り HP。")]
    private int InitialCurrentHp = 1000;

    /// <summary>
    /// Inspector で入力された値を、有効な範囲に収める。
    /// </summary>
    private void OnValidate()
    {
        MaxHp = math.max(1, MaxHp);
        InitialCurrentHp = math.clamp(InitialCurrentHp, 0, MaxHp);
    }

    private sealed class Baker : Baker<BossHealthStageAuthoring>
    {
        /// <summary>
        /// ボスの HP の状態・進み具合・クリア状態の Component を追加する。
        /// </summary>
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
