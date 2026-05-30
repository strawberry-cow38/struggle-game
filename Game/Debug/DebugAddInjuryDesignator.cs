using Godot;
using StruggleGame.Game.Designation;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Bodies;
using StruggleGame.Sim.Commands;

namespace StruggleGame.Game.Debug;

// Debug "Add Injury": click a colonist → pick a body part → pick a
// condition → it's applied. Stays in the tool so you can keep injuring;
// RMB / Esc cancels the tool (handled by Bootstrap).
public partial class DebugAddInjuryDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;
    private static readonly float PickRadiusPx = PixelsPerTile * 0.6f;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private PopupMenu _partMenu = null!;
    private PopupMenu _condMenu = null!;
    private int _pawnId;
    private string _partId = "";

    private static readonly ConditionKind[] _kinds =
        { ConditionKind.Cut, ConditionKind.Stab, ConditionKind.Burn, ConditionKind.Bruise, ConditionKind.Scar, ConditionKind.Missing };

    public override void _Ready()
    {
        ZIndex = 56;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.DebugAddInjury, () => { });
        _partMenu = new PopupMenu();
        AddChild(_partMenu);
        _partMenu.IdPressed += OnPartPicked;
        _condMenu = new PopupMenu();
        AddChild(_condMenu);
        _condMenu.IdPressed += OnConditionPicked;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null || Tools.Mode != ToolMode.DebugAddInjury) return;
        if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;

        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        var world = GetGlobalMousePosition();
        int bestId = -1;
        float bestSq = PickRadiusPx * PickRadiusPx;
        foreach (var d in snap.Dummies)
        {
            float dx = d.X * PixelsPerTile - world.X;
            float dy = d.Y * PixelsPerTile - world.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestSq) { bestSq = d2; bestId = d.EntityId; }
        }
        if (bestId < 0) return;

        _pawnId = bestId;
        _partMenu.Clear();
        for (int i = 0; i < BodyTree.All.Count; i++)
            _partMenu.AddItem(BodyTree.All[i].DisplayName, i);
        PopupAt(_partMenu, world);
        GetViewport().SetInputAsHandled();
    }

    private void OnPartPicked(long id)
    {
        if (id < 0 || id >= BodyTree.All.Count) return;
        _partId = BodyTree.All[(int)id].Id;
        _condMenu.Clear();
        for (int i = 0; i < _kinds.Length; i++)
            _condMenu.AddItem(_kinds[i].ToString(), i);
        PopupAt(_condMenu, GetGlobalMousePosition());
    }

    private void OnConditionPicked(long id)
    {
        if (Host is null || id < 0 || id >= _kinds.Length) return;
        var kind = _kinds[(int)id];
        // Damage is now in hit points (RimWorld-ish).
        float severity = kind switch
        {
            ConditionKind.Missing => 1f, // marker — part is removed regardless
            ConditionKind.Scar => 8f,    // permanent hp loss
            _ => 12f,                     // a solid wound
        };
        Host.QueueCommand(new ApplyInjuryCommand(_pawnId, _partId, kind, severity));
    }

    private void PopupAt(PopupMenu menu, Vector2 world)
    {
        var screen = GetCanvasTransform() * world;
        menu.Position = new Vector2I((int)screen.X, (int)screen.Y);
        menu.Popup();
    }
}
