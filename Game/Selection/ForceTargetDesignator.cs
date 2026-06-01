using Godot;
using StruggleGame.Game.Designation;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;

namespace StruggleGame.Game.Selection;

// Force-fire targeting: with a drafted ranged colonist selected, the draft
// action bar's "Force Target" button enters ToolMode.ForceFireTarget. The
// next LMB click on another pawn issues a fire order at it, then drops back
// to the normal tool. RMB / Esc cancels (handled by Bootstrap).
public partial class ForceTargetDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;
    private static readonly float PickRadiusPx = PixelsPerTile * 0.6f;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    public override void _Ready()
    {
        ZIndex = 56;
        if (Tools is not null)
            this.BindInputToMode(Tools, m => m == ToolMode.ForceFireTarget || m == ToolMode.MeleeAttackTarget, () => { });
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null) return;
        bool melee = Tools.Mode == ToolMode.MeleeAttackTarget;
        if (!melee && Tools.Mode != ToolMode.ForceFireTarget) return;
        if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;

        var snap = Host.LatestSnapshot;
        int? shooter = Host.SelectedDummyId;
        if (snap is null || shooter is null) { Tools.Mode = ToolMode.None; return; }

        var world = GetGlobalMousePosition();
        int bestId = -1;
        float bestSq = PickRadiusPx * PickRadiusPx;
        foreach (var d in snap.Dummies)
        {
            if (d.EntityId == shooter.Value) continue; // don't target self
            float dx = d.X * PixelsPerTile - world.X;
            float dy = d.Y * PixelsPerTile - world.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestSq) { bestSq = d2; bestId = d.EntityId; }
        }
        if (bestId >= 0)
            Host.QueueCommand(melee
                ? new MeleeAttackCommand(shooter.Value, bestId)
                : new SetFireTargetCommand(shooter.Value, bestId));

        Tools.Mode = ToolMode.None;
        GetViewport().SetInputAsHandled();
    }
}
