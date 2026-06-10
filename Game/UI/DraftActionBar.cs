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

    private static readonly Color TileBg = UiTheme.PanelDeep;
    private static readonly Color BorderIdle = UiTheme.Border;
    private static readonly Color BorderActive = UiTheme.Accent;
    private static readonly Color CheckColor = UiTheme.Accent;
    // Pocket Sand cells use a fill tint (not a border) for the active weapon,
    // so the shared "+" gap between cells stays a single line. A visible line
    // layer sits behind the grid; its color shows through the 2px gaps.
    private static readonly Color CellActive = new(0.20f, 0.36f, 0.50f);
    private static readonly Color CardPad = new(0.04f, 0.03f, 0.08f); // dark outer padding
    private static readonly Color CellLine = UiTheme.Border;          // the shared "+" line

    private HBoxContainer _bar = null!;

    // Mag / ammo readout panel (left). Background bar fills with magazine
    // capacity; its color encodes the loaded ammo TYPE (not the amount).
    private Control _magPanel = null!;
    private ProgressBar _magBar = null!;
    private Label _magTitle = null!;
    private Label _magCount = null!;

    private Button _draftBtn = null!;
    private Label _draftCap = null!;

    // Pocket Sand — sidearm swap gizmo (leftmost drafted tile). One tile-sized
    // card split "+"-wise into a 2x2 grid of weapon squares (up to 3 weapons +
    // an Unarmed square); clicking a square swaps straight to it (active one
    // highlit).
    private Control _pocketSandWrap = null!;
    private GridContainer _segGrid = null!;
    private string _pocketSig = "";

    // Ranged-weapon tiles — shown whenever the pawn holds a ranged weapon
    // (drafted or not). Drafted-only tiles (fire-at-will, melee) are separate.
    private readonly List<Control> _rangedWraps = new();
    private readonly List<Control> _draftedWraps = new();

    private Button _forceTargetBtn = null!;
    private Label _forceTargetCap = null!;
    private Button _meleeBtn = null!;
    private Button _fireAtWillBtn = null!;
    private Button _reloadBtn = null!;
    private PopupMenu _reloadMenu = null!;
    private readonly List<string> _reloadAmmoPaths = new();

    private static readonly FireMode[] _modeOrder = { FireMode.Single, FireMode.Burst, FireMode.Auto };
    private static readonly FireModeFlags[] _modeFlags = { FireModeFlags.Single, FireModeFlags.Burst, FireModeFlags.Auto };
    private Button _fireModeBtn = null!;
    private FireMode _shownMode = FireMode.Single;
    private FireModeFlags _shownModes = FireModeFlags.Single;

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
        _bar.Theme = UiTheme.LabelTheme(); // outlined captions over the glass
        _bar.AddThemeConstantOverride("separation", 6);
        AddChild(_bar);

        _reloadMenu = new PopupMenu();
        AddChild(_reloadMenu);
        _reloadMenu.IdPressed += OnReloadMenuPick;

        // Left → right order (per design): mag · fire mode · reload · aim mode ·
        // target area · DRAFT · fire at will · unarmed · weapon. Every tile but
        // Draft is combat-only; the weapon tile sits rightmost.

        // Mag / ammo panel.
        _magPanel = BuildMagPanel();
        _bar.AddChild(_magPanel);
        _rangedWraps.Add(_magPanel);

        // Fire-mode tile — cycles through the weapon's available modes.
        _fireModeBtn = BuildGizmo("Fire Mode", toggle: false, parent: _bar, out _);
        _fireModeBtn.TooltipText = "Cycle the weapon's fire mode.";
        _fireModeBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            Host.QueueCommand(new SetFireModeCommand(_shownPawnId, NextMode(_shownMode, _shownModes)));
        };
        _rangedWraps.Add(WrapOf(_fireModeBtn));

        // Reload (left-click reload, right-click ammo/unload menu).
        _reloadBtn = BuildGizmo("Reload", toggle: false, parent: _bar, out _);
        _reloadBtn.TooltipText = "Left-click: reload. Right-click: pick ammo / unload.";
        _reloadBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            Host.QueueCommand(new ReloadWeaponCommand(_shownPawnId));
        };
        _reloadBtn.GuiInput += OnReloadGuiInput;
        _rangedWraps.Add(WrapOf(_reloadBtn));

        // Aim mode — cycles Aimed → Snapshot → Auto.
        _aimModeBtn = BuildGizmo("Aim Mode", toggle: false, parent: _bar, out _);
        _aimModeBtn.TooltipText = "Aimed: full aim, accurate. Snapshot: no aim, big penalty. Auto: picks by range.";
        _aimModeBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            int idx = System.Array.IndexOf(_aimModeCycle, _shownAimMode);
            var next = _aimModeCycle[(idx + 1) % _aimModeCycle.Length];
            Host.QueueCommand(new SetAimModeCommand(_shownPawnId, next));
        };
        _rangedWraps.Add(WrapOf(_aimModeBtn));

        // Target area — cycles Auto → Head → Torso → Legs.
        _targetAreaBtn = BuildGizmo("Target Area", toggle: false, parent: _bar, out _);
        _targetAreaBtn.TooltipText = "Body region the colonist aims for.";
        _targetAreaBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            int idx = System.Array.IndexOf(_areaCycle, _shownArea);
            var next = _areaCycle[(idx + 1) % _areaCycle.Length];
            Host.QueueCommand(new SetTargetAreaCommand(_shownPawnId, next));
        };
        _rangedWraps.Add(WrapOf(_targetAreaBtn));

        // Draft / Undraft tile — always shown while a colonist is selected.
        _draftBtn = BuildGizmo("Draft", toggle: false, parent: _bar, out _draftCap);
        _draftBtn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            Host.QueueCommand(new ToggleDraftCommand(_shownPawnId));
        };

        // Pocket Sand — segmented sidearm switcher. Sits right after Draft so
        // it's the leftmost drafted-only gizmo. Rebuilt from the pawn's weapons.
        _pocketSandWrap = BuildPocketSandCard();
        _bar.AddChild(_pocketSandWrap);
        _draftedWraps.Add(_pocketSandWrap);

        // Global "fire at will" toggle.
        _fireAtWillBtn = BuildGizmo("Fire at Will", toggle: true, parent: _bar, out _);
        _fireAtWillBtn.TooltipText = "On: colonists auto-engage enemies. Off: only fire at a target you assign via right-click.";
        _fireAtWillBtn.Pressed += () =>
        {
            if (Host is null) return;
            Host.QueueCommand(new SetFireAtWillCommand(_fireAtWillBtn.ButtonPressed));
        };
        _draftedWraps.Add(WrapOf(_fireAtWillBtn));

        // Unarmed / melee target mode.
        _meleeBtn = BuildGizmo("Unarmed", toggle: true, parent: _bar, out _);
        _meleeBtn.TooltipText = "Click, then a pawn, to melee it.";
        _meleeBtn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = _meleeBtn.ButtonPressed ? ToolMode.MeleeAttackTarget : ToolMode.None;
        };
        _draftedWraps.Add(WrapOf(_meleeBtn));

        // Weapon tile (rightmost) = ranged target mode (click, then a pawn, to
        // set a shoot target). Caption is the equipped weapon name, set each frame.
        _forceTargetBtn = BuildGizmo("Fire", toggle: true, parent: _bar, out _forceTargetCap);
        _forceTargetBtn.TooltipText = "Click, then a pawn, to set a shoot target.";
        _forceTargetBtn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = _forceTargetBtn.ButtonPressed ? ToolMode.ForceFireTarget : ToolMode.None;
        };
        _rangedWraps.Add(WrapOf(_forceTargetBtn));
    }

    private static Control WrapOf(Button tile) => (Control)tile.GetParent();

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

        // Ranged tiles show whenever the pawn has a ranged weapon (drafted or
        // not); fire-at-will + melee only once drafted.
        foreach (var w in _rangedWraps) w.Visible = p.HasRangedWeapon;
        foreach (var w in _draftedWraps) w.Visible = p.Drafted;

        if (p.HasRangedWeapon)
        {
            _shownMode = p.RangedMode;
            _shownModes = p.RangedModes;
            _fireModeBtn.Text = p.RangedMode.ToString();

            // Weapon name → force-target caption; mag panel = ammo type + count.
            string weaponName = "Weapon";
            foreach (var eq in p.Equipped)
                if (ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var wd) && wd.Ranged is not null)
                { weaponName = wd.DisplayName; break; }
            _magTitle.Text = AmmoLongName(p.LoadedAmmoPath);
            _magCount.Text = $"{p.RangedMag} / {p.RangedMagSize}";
            _magBar.Value = p.RangedMagSize > 0 ? (float)p.RangedMag / p.RangedMagSize : 0f;
            _magBar.AddThemeStyleboxOverride("fill", MakeBox(AmmoColor(p.LoadedAmmoPath), default, 0, 4));
            _forceTargetCap.Text = weaponName;

            _shownArea = p.RangedTargetArea;
            _targetAreaBtn.Text = p.RangedTargetArea.ToString();

            _shownAimMode = p.RangedAimMode;
            _aimModeBtn.Text = p.RangedAimMode.ToString();

            if (Tools is not null)
                SetTileActive(_forceTargetBtn, Tools.Mode == ToolMode.ForceFireTarget);
        }

        if (p.Drafted)
        {
            RebuildPocketSand(p);
            if (Tools is not null)
                SetTileActive(_meleeBtn, Tools.Mode == ToolMode.MeleeAttackTarget);
            SetTileActive(_fireAtWillBtn, snap.FireAtWill);
        }

        Recenter();
    }

    // Short ammo tag for the loaded round: the parenthetical from the ammo's
    // display name ("5.56x45mm NATO (FMJ)" → "FMJ"), else the caliber, else "—".
    private static string AmmoTag(string? path)
    {
        if (path is null || !ItemCatalog.ItemsByPath.TryGetValue(path, out var def)) return "—";
        string n = def.DisplayName;
        int a = n.IndexOf('('), b = n.IndexOf(')');
        if (a >= 0 && b > a) return n.Substring(a + 1, b - a - 1);
        return n;
    }

    // Long-form name for the loaded round, for the mag-panel caption.
    private static string AmmoLongName(string? path) => AmmoTag(path).ToUpperInvariant() switch
    {
        "FMJ" => "Full Metal Jacket",
        "HP" => "Hollow Point",
        "AP" => "Armor Piercing",
        _ => AmmoTag(path), // already a full name (caliber) or "—"
    };

    // Fill color encodes ammo TYPE (not remaining amount).
    private static Color AmmoColor(string? path) => AmmoTag(path).ToUpperInvariant() switch
    {
        "FMJ" => new Color(0.40f, 0.74f, 0.34f), // green
        "AP" => new Color(0.78f, 0.30f, 0.26f),  // red
        "HP" => new Color(0.86f, 0.74f, 0.20f),  // yellow
        _ => new Color(0.40f, 0.42f, 0.48f),     // gray
    };

    // Next available fire mode after `cur`, skipping modes the weapon lacks.
    private static FireMode NextMode(FireMode cur, FireModeFlags avail)
    {
        int start = System.Array.IndexOf(_modeOrder, cur);
        for (int k = 1; k <= _modeOrder.Length; k++)
        {
            int j = (start + k) % _modeOrder.Length;
            if ((avail & _modeFlags[j]) != 0) return _modeOrder[j];
        }
        return cur;
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
        tile.AddThemeStyleboxOverride("normal", ScanTile(TileBg, BorderIdle));
        tile.AddThemeStyleboxOverride("hover", ScanTile(TileBg.Lightened(0.06f), BorderIdle));
        tile.AddThemeStyleboxOverride("pressed", ScanTile(TileBg, BorderIdle));
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
        tile.AddThemeStyleboxOverride("normal", ScanTile(TileBg, border));
        tile.AddThemeStyleboxOverride("pressed", ScanTile(TileBg, border));
        if (tile.ToggleMode)
        {
            tile.SetPressedNoSignal(active);
            tile.Text = active ? "✓" : "";
            tile.AddThemeColorOverride("font_color", CheckColor);
        }
    }

    // Mag panel mirrors a gizmo: a square tile (big mag count on a dark
    // inset) with the weapon name as the caption beneath, so it lines up to
    // the same height as the buttons.
    private Control BuildMagPanel()
    {
        var wrap = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        wrap.AddThemeConstantOverride("separation", 2);

        var tile = new Control { CustomMinimumSize = new Vector2(96, TileSize) };

        // Capacity bar fills the tile; fill color set per ammo type each frame.
        _magBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Step = 0.0001, ShowPercentage = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _magBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _magBar.AddThemeStyleboxOverride("background", MakeBox(UiTheme.Inset, BorderIdle, 2, 4, 0));
        _magBar.AddThemeStyleboxOverride("fill", MakeBox(new Color(0.40f, 0.42f, 0.48f), default, 0, 4));
        tile.AddChild(_magBar);

        _magCount = new Label
        {
            Text = "0 / 0",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _magCount.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _magCount.AddThemeFontSizeOverride("font_size", 18);
        _magCount.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 0.98f));
        _magCount.AddThemeConstantOverride("outline_size", 4);
        _magCount.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.7f));
        tile.AddChild(_magCount);
        wrap.AddChild(tile);

        _magTitle = new Label
        {
            Text = "Weapon",
            CustomMinimumSize = new Vector2(96, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _magTitle.AddThemeFontSizeOverride("font_size", 11);
        wrap.AddChild(_magTitle);

        return wrap;
    }

    // A gizmo tile background with the VFD scan-line grid (matches the panes).
    private static ScanlineStyleBox ScanTile(Color bg, Color border)
        => UiTheme.Scan(MakeBox(bg, border, 2, 4), inset: 4f);

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
            _reloadMenu.AddItem($"Reload: {AmmoLongName(h.ItemPath)} ({h.Count})", id);
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

    // The Pocket Sand card: a single tile-sized panel holding a 2x2 grid of
    // weapon squares (the dark "+" gap between them is the divider), with the
    // caption beneath. Squares are filled in by RebuildPocketSand.
    private Control BuildPocketSandCard()
    {
        var wrap = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        wrap.AddThemeConstantOverride("separation", 2);

        // Dark card backing — the 3px grid gaps show it through as the "+".
        var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(TileSize, TileSize) };
        card.AddThemeStyleboxOverride("panel", MakeBox(CardPad, BorderIdle, 2, 4, 3));
        // Line layer behind the grid: its color peeks through the 2px gaps as a
        // faux "+" between the four even cells.
        var lineLayer = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        lineLayer.AddThemeStyleboxOverride("panel", MakeBox(CellLine, default, 0, 0));
        card.AddChild(lineLayer);
        _segGrid = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Pass };
        _segGrid.AddThemeConstantOverride("h_separation", 2);
        _segGrid.AddThemeConstantOverride("v_separation", 2);
        lineLayer.AddChild(_segGrid);
        wrap.AddChild(card);
        return wrap; // no caption — the grid speaks for itself
    }

    // One weapon square: path, icon kind, active flag. Path "" = Unarmed,
    // a null entry = an empty square.
    private readonly record struct WpnSeg(string Path, WeaponGlyph.Kind Kind, bool Active);

    // Up to 3 carried weapons (equipped + pocketed, de-duped) fill the first
    // three squares; the 4th is always Unarmed. Empty squares pad the grid to
    // four. Rebuild only when the set changes.
    private void RebuildPocketSand(in DummyState p)
    {
        var weapons = new List<WpnSeg>();
        var seen = new HashSet<string>();
        bool anyEquipped = false;

        void Consider(string path, bool equipped)
        {
            if (!ItemCatalog.ItemsByPath.TryGetValue(path, out var def)) return;
            if (!def.IsWeapon && !def.IsRangedWeapon) return;
            if (equipped) anyEquipped = true;
            if (!seen.Add(path)) { if (equipped) MarkActive(weapons, path); return; }
            weapons.Add(new WpnSeg(path, def.IsRangedWeapon ? WeaponGlyph.Kind.Ranged : WeaponGlyph.Kind.Melee, equipped));
        }

        foreach (var eq in p.Equipped) Consider(eq.ItemPath, equipped: true);
        foreach (var h in p.Held) Consider(h.ItemPath, equipped: false);

        // Four squares: up to 3 weapons, empties, then Unarmed in the last.
        var slots = new List<WpnSeg?>();
        for (int i = 0; i < 3; i++) slots.Add(i < weapons.Count ? weapons[i] : (WpnSeg?)null);
        slots.Add(new WpnSeg("", WeaponGlyph.Kind.Unarmed, !anyEquipped));

        var sb = new System.Text.StringBuilder();
        foreach (var s in slots) sb.Append(s is { } v ? v.Path + (v.Active ? "1" : "0") : "_").Append('|');
        string sig = sb.ToString();
        if (sig == _pocketSig) return;
        _pocketSig = sig;

        foreach (var child in _segGrid.GetChildren()) { _segGrid.RemoveChild(child); child.QueueFree(); }
        foreach (var s in slots) _segGrid.AddChild(BuildSquare(s));
    }

    private static void MarkActive(List<WpnSeg> segs, string path)
    {
        for (int i = 0; i < segs.Count; i++)
            if (segs[i].Path == path) { segs[i] = segs[i] with { Active = true }; return; }
    }

    // Per-item pixel-art icons, loaded once from assets/items/<file>.png at
    // runtime (no Godot import, same as the ground texture). null = no art,
    // fall back to the vector glyph. Maps item key -> asset file.
    private static readonly Dictionary<string, ImageTexture?> _itemIconCache = new();
    private static ImageTexture? ItemIcon(string itemPath)
    {
        if (string.IsNullOrEmpty(itemPath)) return null;
        if (_itemIconCache.TryGetValue(itemPath, out var cached)) return cached;
        // ItemsByPath is keyed by FullPath ("Equipment/AssaultRifle"); match on
        // the item Id so category nesting doesn't matter.
        string? id = ItemCatalog.ItemsByPath.TryGetValue(itemPath, out var def) ? def.Id : null;
        string? file = id switch { "AssaultRifle" => "m16", _ => null };
        ImageTexture? tex = null;
        if (file is not null)
        {
            var img = new Image();
            if (img.Load(ProjectSettings.GlobalizePath($"res://assets/items/{file}.png")) == Error.Ok)
                tex = ImageTexture.CreateFromImage(img);
        }
        _itemIconCache[itemPath] = tex;
        return tex;
    }

    private Control BuildSquare(WpnSeg? seg)
    {
        // (tile - 2*border - 2*margin - gap) / 2 — the 2x2 fills the card.
        int q = (TileSize - 4 - 6 - 2) / 2;

        // No cell borders: the shared "+" gap is the only divider. Empty
        // square = a darker filler, not clickable.
        if (seg is not { } s)
        {
            var blank = new PanelContainer
            {
                CustomMinimumSize = new Vector2(q, q),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            blank.AddThemeStyleboxOverride("panel", MakeBox(TileBg.Darkened(0.30f), default, 0, 0));
            return blank;
        }

        var btn = new Button
        {
            CustomMinimumSize = new Vector2(q, q),
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            TooltipText = s.Path == "" ? "Go unarmed (stash your weapon)"
                : ItemCatalog.ItemsByPath.TryGetValue(s.Path, out var d) ? d.DisplayName : s.Path,
        };
        var fill = s.Active ? CellActive : TileBg;
        btn.AddThemeStyleboxOverride("normal", MakeBox(fill, default, 0, 0));
        btn.AddThemeStyleboxOverride("hover", MakeBox(fill.Lightened(0.10f), default, 0, 0));
        btn.AddThemeStyleboxOverride("pressed", MakeBox(fill, default, 0, 0));

        // Pixel-art icon if the item has one (assets/items/<key>.png);
        // otherwise the vector WeaponGlyph for that weapon kind.
        Control icon;
        var tex = ItemIcon(s.Path);
        if (tex is not null)
            icon = new TextureRect
            {
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
        else
            icon = new WeaponGlyph { Glyph = s.Kind, MouseFilter = Control.MouseFilterEnum.Ignore };
        icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        icon.OffsetLeft = 4; icon.OffsetRight = -4; icon.OffsetTop = 4; icon.OffsetBottom = -4;
        btn.AddChild(icon);

        string path = s.Path;
        btn.Pressed += () =>
        {
            if (Host is null || _shownPawnId < 0) return;
            Host.QueueCommand(new SwapToWeaponCommand(_shownPawnId, path));
        };
        return btn;
    }

    private void HideBar()
    {
        if (_bar.Visible) { _bar.Visible = false; _shownPawnId = -1; }
    }
}
