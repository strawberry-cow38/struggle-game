using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Shared frame for the right-side, tile-keyed selection info panels (Wall,
// UrBoard, Lamp, Door, Bed, Stove, Blueprint). They all built the same
// CanvasLayer → Panel → VBox(header + close + separator) scaffold, the
// same show/hide + change-detect _Process loop, and the same top-right
// Reposition. Only the body controls and the per-tick Render content
// differ, plus which Host.SelectedXxxTiles array the panel reflects.
//
// Subclasses supply SelectedTiles (the bound Host array), Title, the body
// (BuildBody) and the render pass (Render). NameLabel is exposed so Render
// can retitle for multi-select. (Id-keyed panels — Tree/Crop/Item — use a
// separate base since Godot C# can't share a generic Node base.)
public abstract partial class TileInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    protected const int PanelWidth = 560;
    private const int MarginLeft = 12;
    private const int MarginBottom = 16;
    private const int PanelPad = 10;

    private Panel _root = null!;
    private VBoxContainer _vbox = null!;
    private StyleBoxFlat _panelBox = null!;
    private double _glowT;
    protected Label NameLabel = null!;

    private TilePos[] _shownTiles = Array.Empty<TilePos>();
    private long _lastSnapshotTick = -1;

    // The Host selection array this panel reflects.
    protected abstract TilePos[] SelectedTiles { get; set; }
    // Header text + default label.
    protected abstract string Title { get; }
    protected virtual int MinHeight => 160;

    // Add labels/buttons under the header separator.
    protected abstract void BuildBody(VBoxContainer vbox);
    // Fill label text from the current selection + snapshot. May shrink the
    // selection (assign SelectedTiles) when tiles go stale.
    protected abstract void Render(SimSnapshot snap, TilePos[] tiles);

    public override void _Ready()
    {
        Layer = 95;
        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, MinHeight),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        AddChild(new GlassBackdrop { Target = _root, Corner = 12f }); // frosted blur behind
        AddChild(_root);

        // Ethereal glass card — same dreamcore theme as the colonist pane.
        _panelBox = UiTheme.PanelBox(corner: 12, margin: 10);
        _root.AddThemeStyleboxOverride("panel", _panelBox);
        _root.Theme = UiTheme.LabelTheme();

        _vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 10, OffsetTop = 10, OffsetRight = -10, OffsetBottom = -10,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _vbox.AddThemeConstantOverride("separation", 6);
        _root.AddChild(_vbox);

        var headerRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        NameLabel = new Label { Text = Title, CustomMinimumSize = new Vector2(0, 28) };
        NameLabel.AddThemeFontSizeOverride("font_size", 22);
        NameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(NameLabel);

        // Close: the shared red-box white-X button.
        var closeBtn = UiTheme.CloseButton();
        closeBtn.Pressed += () => SelectedTiles = Array.Empty<TilePos>();
        headerRow.AddChild(closeBtn);
        _vbox.AddChild(headerRow);

        _vbox.AddChild(new HSeparator());

        BuildBody(_vbox);

        GetTree().Root.SizeChanged += Reposition;
        Callable.From(Reposition).CallDeferred();
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        var tiles = SelectedTiles;
        var snap = Host.LatestSnapshot;
        if (tiles.Length == 0 || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownTiles = Array.Empty<TilePos>(); }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        _glowT += delta;
        UiTheme.AnimateGlow(_panelBox, _glowT);
        Reposition(); // re-anchor bottom-left as content height changes
        if (!TilesEqual(tiles, _shownTiles) || snap.Tick != _lastSnapshotTick)
        {
            Render(snap, tiles);
            _shownTiles = tiles;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private static bool TilesEqual(TilePos[] a, TilePos[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        // Size to content so the bottom padding matches the sides, anchored
        // bottom-left like the colonist pane.
        float h = Math.Max(MinHeight, _vbox.GetCombinedMinimumSize().Y + PanelPad * 2);
        _root.Size = new Vector2(PanelWidth, h);
        _root.Position = new Vector2(MarginLeft, vp.Y - h - MarginBottom);
    }
}
