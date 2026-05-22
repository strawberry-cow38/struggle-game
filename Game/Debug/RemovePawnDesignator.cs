using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;

namespace StruggleGame.Game.Debug;

// Active when ToolMode == RemovePawn. LMB picks the nearest pawn within
// a small pixel radius (same heuristic as Selector) and queues a
// RemoveDummyCommand. Clicks that miss are no-ops.
public partial class RemovePawnDesignator : Node2D
{
    private const float PickRadiusPx = SimConstants.PixelsPerTile * 0.6f;
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null) return;
        if (Tools.Mode != ToolMode.RemovePawn) return;
        if (@event is not InputEventMouseButton mb) return;
        if (mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;

        var snap = Host.LatestSnapshot;
        if (snap is null) return;

        var world = GetGlobalMousePosition();
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

        if (bestId < 0) return;

        if (Host.SelectedDummyId == bestId) Host.SelectedDummyId = null;
        Host.QueueCommand(new RemoveDummyCommand(bestId));
        GetViewport().SetInputAsHandled();
    }
}
