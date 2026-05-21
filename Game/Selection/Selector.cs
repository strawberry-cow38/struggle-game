using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Selection;

// LMB click in the world (when no other tool is active) selects the
// nearest colonist within a small pixel radius; clicking empty space
// deselects. Selection lives on SimHost so the sim thread can include
// the selected pawn's path in the snapshot.
//
// RMB on a drafted colonist's selection issues a move order to the
// clicked tile. Shift+RMB appends to the order queue instead of
// replacing it. If the right-clicked target had multiple actions (no
// such targets exist yet), a context menu would open instead — for now
// move is the only valid action.
public partial class Selector : Node2D
{
    private const float PickRadiusPx = SimConstants.PixelsPerTile * 0.6f;
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null) return;
        if (Tools.Mode != ToolMode.None) return;
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;

        if (mb.ButtonIndex == MouseButton.Left)
        {
            HandleSelect();
            GetViewport().SetInputAsHandled();
        }
        else if (mb.ButtonIndex == MouseButton.Right)
        {
            if (HandleOrder(mb.ShiftPressed))
            {
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private void HandleSelect()
    {
        var world = GetGlobalMousePosition();
        var snap = Host!.LatestSnapshot;
        if (snap is null) return;

        int bestId = -1;
        float bestDistSq = PickRadiusPx * PickRadiusPx;
        foreach (var d in snap.Dummies)
        {
            float px = d.X * PixelsPerTile;
            float py = d.Y * PixelsPerTile;
            float dx = px - world.X;
            float dy = py - world.Y;
            float distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestId = d.EntityId;
            }
        }

        Host.SelectedDummyId = bestId >= 0 ? bestId : null;
    }

    private bool HandleOrder(bool append)
    {
        if (Host!.SelectedDummyId is not int sel) return false;
        var snap = Host.LatestSnapshot;
        if (snap is null) return false;

        bool drafted = false;
        foreach (var d in snap.Dummies)
        {
            if (d.EntityId == sel) { drafted = d.Drafted; break; }
        }
        if (!drafted) return false;

        var world = GetGlobalMousePosition();
        var tile = new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
        Host.QueueCommand(new IssueMoveOrderCommand(sel, tile, append));
        return true;
    }
}
