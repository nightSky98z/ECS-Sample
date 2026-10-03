using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 接地判定に使う球の位置と半径を、Prefab 上で調整するための Authoring。
/// この GameObject の位置が球の中心になる。
/// </summary>
public sealed class GroundSensorAuthoring : MonoBehaviour
{
    [SerializeField]
    [Min(0f)]
    [Tooltip("接地判定に使う球の半径。Transform のスケールも反映される。")]
    private float Radius = 0.1f;

    [SerializeField]
    [Min(0f)]
    [Tooltip("接地として許容する余白距離。")]
    private float Skin = 0.03f;

    public float SensorRadius => Radius;
    public float SensorSkin => Skin;

    /// <summary>
    /// 負の値が入力された場合に正の値へ直す。
    /// </summary>
    private void OnValidate()
    {
        Radius = math.abs(Radius);
        Skin = math.abs(Skin);
    }

#if UNITY_EDITOR
    /// <summary>
    /// Scene ビューに判定用の球を表示する。
    /// </summary>
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
/// GroundSensorAuthoring と Baker で共通に使う変換処理。
/// </summary>
public static class GroundSensorAuthoringMath
{
    /// <summary>
    /// Inspector の設定値から GroundSensor Component を作る。
    /// </summary>
    /// <param name="localCenter">Entity の原点から見た球の中心。</param>
    /// <param name="radius">Entity のローカル座標での半径。</param>
    /// <param name="skin">接地とみなす余裕の幅。</param>
    /// <returns>作成した GroundSensor。</returns>
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
    /// スケールの各軸のうち、絶対値が最も大きいものを返す（球の半径にかけるため）。
    /// </summary>
    public static float GetMaxAbsScale(float3 scale)
    {
        return math.max(math.max(math.abs(scale.x), math.abs(scale.y)), math.abs(scale.z));
    }
}
