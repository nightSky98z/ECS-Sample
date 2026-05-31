using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// 生成したい Prefab Entity への一回分の要求。
/// </summary>
public struct SpawnRequest : IBufferElementData
{
    /// <summary> 生成する Prefab Entity。 </summary>
    public Entity Prefab;

    /// <summary> 生成時に設定する local transform。 </summary>
    public LocalTransform Transform;
}

/// <summary>
/// Scene 上の設定から、初期配置用の SpawnRequest を Bake する Authoring。
/// </summary>
public partial class EntitySpawnerAuthoring : MonoBehaviour
{
    [SerializeField]
    [Tooltip("生成するプレハブ。プレイヤー、モンスター、弾、エフェクトなど、SpawnRequest がインスタンス化する対象。")]
    private GameObject SpawnEntityPrefab;

    [SerializeField]
    [Min(0)]
    [Tooltip("このスポナーから作る SpawnRequest の数。0 は生成なし。RandomizePosition 無効時は最大 1。")]
    private int SpawnCount = 1;

    [SerializeField]
    [Tooltip("有効にすると、スポナーの位置を中心に SpawnAreaSize の範囲へランダム配置する。")]
    private bool RandomizePosition;

    [SerializeField]
    [Tooltip("ランダム配置に使うシード。0 は Unity.Mathematics.Random で使えないため 1 として扱う。")]
    private int RandomSeed = 1;

    [SerializeField]
    [Tooltip("ランダム配置の X/Z 範囲。Y はスポナーの高さをそのまま使う。")]
    private Vector2 SpawnAreaSize = new Vector2(20f, 20f);

    /// <summary>
    /// Authoring GameObject から、EntitySpawnSystem が消費する SpawnRequest buffer を作る。
    /// </summary>
    class Baking : Unity.Entities.Baker<EntitySpawnerAuthoring>
    {
        public override void Bake(EntitySpawnerAuthoring authoring)
        {
            var spawnCount = SpawnTransformUtility.GetEffectiveSpawnCount(
                authoring.SpawnCount,
                authoring.RandomizePosition);

            if (authoring.SpawnEntityPrefab == null || spawnCount <= 0)
            {
                return;
            }

            var prefab = GetEntity(authoring.SpawnEntityPrefab, TransformUsageFlags.Dynamic);
            var entity = GetEntity(TransformUsageFlags.None);
            var spawnRequests = AddBuffer<SpawnRequest>(entity);
            var rotation = authoring.transform.rotation;
            var baseRotation = new quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            // LocalTransform は uniform scale のみ保持する。非 uniform scale が必要なら PostTransformMatrix を使う。
            var uniformScale = authoring.transform.lossyScale.x;
            var center = (float3)authoring.transform.position;
            var areaSize = new float2(authoring.SpawnAreaSize.x, authoring.SpawnAreaSize.y);
            var random = new Unity.Mathematics.Random(SpawnTransformUtility.NormalizeSeed(authoring.RandomSeed));

            for (var spawnIndex = 0; spawnIndex < spawnCount; spawnIndex++)
            {
                var position = SpawnTransformUtility.CreateSpawnPosition(
                    center,
                    authoring.RandomizePosition,
                    areaSize,
                    ref random);

                var spawnRequest = new SpawnRequest
                {
                    Prefab = prefab,
                    Transform = LocalTransform.FromPositionRotationScale(
                        position,
                        baseRotation,
                        uniformScale)
                };

                spawnRequests.Add(spawnRequest);
            }
        }
    }
}

/// <summary>
/// SpawnRequest 作成と Editor preview が共有する座標計算。
/// </summary>
public static class SpawnTransformUtility
{
    /// <summary>
    /// RandomizePosition の有無から、実際に Bake / preview する生成数を返す。
    /// </summary>
    /// <param name="spawnCount">Authoring が保持する要求数。0 以下は生成なし。</param>
    /// <param name="randomizePosition">true の場合だけ複数生成を許可する。</param>
    /// <returns>実際に生成する SpawnRequest 数。</returns>
    public static int GetEffectiveSpawnCount(int spawnCount, bool randomizePosition)
    {
        if (spawnCount <= 0)
        {
            return 0;
        }

        if (!randomizePosition)
        {
            return 1;
        }

        return spawnCount;
    }

    /// <summary>
    /// Unity.Mathematics.Random が受け付ける非ゼロ seed に正規化する。
    /// </summary>
    public static uint NormalizeSeed(int seed)
    {
        if (seed == 0)
        {
            return 1u;
        }

        return (uint)seed;
    }

    /// <summary>
    /// Spawner の中心位置と random 設定から、1体分の spawn 位置を返す。
    /// </summary>
    public static float3 CreateSpawnPosition(
        float3 center,
        bool randomizePosition,
        float2 areaSize,
        ref Unity.Mathematics.Random random)
    {
        if (!randomizePosition)
        {
            return center;
        }

        var halfAreaSize = math.abs(areaSize) * 0.5f;
        var offset = random.NextFloat2(-halfAreaSize, halfAreaSize);

        return center + new float3(offset.x, 0f, offset.y);
    }
}
