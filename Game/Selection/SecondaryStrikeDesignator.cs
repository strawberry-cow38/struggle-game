using Godot;
using StruggleGame.Game.Designation;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;

namespace StruggleGame.Game.Selection;

// Secondary-weapon (underbarrel launcher) ground-strike targeting: with a
// drafted pawn whose weapon carries a secondary launcher (M16 M203) selected,
// the draft action bar's "M203" button enters ToolMode.SecondaryStrike. The
// next LMB click picks a GROUND tile and orders a grenade lobbed at it. A
// reticle previews the aim point — red when it's inside the min range or past
// the SECONDARY's reach (the sim also rejects no-LoS shots). RMB / Esc
// cancels (handled by Bootstrap).
public partial class SecondaryStrikeDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;
    private static readonly Color OkColor = new(0.4f, 1.0f, 0.45f, 0.9f);
    private static readonly Color BadColor = new(1.0f, 0.35f, 0.3f, 0.9f);
    private static readonly Color RingColor = new(1.0f, 0.8f, 0.3f, 0.5f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    public override void _Ready()
    {
        ZIndex = 57;
        if (Tools is not null)
            this.BindInputToMode(Tools, m => m == ToolMode.SecondaryStrike, QueueRedraw);
    }

    public override void _Process(double delta)
    {
        if (Tools?.Mode == ToolMode.SecondaryStrike) QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null || Tools.Mode != ToolMode.SecondaryStrike) return;
        if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;

        int? shooter = Host.SelectedDummyId;
        if (shooter is null) { Tools.Mode = ToolMode.None; return; }

        var world = GetGlobalMousePosition();
        int tx = Mathf.FloorToInt(world.X / PixelsPerTile);
        int ty = Mathf.FloorToInt(world.Y / PixelsPerTile);
        Host.QueueCommand(new LaunchSecondaryCommand(shooter.Value, tx, ty));

        Tools.Mode = ToolMode.None;
        GetViewport().SetInputAsHandled();
    }

    // The shooter's SECONDARY range (the snapshot's RangedRange is the rifle's)
    // — looked up from the equipped weapon's catalog def.
    private static float SecondaryRangeOf(in Sim.Snapshots.DummyState d)
    {
        foreach (var eq in d.Equipped)
            if (ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var def) && def.RangedSecondary is not null)
                return def.RangedSecondary.Range;
        return 0f;
    }

    public override void _Draw()
    {
        if (Host is null || Tools is null || Tools.Mode != ToolMode.SecondaryStrike) return;
        var snap = Host.LatestSnapshot;
        int? shooter = Host.SelectedDummyId;
        if (snap is null || shooter is null) return;

        float sx = 0f, sy = 0f, range = 0f;
        bool found = false;
        foreach (var d in snap.Dummies)
            if (d.EntityId == shooter.Value) { sx = d.X; sy = d.Y; range = SecondaryRangeOf(d); found = true; break; }
        if (!found) return;

        var center = new Vector2(sx, sy) * PixelsPerTile;
        // Min (can't-fire) and max (reach) range rings.
        DrawArc(center, SimConstants.RocketMinTargetRange * PixelsPerTile, 0, Mathf.Tau, 48, RingColor, 3.0f, true);
        DrawArc(center, range * PixelsPerTile, 0, Mathf.Tau, 64, RingColor, 3.0f, true);

        var world = GetGlobalMousePosition();
        int tx = Mathf.FloorToInt(world.X / PixelsPerTile);
        int ty = Mathf.FloorToInt(world.Y / PixelsPerTile);
        var aim = new Vector2(tx + 0.5f, ty + 0.5f) * PixelsPerTile;
        float dist = (aim - center).Length() / PixelsPerTile;
        bool inBand = dist >= SimConstants.RocketMinTargetRange && dist <= range;
        var col = inBand ? OkColor : BadColor;

        // Crosshair reticle on the aim tile.
        float r = PixelsPerTile * 0.5f;
        DrawArc(aim, r, 0, Mathf.Tau, 32, col, 2.0f, true);
        DrawLine(aim - new Vector2(r, 0), aim + new Vector2(r, 0), col, 2.0f, true);
        DrawLine(aim - new Vector2(0, r), aim + new Vector2(0, r), col, 2.0f, true);
    }
}
