using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for a single selected tree. Mirrors ItemInfoPanel's
// look. Shows tile + chop progress + job state, with Chop / Cancel
// buttons that wrap the existing rect commands at a 1x1 rect.
public partial class TreeInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 280;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private Button _chopBtn = null!;
    private Button _cancelBtn = null!;

    private int _shownEntityId = -1;
    private long _lastSnapshotTick = -1;

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 180),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        AddChild(_root);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 10, OffsetTop = 10, OffsetRight = -10, OffsetBottom = -10,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddThemeConstantOverride("separation", 6);
        _root.AddChild(vbox);

        var headerRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _nameLabel = new Label { Text = "Tree", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedTreeIds = Array.Empty<int>();
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);

        var btnRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btnRow.AddThemeConstantOverride("separation", 6);
        _chopBtn = new Button { Text = "Chop", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _chopBtn.Pressed += OnChopPressed;
        btnRow.AddChild(_chopBtn);
        _cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _cancelBtn.Pressed += OnCancelPressed;
        btnRow.AddChild(_cancelBtn);
        vbox.AddChild(btnRow);

        GetTree().Root.SizeChanged += Reposition;
        CallDeferred(nameof(Reposition));
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        var ids = Host.SelectedTreeIds;
        var snap = Host.LatestSnapshot;
        // Only show for single-tree selection — multi-tree uses the
        // designator flow (rect-chop), no per-tile panel needed.
        if (ids.Length != 1 || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownEntityId = -1; }
            return;
        }
        int id = ids[0];
        TreeState? found = null;
        foreach (var t in snap.Trees)
        {
            if (t.EntityId == id) { found = t; break; }
        }
        if (found is null)
        {
            // Tree felled — clear selection.
            Host.SelectedTreeIds = Array.Empty<int>();
            _root.Visible = false;
            _shownEntityId = -1;
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        if (found.Value.EntityId != _shownEntityId || snap.Tick != _lastSnapshotTick)
        {
            Render(found.Value);
            _shownEntityId = found.Value.EntityId;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, _root.Size.Y);
    }

    private void Render(TreeState t)
    {
        _nameLabel.Text = "Tree";
        _tileLabel.Text = $"Tile: ({t.Tile.X}, {t.Tile.Y})";
        if (t.HasJob)
        {
            int pct = Mathf.Clamp((int)Mathf.Round(t.ChopProgress * 100f), 0, 100);
            _stateLabel.Text = $"Chop job queued ({pct}%)";
            _chopBtn.Disabled = true;
            _cancelBtn.Disabled = false;
        }
        else
        {
            _stateLabel.Text = "Standing";
            _chopBtn.Disabled = false;
            _cancelBtn.Disabled = true;
        }
    }

    private void OnChopPressed()
    {
        if (Host is null) return;
        var ids = Host.SelectedTreeIds;
        if (ids.Length != 1) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        foreach (var t in snap.Trees)
        {
            if (t.EntityId != ids[0]) continue;
            Host.QueueCommand(new ChopTreesInRectCommand(t.Tile, t.Tile));
            return;
        }
    }

    private void OnCancelPressed()
    {
        if (Host is null) return;
        var ids = Host.SelectedTreeIds;
        if (ids.Length != 1) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        foreach (var t in snap.Trees)
        {
            if (t.EntityId != ids[0]) continue;
            Host.QueueCommand(new CancelJobsInRectCommand(t.Tile, t.Tile));
            return;
        }
    }
}
