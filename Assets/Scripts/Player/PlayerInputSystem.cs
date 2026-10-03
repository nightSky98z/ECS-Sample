using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;

/// <summary>
/// キーボード入力（WASD）を読み取り、PlayerInput Component に書き込む。
/// この System は「入力を読む」ことだけを担当し、実際の移動は MovementSystem に任せている。
/// 毎フレーム必ず上書きするため、キーを離したフレームに古い入力が残ることはない。
/// </summary>
[UpdateBefore(typeof(MovementSystem))]
public partial struct PlayerInputSystem : ISystem
{
    /// <summary>
    /// プレイヤーがいる World でだけ入力を読むようにする。
    /// </summary>
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
    }

    /// <summary>
    /// WASD キーの状態を読み、プレイヤーの PlayerInput.Move を更新する。ゲーム中以外（クリア後など）は入力を 0 にする。
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
                // 斜め移動が上下左右の移動より速くならないよう、長さを 1 に揃える。
                move = math.normalize(move);
            }
        }

        foreach (var input in SystemAPI.Query<RefRW<PlayerInput>>().WithAll<PlayerTag>())
        {
            input.ValueRW.Move = move;
        }
    }
}
