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
    private Button _draftBtn = null!;
    private HBoxContainer _combatGroup = null!;
    private Label _magLabel = null!;
    private Button _forceTargetBtn = null!;
    private Button _meleeBtn = null!;
    private Button _fireAtWillBtn = null!;
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

    private Button _aimModeBtn = null!;
    private static readonly AimMode[] _aimModeCycle = { AimMode.Aimed, AimMode.Snapshot, AimMode.Auto };
    private AimMode _shownAimMode = AimMode.Aimed;

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

        // Draft / Undraft toggle — always shown while a colonist is selected.
        _draftBtn = new Button
        {
            Text = "Draft",
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _draftBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            Host.QueueCommand(new ToggleDraftCommand(_shownPawnId));
        };
        _bar.AddChild(_draftBtn);

        // Combat controls — only visible once drafted.
        _combatGroup = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _combatGroup.AddThemeConstantOverride("separation", 6);
        _bar.AddChild(_combatGroup);

        var fireLabel = new Label { Text = "Fire:" };
        fireLabel.AddThemeConstantOverride("outline_size", 4);
        _combatGroup.AddChild(fireLabel);

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
            _combatGroup.AddChild(btn);
            _modeButtons[i] = btn;
        }

        _magLabel = new Label { Text = "" };
        _magLabel.AddThemeConstantOverride("outline_size", 4);
        _combatGroup.AddChild(_magLabel);

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
        _combatGroup.AddChild(reloadBtn);

        _reloadMenu = new PopupMenu();
        AddChild(_reloadMenu);
        _reloadMenu.IdPressed += OnReloadMenuPick;

        // Weapon-name button = ranged target mode: click it, then a pawn, to
        // set a shoot target. (Text is set to the equipped weapon each frame.)
        _forceTargetBtn = new Button
        {
            Text = "Fire",
            ToggleMode = true,
            TooltipText = "Click, then a pawn, to set a shoot target.",
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _forceTargetBtn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = _forceTargetBtn.ButtonPressed ? ToolMode.ForceFireTarget : ToolMode.None;
        };
        _combatGroup.AddChild(_forceTargetBtn);

        // Melee target mode: click, then a pawn, to set a melee attack target.
        _meleeBtn = new Button
        {
            Text = "Melee Attack",
            ToggleMode = true,
            TooltipText = "Click, then a pawn, to melee it.",
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _meleeBtn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = _meleeBtn.ButtonPressed ? ToolMode.MeleeAttackTarget : ToolMode.None;
        };
        _combatGroup.AddChild(_meleeBtn);

        // Global "fire at will" toggle — off = colonists only fire at forced
        // (RMB) targets, no auto-acquire/peek.
        _fireAtWillBtn = new Button
        {
            Text = "Fire at Will",
            ToggleMode = true,
            TooltipText = "On: colonists auto-engage enemies. Off: only fire at a target you assign via right-click.",
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _fireAtWillBtn.Pressed += () =>
        {
            if (Host is null) return;
            Host.QueueCommand(new SetFireAtWillCommand(_fireAtWillBtn.ButtonPressed));
        };
        _combatGroup.AddChild(_fireAtWillBtn);

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
        _combatGroup.AddChild(_targetAreaBtn);

        // Aim mode — cycles Aimed → Snapshot → Auto.
        _aimModeBtn = new Button
        {
            Text = "Mode: Aimed",
            TooltipText = "Aimed: full aim, accurate. Snapshot: no aim, big penalty. Auto: picks by range.",
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _aimModeBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            int idx = System.Array.IndexOf(_aimModeCycle, _shownAimMode);
            var next = _aimModeCycle[(idx + 1) % _aimModeCycle.Length];
            Host.QueueCommand(new SetAimModeCommand(_shownPawnId, next));
        };
        _combatGroup.AddChild(_aimModeBtn);
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

        if (found is not { } p) { HideBar(); return; }

        _shownPawnId = p.EntityId;
        if (!_bar.Visible) _bar.Visible = true;

        _draftBtn.Text = p.Drafted ? "Undraft" : "Draft";
        // Combat controls only matter once drafted + holding a ranged weapon;
        // undrafted (or unarmed) shows just the Draft button.
        bool combat = p.Drafted && p.HasRangedWeapon;
        _combatGroup.Visible = combat;
        if (!combat)
        {
            var vp0 = GetViewport().GetVisibleRect().Size;
            _bar.Position = new Vector2((vp0.X - _bar.Size.X) * 0.5f, vp0.Y - _bar.Size.Y - MarginBottom);
            return;
        }

        for (int i = 0; i < _modes.Length; i++)
        {
            bool avail = (p.RangedModes & _modes[i].flag) != 0;
            _modeButtons[i].Visible = avail;
            _modeButtons[i].SetPressedNoSignal(avail && p.RangedMode == _modes[i].mode);
        }
        _magLabel.Text = $"Mag: {p.RangedMag}/{p.RangedMagSize}";

        _shownArea = p.RangedTargetArea;
        _targetAreaBtn.Text = $"Aim: {p.RangedTargetArea}";

        _shownAimMode = p.RangedAimMode;
        _aimModeBtn.Text = $"Mode: {p.RangedAimMode}";

        // Name the ranged button after the equipped weapon.
        string weaponName = "Fire";
        foreach (var eq in p.Equipped)
            if (ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var wd) && wd.Ranged is not null)
            { weaponName = wd.DisplayName; break; }
        _forceTargetBtn.Text = weaponName;

        if (Tools is not null)
        {
            _forceTargetBtn.SetPressedNoSignal(Tools.Mode == ToolMode.ForceFireTarget);
            _meleeBtn.SetPressedNoSignal(Tools.Mode == ToolMode.MeleeAttackTarget);
        }

        _fireAtWillBtn.SetPressedNoSignal(snap.FireAtWill);
        _fireAtWillBtn.Text = snap.FireAtWill ? "Fire at Will: On" : "Fire at Will: Off";

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
