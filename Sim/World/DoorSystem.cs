using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

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

    // Reused per-tick scratch — cleared at top of Step rather than freshly
    // allocated, so the per-tick path doesn't churn the GC.
    private readonly HashSet<TilePos> _occupiedTiles = new();
    // "Unreserved wood on this tile" — from the item spatial index instead
    // of a per-tick full Wood scan. Reserved wood (a hauler's coming for
    // it) doesn't wedge the door, matching the old scan's filter.
    private readonly Func<TilePos, bool> _anyUnreservedWoodAt;

    public DoorSystem(Func<TilePos, bool> anyUnreservedWoodAt)
    {
        _anyUnreservedWoodAt = anyUnreservedWoodAt;
    }

    public void Step(EntityStore store, float dt)
    {
        // Colonists physically standing on a door tile keep it open too —
        // otherwise a pawn standing in the doorway gets sliced as a
        // second pawn walks through and triggers the close timer. Mover
        // already resets IdleSec while a pawn is actively crossing, but
        // a pawn that has STOPPED on the tile (paused job, idle) wasn't
        // ticking that reset.
        _occupiedTiles.Clear();
        store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            _occupiedTiles.Add(new TilePos((int)p.X, (int)p.Y));
        });

        var q = store.Query<Door>();
        q.ForEachEntity((ref Door door, Entity _) =>
        {
            // Forbidden = treated as a wall; refuse every open trigger.
            // Drop any stale WantsOpen so a future un-forbid starts clean.
            if (door.Forbidden)
            {
                door.WantsOpen = false;
                if (door.State == DoorState.Opening)
                {
                    door.State = DoorState.Closing;
                }
                if (door.State == DoorState.Open)
                {
                    door.State = DoorState.Closing;
                    door.ProgressSec = OpenTimeSec;
                }
            }
            bool blocked = _anyUnreservedWoodAt(door.Tile) || _occupiedTiles.Contains(door.Tile);
            switch (door.State)
            {
                case DoorState.Closed:
                    if (!door.Forbidden && (door.WantsOpen || blocked))
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
                    else if (blocked)
                    {
                        door.IdleSec = 0f;
                    }
                    else if (door.IdleSec >= AutoCloseSec)
                    {
                        door.State = DoorState.Closing;
                        door.ProgressSec = OpenTimeSec;
                    }
                    break;
                case DoorState.Closing:
                    if (door.WantsOpen || blocked)
                    {
                        // Caught a pawn (or item blocking the swing) mid-close.
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
