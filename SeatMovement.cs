using UnityEngine;

namespace PassengerCoachAccess
{
    // The native controller keeps its last movement vector after a frame in
    // which CharacterMovement has already processed W/Shift.  Seat transitions
    // must publish a stopped movement state before Multiplayer samples it.
    internal sealed class SeatMovementSnapshot
    {
        internal Vector3 MoveDirection;
        internal Vector3 DesiredMove;
        internal Vector2 Input;
        internal bool Jumping;
        internal bool Walking;

        internal void Stop()
        {
            MoveDirection = Vector3.zero;
            DesiredMove = Vector3.zero;
            Input = Vector2.zero;
            Jumping = false;
            Walking = true;
        }
    }
}
