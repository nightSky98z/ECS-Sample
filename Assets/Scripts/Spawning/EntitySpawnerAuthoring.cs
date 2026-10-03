using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// Entity を 1 つ生成してほしいという要求。EntitySpawnSystem が処理する。
/// </summary>
public struct SpawnRequest : IBufferElementData
{
    /// <summary> 生成する Prefab Entity。 </summary>
    public Entity Prefab;

    /// <summary> 生成した Entity に設定する位置・回転・大きさ。 </summary>
    public LocalTransform Transform;
}

/// <summary>
/// シーンに置いた設定から、ゲーム開始時に生成する Entity の要求（SpawnRequest）を作る Authoring。
/// 位置をランダムにする場合は、seed から位置が決まるため、毎回同じ配置になる。
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
    /// 設定から生成要求（SpawnRequest）の一覧を作り、Entity に Buffer として追加する。
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
            // LocalTransform は全軸共通の大きさしか持てないため、X 軸の大きさを使う。
            // 軸ごとに異なる大きさが必要な場合は PostTransformMatrix を使う。
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
/// 生成位置の計算処理。Bake とエディタのプレビューで同じ計算を使い、プレビューと実際の配置が一致するようにしている。
/// </summary>
public static class SpawnTransformUtility
{
    /// <summary>
    /// 実際に生成する数を返す。位置をランダムにしない場合、同じ場所に重なってしまうため 1 つだけにする。
    /// </summary>
    /// <param name="spawnCount">設定された生成数。0 以下なら生成しない。</param>
    /// <param name="randomizePosition">true の場合だけ複数生成できる。</param>
    /// <returns>実際に生成する数。</returns>
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
    /// seed を 0 以外の値にする（Unity.Mathematics.Random は 0 を受け付けないため）。
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
    /// 1 体分の生成位置を返す。ランダムにする場合は、中心から範囲内のランダムな位置（XZ 平面）を返す。
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
