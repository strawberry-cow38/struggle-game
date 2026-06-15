using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Shared frame for the right-side, entity-id-keyed selection info panels
// (Tree, Crop, Item). They all built the same CanvasLayer → Panel →
// VBox(header + close + separator) scaffold, the same show/hide +
// change-detect _Process loop (keyed on selection count + first id +
// snapshot tick), and the same bottom-left Reposition. Only the body
// controls and the per-tick Render content differ, plus which
// Host.SelectedXxxIds array the panel reflects.
//
// This is the id-keyed analogue of TileInfoPanel (tile-keyed). Godot C#
// can't share a single generic Node base, so the two bases coexist.
//
// Subclasses supply SelectedIds (the bound Host array, get+set), Title, the
// body (BuildBody) and the render pass (Render). NameLabel is exposed so
// Render can retitle for multi-select.
public abstract partial class EntityInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    protected const int PanelWidth = 560;
    private const int MarginLeft = 12;
    private const int MarginBottom = 16;
    private const int PanelPad = 10;

    private Panel _root = null!;
    private VBoxContainer _vbox = null!;
    private ScanlineStyleBox _panelBox = null!;
    private double _glowT;
    protected Label NameLabel = null!;

    private int _shownCount = -1;
    private int _shownFirstId = -1;
    private long _lastSnapshotTick = -1;

    // The Host selection array this panel reflects. The setter is used by the
    // shared close button and by Render when the selection goes stale.
    protected abstract int[] SelectedIds { get; set; }
    // Header text + default label.
    protected abstract string Title { get; }
    protected virtual int MinHeight => 350; // match the colonist pane footprint

    // Add labels/buttons under the header separator.
    protected abstract void BuildBody(VBoxContainer vbox);
    // Fill label text from the current selection + snapshot. May clear the
    // selection (assign SelectedIds) when entities go stale.
    protected abstract void Render(SimSnapshot snap, int[] ids);

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
        closeBtn.Pressed += () => SelectedIds = Array.Empty<int>();
        headerRow.AddChild(closeBtn);
        _vbox.AddChild(headerRow);

        _vbox.AddChild(new HSeparator());

        BuildBody(_vbox);

        GetTree().Root.SizeChanged += Reposition;
        Callable.From(Reposition).CallDeferred();
    }

    public override void _ExitTree()
    {
        GetTree().Root.SizeChanged -= Reposition;
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        var ids = SelectedIds;
        var snap = Host.LatestSnapshot;
        if (ids.Length == 0 || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownCount = -1; }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        _glowT += delta;
        UiTheme.AnimateGlow(_panelBox, _glowT);
        Reposition(); // re-anchor bottom-left as content height changes
        int first = ids[0];
        if (ids.Length != _shownCount || first != _shownFirstId || snap.Tick != _lastSnapshotTick)
        {
            Render(snap, ids);
            _shownCount = ids.Length;
            _shownFirstId = first;
            _lastSnapshotTick = snap.Tick;
        }
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
