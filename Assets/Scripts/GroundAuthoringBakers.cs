using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Authoring 側の SensorCollider SphereCollider を GroundSensor に変換する。
/// </summary>
internal static class GroundSensorAuthoringUtility
{
    private const string SensorColliderTag = "SensorCollider";
    private const float DefaultSkin = 0.03f;

    /// <summary>
    /// authoring の子から接地センサー用 SphereCollider を検索する。
    /// </summary>
    /// <param name="authoring">検索元の authoring component。戻り値の GroundSensor は root local 空間で保存される。</param>
    /// <param name="sensor">見つかった SphereCollider から作った GroundSensor。</param>
    /// <returns>SensorCollider タグの SphereCollider が見つかった場合は true。</returns>
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
/// Ground タグの MeshCollider を DOTS Physics の接地対象として分類する。
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
