using System.Collections.Generic;
using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Game.Tools;

namespace StruggleGame.Game.UI;

// Bottom-center bar shown when a single drafted colonist holding a ranged
// weapon is selected. Offers the weapon's available fire modes + a
// "Force Target" button (enters ToolMode.ForceFireTarget; the next click on
// a pawn issues a fire order). Polls the snapshot each frame like the other
// selection-driven panels.
public partial class DraftActionBar : CanvasLayer
{
    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private const int ButtonHeight = 28;
    private const float MarginBottom = 14f;

    private const int UnloadMenuId = 10000;

    private HBoxContainer _bar = null!;
    private Label _magLabel = null!;
    private Button _forceTargetBtn = null!;
    private PopupMenu _reloadMenu = null!;
    private readonly List<string> _reloadAmmoPaths = new();

    private readonly (FireMode mode, FireModeFlags flag)[] _modes =
    {
        (FireMode.Single, FireModeFlags.Single),
        (FireMode.Burst, FireModeFlags.Burst),
        (FireMode.Auto, FireModeFlags.Auto),
    };
    private readonly Button[] _modeButtons = new Button[3];

    private Button _targetAreaBtn = null!;
    private static readonly TargetArea[] _areaCycle = { TargetArea.Auto, TargetArea.Head, TargetArea.Torso, TargetArea.Legs };
    private TargetArea _shownArea = TargetArea.Auto;

    private int _shownPawnId = -1;

    public override void _Ready()
    {
        Layer = 96;

        _bar = new HBoxContainer
        {
            Name = "DraftRow",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _bar.AddThemeConstantOverride("separation", 6);
        AddChild(_bar);

        var fireLabel = new Label { Text = "Fire:" };
        fireLabel.AddThemeConstantOverride("outline_size", 4);
        _bar.AddChild(fireLabel);

        for (int i = 0; i < _modes.Length; i++)
        {
            var m = _modes[i].mode;
            var btn = new Button
            {
                Text = m.ToString(),
                ToggleMode = true,
                CustomMinimumSize = new Vector2(0, ButtonHeight),
                FocusMode = Control.FocusModeEnum.None,
            };
            btn.Pressed += () =>
            {
                if (Host is null || _shownPawnId < 0) return;
                Host.QueueCommand(new SetFireModeCommand(_shownPawnId, m));
            };
            _bar.AddChild(btn);
            _modeButtons[i] = btn;
        }

        _magLabel = new Label { Text = "" };
        _magLabel.AddThemeConstantOverride("outline_size", 4);
        _bar.AddChild(_magLabel);

        var reloadBtn = new Button
        {
            Text = "Reload",
            TooltipText = "Left-click: reload. Right-click: pick ammo / unload.",
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        reloadBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            Host.QueueCommand(new ReloadWeaponCommand(_shownPawnId));
        };
        reloadBtn.GuiInput += OnReloadGuiInput;
        _bar.AddChild(reloadBtn);

        _reloadMenu = new PopupMenu();
        AddChild(_reloadMenu);
        _reloadMenu.IdPressed += OnReloadMenuPick;

        _forceTargetBtn = new Button
        {
            Text = "Force Target",
            ToggleMode = true,
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _forceTargetBtn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = _forceTargetBtn.ButtonPressed ? ToolMode.ForceFireTarget : ToolMode.None;
        };
        _bar.AddChild(_forceTargetBtn);

        // Targeted area — cycles Head → Torso → Legs, sets the aim region.
        _targetAreaBtn = new Button
        {
            Text = "Aim: Auto",
            TooltipText = "Body region the colonist aims for.",
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _targetAreaBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            int idx = System.Array.IndexOf(_areaCycle, _shownArea);
            var next = _areaCycle[(idx + 1) % _areaCycle.Length];
            Host.QueueCommand(new SetTargetAreaCommand(_shownPawnId, next));
        };
        _bar.AddChild(_targetAreaBtn);
    }

    public override void _Process(double delta)
    {
        if (Host is null) { HideBar(); return; }
        var snap = Host.LatestSnapshot;
        int? sel = Host.SelectedDummyId;
        if (snap is null || sel is null) { HideBar(); return; }

        DummyState? found = null;
        foreach (var d in snap.Dummies)
            if (d.EntityId == sel.Value) { found = d; break; }

        if (found is not { } p || !p.Drafted || !p.HasRangedWeapon) { HideBar(); return; }

        _shownPawnId = p.EntityId;
        if (!_bar.Visible) _bar.Visible = true;

        for (int i = 0; i < _modes.Length; i++)
        {
            bool avail = (p.RangedModes & _modes[i].flag) != 0;
            _modeButtons[i].Visible = avail;
            _modeButtons[i].SetPressedNoSignal(avail && p.RangedMode == _modes[i].mode);
        }
        _magLabel.Text = $"Mag: {p.RangedMag}/{p.RangedMagSize}";

        _shownArea = p.RangedTargetArea;
        _targetAreaBtn.Text = $"Aim: {p.RangedTargetArea}";

        if (Tools is not null)
            _forceTargetBtn.SetPressedNoSignal(Tools.Mode == ToolMode.ForceFireTarget);

        // Bottom-center, recomputed each frame (button visibility changes width).
        var vp = GetViewport().GetVisibleRect().Size;
        _bar.Position = new Vector2((vp.X - _bar.Size.X) * 0.5f, vp.Y - _bar.Size.Y - MarginBottom);
    }

    // Right-click the Reload button → choose an ammo type the colonist is
    // carrying (force-reloads + locks auto-reload to it), or unload the mag.
    private void OnReloadGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb) return;
        if (mb.ButtonIndex != MouseButton.Right || !mb.Pressed) return;
        if (Host?.LatestSnapshot is not { } snap || _shownPawnId < 0) return;

        DummyState? found = null;
        foreach (var d in snap.Dummies)
            if (d.EntityId == _shownPawnId) { found = d; break; }
        if (found is not { } p) return;

        _reloadMenu.Clear();
        _reloadAmmoPaths.Clear();
        foreach (var h in p.Held)
        {
            if (!ItemCatalog.ItemsByPath.TryGetValue(h.ItemPath, out var def) || def.Ammo is null) continue;
            int id = _reloadAmmoPaths.Count;
            _reloadMenu.AddItem($"Reload: {def.DisplayName} ({h.Count})", id);
            _reloadAmmoPaths.Add(h.ItemPath);
        }
        if (_reloadMenu.ItemCount > 0) _reloadMenu.AddSeparator();
        _reloadMenu.AddItem("Unload Magazine", UnloadMenuId);

        _reloadMenu.Position = (Vector2I)GetViewport().GetMousePosition();
        _reloadMenu.Popup();
    }

    private void OnReloadMenuPick(long id)
    {
        if (Host is null || _shownPawnId < 0) return;
        if (id == UnloadMenuId)
        {
            Host.QueueCommand(new UnloadMagazineCommand(_shownPawnId));
            return;
        }
        if (id >= 0 && id < _reloadAmmoPaths.Count)
            Host.QueueCommand(new SetReloadAmmoCommand(_shownPawnId, _reloadAmmoPaths[(int)id]));
    }

    private void HideBar()
    {
        if (_bar.Visible) { _bar.Visible = false; _shownPawnId = -1; }
    }
}
