using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;

/// <summary>
/// Unity Input System のキーボード入力を PlayerInput component に書き込む。
/// </summary>
[UpdateBefore(typeof(MovementSystem))]
public partial struct PlayerInputSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<PlayerTag>();
    }

    public void OnUpdate(ref SystemState state)
    {
        var move = float2.zero;
        var keyboard = Keyboard.current;

        if (keyboard != null)
        {
            move.x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
            move.y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);

            if (math.lengthsq(move) > 1f)
            {
                move = math.normalize(move);
            }
        }

        foreach (var input in SystemAPI.Query<RefRW<PlayerInput>>().WithAll<PlayerTag>())
        {
            input.ValueRW.Move = move;
        }
    }
}
