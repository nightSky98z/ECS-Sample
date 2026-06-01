using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;

/// <summary>
/// Unity Input System のキーボード入力を PlayerInput component に書き込む。
///
/// この System は入力デバイスを読む境界だけを担当し、移動量の適用は MovementSystem に委譲する。
/// PlayerInput を常に上書きすることで、入力なしの frame でも古い入力が残らないようにする。
/// </summary>
[UpdateBefore(typeof(MovementSystem))]
public partial struct PlayerInputSystem : ISystem
{
    /// <summary>
    /// Player が存在する world でだけ input polling を有効にする。
    /// </summary>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
    }

    /// <summary>
    /// Keyboard.current から WASD を読み、全 PlayerTag entity の PlayerInput.Move を更新する。
    /// </summary>
    public void OnUpdate(ref SystemState state)
    {
        var canReadGameplayInput = true;

        foreach (var runtimeState in SystemAPI.Query<RefRO<StageRuntimeState>>())
        {
            if (!StageRuntimeUtility.IsGameplayPhase(runtimeState.ValueRO.Phase))
            {
                canReadGameplayInput = false;
                break;
            }
        }

        var move = float2.zero;
        var keyboard = Keyboard.current;

        if (canReadGameplayInput && keyboard != null)
        {
            move.x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
            move.y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);

            if (math.lengthsq(move) > 1f)
            {
                // 斜め入力が軸入力より速くならないように、入力ベクトルだけを正規化する。
                move = math.normalize(move);
            }
        }

        foreach (var input in SystemAPI.Query<RefRW<PlayerInput>>().WithAll<PlayerTag>())
        {
            input.ValueRW.Move = move;
        }
    }
}
