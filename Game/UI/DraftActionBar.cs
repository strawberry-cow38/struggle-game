using System.Collections.Generic;
using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Game.Tools;

namespace StruggleGame.Game.UI;

// Bottom-center action bar shown when a single colonist is selected, styled
// as RimWorld-ish "gizmo" tiles: a square button with a caption beneath it,
// active toggles tinted green with a check. A Draft/Undraft tile is always
// present; the combat tiles + the mag/ammo panel appear once drafted and
// holding a ranged weapon. Polls the snapshot each frame.
public partial class DraftActionBar : CanvasLayer
{
    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private const int TileSize = 72;
    private const float MarginBottom = 14f;
    private const int UnloadMenuId = 10000;

    private static readonly Color TileBg = new(0.16f, 0.17f, 0.20f);
    private static readonly Color BorderIdle = new(0.35f, 0.37f, 0.42f);
    private static readonly Color BorderActive = new(0.35f, 0.78f, 0.40f);
    private static readonly Color CheckColor = new(0.45f, 0.92f, 0.50f);

    private HBoxContainer _bar = null!;

    // Mag / ammo readout panel (left).
    private Control _magPanel = null!;
    private Label _magTitle = null!;
    private Label _magCount = null!;

    private Button _draftBtn = null!;
    private Label _draftCap = null!;

    private HBoxContainer _combatGroup = null!;

    private Button _forceTargetBtn = null!;
    private Label _forceTargetCap = null!;
    private Button _meleeBtn = null!;
    private Button _fireAtWillBtn = null!;
    private Button _reloadBtn = null!;
    private PopupMenu _reloadMenu = null!;
    private readonly List<string> _reloadAmmoPaths = new();

    private readonly (FireMode mode, FireModeFlags flag)[] _modes =
    {
        (FireMode.Single, FireModeFlags.Single),
        (FireMode.Burst, FireModeFlags.Burst),
        (FireMode.Auto, FireModeFlags.Auto),
    };
    private readonly Control[] _modeWraps = new Control[3];
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

        _bar = new HBoxContainer { Name = "DraftRow", Visible = false, MouseFilter = Control.MouseFilterEnum.Pass };
        _bar.AddThemeConstantOverride("separation", 6);
        AddChild(_bar);

        // Mag / ammo panel — weapon name on top, big mag count below.
        _magPanel = BuildMagPanel();
        _bar.AddChild(_magPanel);

        // Draft / Undraft tile — always shown while a colonist is selected.
        _draftBtn = BuildGizmo("Draft", toggle: false, parent: _bar, out _draftCap);
        _draftBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            Host.QueueCommand(new ToggleDraftCommand(_shownPawnId));
        };

        // Combat tiles — only visible once drafted + holding a ranged weapon.
        _combatGroup = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _combatGroup.AddThemeConstantOverride("separation", 6);
        _bar.AddChild(_combatGroup);

        // Fire-mode tiles (Single / Burst / Auto).
        for (int i = 0; i < _modes.Length; i++)
        {
            var m = _modes[i].mode;
            var btn = BuildGizmo(m.ToString(), toggle: true, parent: _combatGroup, out _);
            btn.Pressed += () =>
            {
                if (Host is null || _shownPawnId < 0) return;
                Host.QueueCommand(new SetFireModeCommand(_shownPawnId, m));
            };
            _modeWraps[i] = (Control)btn.GetParent();
            _modeButtons[i] = btn;
        }

        // Reload (left-click reload, right-click ammo/unload menu).
        _reloadBtn = BuildGizmo("Reload", toggle: false, parent: _combatGroup, out _);
        _reloadBtn.TooltipText = "Left-click: reload. Right-click: pick ammo / unload.";
        _reloadBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            Host.QueueCommand(new ReloadWeaponCommand(_shownPawnId));
        };
        _reloadBtn.GuiInput += OnReloadGuiInput;

        _reloadMenu = new PopupMenu();
        AddChild(_reloadMenu);
        _reloadMenu.IdPressed += OnReloadMenuPick;

        // Weapon tile = ranged target mode (click, then a pawn, to set a shoot
        // target). Caption is the equipped weapon name, set each frame.
        _forceTargetBtn = BuildGizmo("Fire", toggle: true, parent: _combatGroup, out _forceTargetCap);
        _forceTargetBtn.TooltipText = "Click, then a pawn, to set a shoot target.";
        _forceTargetBtn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = _forceTargetBtn.ButtonPressed ? ToolMode.ForceFireTarget : ToolMode.None;
        };

        // Melee target mode.
        _meleeBtn = BuildGizmo("Melee Attack", toggle: true, parent: _combatGroup, out _);
        _meleeBtn.TooltipText = "Click, then a pawn, to melee it.";
        _meleeBtn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = _meleeBtn.ButtonPressed ? ToolMode.MeleeAttackTarget : ToolMode.None;
        };

        // Global "fire at will" toggle.
        _fireAtWillBtn = BuildGizmo("Fire at Will", toggle: true, parent: _combatGroup, out _);
        _fireAtWillBtn.TooltipText = "On: colonists auto-engage enemies. Off: only fire at a target you assign via right-click.";
        _fireAtWillBtn.Pressed += () =>
        {
            if (Host is null) return;
            Host.QueueCommand(new SetFireAtWillCommand(_fireAtWillBtn.ButtonPressed));
        };

        // Target area — cycles Auto → Head → Torso → Legs.
        _targetAreaBtn = BuildGizmo("Target Area", toggle: false, parent: _combatGroup, out _);
        _targetAreaBtn.TooltipText = "Body region the colonist aims for.";
        _targetAreaBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            int idx = System.Array.IndexOf(_areaCycle, _shownArea);
            var next = _areaCycle[(idx + 1) % _areaCycle.Length];
            Host.QueueCommand(new SetTargetAreaCommand(_shownPawnId, next));
        };

        // Aim mode — cycles Aimed → Snapshot → Auto.
        _aimModeBtn = BuildGizmo("Aim Mode", toggle: false, parent: _combatGroup, out _);
        _aimModeBtn.TooltipText = "Aimed: full aim, accurate. Snapshot: no aim, big penalty. Auto: picks by range.";
        _aimModeBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            int idx = System.Array.IndexOf(_aimModeCycle, _shownAimMode);
            var next = _aimModeCycle[(idx + 1) % _aimModeCycle.Length];
            Host.QueueCommand(new SetAimModeCommand(_shownPawnId, next));
        };
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

        _draftCap.Text = p.Drafted ? "Undraft" : "Draft";
        SetTileActive(_draftBtn, p.Drafted);

        bool combat = p.Drafted && p.HasRangedWeapon;
        _combatGroup.Visible = combat;
        _magPanel.Visible = combat;
        if (!combat) { Recenter(); return; }

        for (int i = 0; i < _modes.Length; i++)
        {
            bool avail = (p.RangedModes & _modes[i].flag) != 0;
            _modeWraps[i].Visible = avail;
            SetTileActive(_modeButtons[i], avail && p.RangedMode == _modes[i].mode);
        }

        // Weapon name → mag panel title + force-target caption.
        string weaponName = "Weapon";
        foreach (var eq in p.Equipped)
            if (ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var wd) && wd.Ranged is not null)
            { weaponName = wd.DisplayName; break; }
        _magTitle.Text = weaponName;
        _magCount.Text = $"{p.RangedMag} / {p.RangedMagSize}";
        _forceTargetCap.Text = weaponName;

        _shownArea = p.RangedTargetArea;
        _targetAreaBtn.Text = p.RangedTargetArea.ToString();

        _shownAimMode = p.RangedAimMode;
        _aimModeBtn.Text = p.RangedAimMode.ToString();

        if (Tools is not null)
        {
            SetTileActive(_forceTargetBtn, Tools.Mode == ToolMode.ForceFireTarget);
            SetTileActive(_meleeBtn, Tools.Mode == ToolMode.MeleeAttackTarget);
        }

        SetTileActive(_fireAtWillBtn, snap.FireAtWill);

        Recenter();
    }

    private void Recenter()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _bar.Position = new Vector2((vp.X - _bar.Size.X) * 0.5f, vp.Y - _bar.Size.Y - MarginBottom);
    }

    // A gizmo tile = a square Button with a caption Label beneath it, wrapped
    // in a VBox. `toggle` tiles flip to a green border + check when active.
    private Button BuildGizmo(string caption, bool toggle, Control parent, out Label captionLabel)
    {
        var wrap = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        wrap.AddThemeConstantOverride("separation", 2);

        var tile = new Button
        {
            ToggleMode = toggle,
            CustomMinimumSize = new Vector2(TileSize, TileSize),
            FocusMode = Control.FocusModeEnum.None,
            ClipText = true,
        };
        var box = MakeBox(TileBg, BorderIdle, 2, 4);
        tile.AddThemeStyleboxOverride("normal", box);
        tile.AddThemeStyleboxOverride("hover", MakeBox(TileBg.Lightened(0.06f), BorderIdle, 2, 4));
        tile.AddThemeStyleboxOverride("pressed", box);
        tile.AddThemeFontSizeOverride("font_size", 14);
        wrap.AddChild(tile);

        captionLabel = new Label
        {
            Text = caption,
            CustomMinimumSize = new Vector2(TileSize, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        captionLabel.AddThemeFontSizeOverride("font_size", 11);
        wrap.AddChild(captionLabel);

        parent.AddChild(wrap);
        return tile;
    }

    // Toggle tiles: green border + check when active, plain otherwise. For
    // value tiles (Text holds a value) this just swaps the border color.
    private static void SetTileActive(Button tile, bool active)
    {
        var border = active ? BorderActive : BorderIdle;
        tile.AddThemeStyleboxOverride("normal", MakeBox(TileBg, border, 2, 4));
        tile.AddThemeStyleboxOverride("pressed", MakeBox(TileBg, border, 2, 4));
        if (tile.ToggleMode)
        {
            tile.SetPressedNoSignal(active);
            tile.Text = active ? "✓" : "";
            tile.AddThemeColorOverride("font_color", CheckColor);
        }
    }

    private Control BuildMagPanel()
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(132, TileSize) };
        panel.AddThemeStyleboxOverride("panel", MakeBox(TileBg, BorderIdle, 2, 4, 6));

        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        col.AddThemeConstantOverride("separation", 4);
        panel.AddChild(col);

        _magTitle = new Label
        {
            Text = "Weapon",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _magTitle.AddThemeFontSizeOverride("font_size", 12);
        col.AddChild(_magTitle);

        // Big mag count on a darker inset, like the screenshot.
        var inset = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        inset.AddThemeStyleboxOverride("panel", MakeBox(new Color(0.10f, 0.11f, 0.13f), new Color(0.28f, 0.30f, 0.34f), 1, 3, 2));
        _magCount = new Label
        {
            Text = "0 / 0",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _magCount.AddThemeFontSizeOverride("font_size", 18);
        _magCount.AddThemeColorOverride("font_color", new Color(0.78f, 0.86f, 0.88f));
        inset.AddChild(_magCount);
        col.AddChild(inset);

        return panel;
    }

    private static StyleBoxFlat MakeBox(Color bg, Color border, int borderWidth, int corner, int margin = 0)
    {
        var box = new StyleBoxFlat { BgColor = bg };
        box.BorderColor = border;
        box.BorderWidthLeft = box.BorderWidthRight = box.BorderWidthTop = box.BorderWidthBottom = borderWidth;
        box.CornerRadiusTopLeft = box.CornerRadiusTopRight = box.CornerRadiusBottomLeft = box.CornerRadiusBottomRight = corner;
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = margin;
        return box;
    }

    // Right-click the Reload tile → choose an ammo type the colonist is
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
