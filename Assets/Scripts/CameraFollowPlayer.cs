using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

/// <summary>
/// PlayerTag を持つ Entity の位置に Main Camera を追従させる。
/// </summary>
public sealed class CameraFollowPlayer : MonoBehaviour
{
    [Tooltip("プレイヤー位置から見たカメラの相対位置。")]
    [SerializeField]
    private Vector3 Offset = new Vector3(0f, 0f, -10f);

    [Tooltip("追従の滑らかさ。大きいほど素早く目標位置へ近づく。")]
    [SerializeField]
    private float FollowSpeed = 12f;

    private EntityQuery PlayerQuery;

    private void OnEnable()
    {
        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            enabled = false;
            return;
        }

        PlayerQuery = world.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadOnly<LocalToWorld>()
        );
    }

    private void LateUpdate()
    {
        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            return;
        }

        if (PlayerQuery.IsEmpty)
        {
            return;
        }

        var entityManager = world.EntityManager;
        var playerEntity = PlayerQuery.GetSingletonEntity();
        var playerTransform = entityManager.GetComponentData<LocalToWorld>(playerEntity);

        var playerPosition = (Vector3)playerTransform.Position;
        var targetPosition = playerPosition + Offset;

        transform.position = Vector3.Lerp(
            transform.position,
            targetPosition,
            1f - Mathf.Exp(-FollowSpeed * Time.deltaTime)
        );
    }
}
