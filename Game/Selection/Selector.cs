using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;

namespace StruggleGame.Game.Selection;

// LMB click in the world (when no other tool is active) selects the
// nearest colonist within a small pixel radius; clicking empty space
// deselects. Selection lives on SimHost so the sim thread can include
// the selected pawn's path in the snapshot.
public partial class Selector : Node2D
{
    private const float PickRadiusPx = SimConstants.PixelsPerTile * 0.6f;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null) return;
        if (Tools.Mode != ToolMode.None) return;
        if (@event is not InputEventMouseButton mb) return;
        if (mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;

        var world = GetGlobalMousePosition();
        var snap = Host.LatestSnapshot;
        if (snap is null) return;

        int bestId = -1;
        float bestDistSq = PickRadiusPx * PickRadiusPx;
        foreach (var d in snap.Dummies)
        {
            float px = d.X * SimConstants.PixelsPerTile;
            float py = d.Y * SimConstants.PixelsPerTile;
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
        GetViewport().SetInputAsHandled();
    }
}
