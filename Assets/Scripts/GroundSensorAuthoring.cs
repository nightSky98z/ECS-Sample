using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Prefab 上で接地センサーの中心と半径を調整するための Authoring。
/// </summary>
public sealed class GroundSensorAuthoring : MonoBehaviour
{
    [SerializeField]
    [Min(0f)]
    [Tooltip("接地判定に使う sphere の半径。Transform scale も反映される。")]
    private float Radius = 0.1f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("接地として許容する余白距離。")]
    private float Skin = 0.03f;

    public float SensorRadius => Radius;
    public float SensorSkin => Skin;

    private void OnValidate()
    {
        Radius = math.abs(Radius);
        Skin = math.abs(Skin);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        var previousColor = Gizmos.color;
        var sensorScale = GroundSensorAuthoringMath.GetMaxAbsScale(transform.lossyScale);

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.85f);
        Gizmos.DrawWireSphere(transform.position, Radius * sensorScale);
        Gizmos.color = previousColor;
    }
#endif
}

/// <summary>
/// GroundSensorAuthoring と Baker が共有するデータ変換。
/// </summary>
public static class GroundSensorAuthoringMath
{
    /// <summary>
    /// Authoring 値から runtime 用 GroundSensor を作る。
    /// </summary>
    /// <param name="localCenter">root Entity から見た sensor 中心。</param>
    /// <param name="radius">root Entity の local 空間へ変換済みの半径。</param>
    /// <param name="skin">接地許容距離。</param>
    /// <returns>runtime 用 GroundSensor。</returns>
    public static GroundSensor CreateSensorData(float3 localCenter, float radius, float skin)
    {
        return new GroundSensor
        {
            LocalCenter = localCenter,
            Radius = math.abs(radius),
            Skin = math.abs(skin)
        };
    }

    /// <summary>
    /// scale の最大絶対値を返す。
    /// </summary>
    public static float GetMaxAbsScale(float3 scale)
    {
        return math.max(math.max(math.abs(scale.x), math.abs(scale.y)), math.abs(scale.z));
    }
}
