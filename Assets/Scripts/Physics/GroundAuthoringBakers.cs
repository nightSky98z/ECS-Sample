using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Prefab 内の接地判定用の設定を GroundSensor Component に変換する（Baker から呼ばれる）。
/// </summary>
internal static class GroundSensorAuthoringUtility
{
    private const string SensorColliderTag = "SensorCollider";
    private const float DefaultSkin = 0.03f;

    /// <summary>
    /// 子オブジェクトから接地判定用の設定を探し、GroundSensor を作る。
    /// 1. GroundSensorAuthoring があればそれを使う
    /// 2. なければ、SensorCollider タグが付いた SphereCollider を使う
    /// </summary>
    /// <param name="authoring">検索を始めるオブジェクト。GroundSensor はこのオブジェクトのローカル座標で保存される。</param>
    /// <param name="sensor">作成した GroundSensor。</param>
    /// <returns>判定用の設定が見つかった場合は true。</returns>
    public static bool TryCreateGroundSensor(MonoBehaviour authoring, out GroundSensor sensor)
    {
        sensor = default;

        var rootTransform = authoring.transform;
        var rootWorldToLocal = rootTransform.worldToLocalMatrix;
        var authoringSensors = authoring.GetComponentsInChildren<GroundSensorAuthoring>(true);

        foreach (var groundSensorAuthoring in authoringSensors)
        {
            var centerWorld = groundSensorAuthoring.transform.position;
            var localCenter = rootWorldToLocal.MultiplyPoint3x4(centerWorld);
            var sensorScale = GroundSensorAuthoringMath.GetMaxAbsScale(groundSensorAuthoring.transform.lossyScale);
            // 実行時にルートのスケールが掛けられるため、ここではルートのスケールで割っておく。
            var rootScale = GroundSensorAuthoringMath.GetMaxAbsScale(rootTransform.lossyScale);

            if (rootScale <= 0.000001f)
            {
                rootScale = 1f;
            }

            sensor = GroundSensorAuthoringMath.CreateSensorData(
                localCenter,
                groundSensorAuthoring.SensorRadius * sensorScale / rootScale,
                groundSensorAuthoring.SensorSkin);

            return true;
        }

        var colliders = authoring.GetComponentsInChildren<SphereCollider>(true);

        foreach (var sphereCollider in colliders)
        {
            if (!sphereCollider.CompareTag(SensorColliderTag))
            {
                continue;
            }

            var centerWorld = sphereCollider.transform.TransformPoint(sphereCollider.center);
            var localCenter = rootWorldToLocal.MultiplyPoint3x4(centerWorld);
            var lossyScale = sphereCollider.transform.lossyScale;
            var maxColliderScale = Mathf.Max(
                Mathf.Abs(lossyScale.x),
                Mathf.Abs(lossyScale.y),
                Mathf.Abs(lossyScale.z));
            var rootScale = Mathf.Abs(rootTransform.lossyScale.x);

            if (rootScale <= 0.000001f)
            {
                rootScale = 1f;
            }

            sensor = GroundSensorAuthoringMath.CreateSensorData(
                localCenter,
                sphereCollider.radius * maxColliderScale / rootScale,
                DefaultSkin);

            return true;
        }

        return false;
    }
}

/// <summary>
/// Ground タグが付いた MeshCollider に GroundTag を追加し、接地判定の対象にする。
/// </summary>
internal sealed class GroundMeshColliderBaker : Baker<MeshCollider>
{
    private const string GroundTagName = "Ground";

    public override void Bake(MeshCollider authoring)
    {
        if (!authoring.CompareTag(GroundTagName))
        {
            return;
        }

        var entity = GetEntity(TransformUsageFlags.None);

        AddComponent<GroundTag>(entity);
    }
}
