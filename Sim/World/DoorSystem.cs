using Friflo.Engine.ECS;

namespace StruggleGame.Sim.World;

// Drives the open/close state machine for every built Door:
//   Closed   + WantsOpen → Opening
//   Opening  → ProgressSec ramps to OpenTimeSec, then Open
//   Open     + IdleSec >= AutoCloseSec → Closing
//   Closing  → ProgressSec ramps back to 0, then Closed
//
// WantsOpen is set by the mover when a pawn intends to step onto the
// door tile. Once Open, IdleSec accumulates each tick except when the
// mover resets it (pawn currently on / entering the tile).
public sealed class DoorSystem
{
    public const float OpenTimeSec = 0.5f;
    public const float AutoCloseSec = 1.5f;

    public void Step(EntityStore store, float dt)
    {
        var q = store.Query<Door>();
        q.ForEachEntity((ref Door door, Entity _) =>
        {
            switch (door.State)
            {
                case DoorState.Closed:
                    if (door.WantsOpen)
                    {
                        door.State = DoorState.Opening;
                        door.ProgressSec = 0f;
                        door.WantsOpen = false;
                    }
                    break;
                case DoorState.Opening:
                    door.ProgressSec += dt;
                    if (door.ProgressSec >= OpenTimeSec)
                    {
                        door.ProgressSec = OpenTimeSec;
                        door.State = DoorState.Open;
                        door.IdleSec = 0f;
                    }
                    break;
                case DoorState.Open:
                    door.IdleSec += dt;
                    if (door.WantsOpen)
                    {
                        // Pawn still passing through — keep it open.
                        door.IdleSec = 0f;
                        door.WantsOpen = false;
                    }
                    else if (door.IdleSec >= AutoCloseSec)
                    {
                        door.State = DoorState.Closing;
                        door.ProgressSec = OpenTimeSec;
                    }
                    break;
                case DoorState.Closing:
                    if (door.WantsOpen)
                    {
                        // Caught a pawn mid-close — reverse course.
                        door.State = DoorState.Opening;
                        door.WantsOpen = false;
                        break;
                    }
                    door.ProgressSec -= dt;
                    if (door.ProgressSec <= 0f)
                    {
                        door.ProgressSec = 0f;
                        door.State = DoorState.Closed;
                    }
                    break;
            }
        });
    }
}
