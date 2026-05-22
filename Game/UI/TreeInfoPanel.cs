using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for the selected tree(s). Mirrors ItemInfoPanel.
// Single selection shows tile + chop progress. Multi-selection shows
// aggregate counts. Chop / Cancel buttons fire one 1x1 rect command
// per selected tree so the existing rect plumbing handles every tile.
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

    private int _shownCount = -1;
    private int _shownFirstId = -1;
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
        if (ids.Length == 0 || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownCount = -1; }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
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
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, _root.Size.Y);
    }

    private void Render(SimSnapshot snap, int[] ids)
    {
        var idSet = new HashSet<int>(ids);
        int withJob = 0, standing = 0, missing = 0;
        TreeState? first = null;
        foreach (var t in snap.Trees)
        {
            if (!idSet.Contains(t.EntityId)) continue;
            if (first is null) first = t;
            if (t.HasJob) withJob++; else standing++;
            idSet.Remove(t.EntityId);
        }
        // Anything left in idSet was felled out from under the selection.
        missing = idSet.Count;
        if (missing > 0 && withJob + standing == 0)
        {
            Host!.SelectedTreeIds = Array.Empty<int>();
            return;
        }

        if (ids.Length == 1 && first is TreeState t1)
        {
            _nameLabel.Text = "Tree";
            _tileLabel.Text = $"Tile: ({t1.Tile.X}, {t1.Tile.Y})";
            int growPct = Mathf.Clamp((int)Mathf.Round(t1.GrowthStage * 100f), 0, 100);
            string growth = $"Growth {growPct}%";
            if (t1.HasJob)
            {
                int pct = Mathf.Clamp((int)Mathf.Round(t1.ChopProgress * 100f), 0, 100);
                _stateLabel.Text = $"Chop job queued ({pct}%)\n{growth}";
            }
            else
            {
                _stateLabel.Text = $"Standing\n{growth}";
            }
        }
        else
        {
            _nameLabel.Text = $"Trees ({ids.Length})";
            _tileLabel.Text = first is TreeState f
                ? $"First: ({f.Tile.X}, {f.Tile.Y})"
                : "";
            _stateLabel.Text = $"{withJob} queued · {standing} standing";
        }
        _chopBtn.Disabled = standing == 0;
        _cancelBtn.Disabled = withJob == 0;
    }

    private void OnChopPressed()
    {
        if (Host is null) return;
        var ids = Host.SelectedTreeIds;
        if (ids.Length == 0) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        var idSet = new HashSet<int>(ids);
        foreach (var t in snap.Trees)
        {
            if (!idSet.Contains(t.EntityId)) continue;
            if (t.HasJob) continue;
            Host.QueueCommand(new ChopTreesInRectCommand(t.Tile, t.Tile));
        }
    }

    private void OnCancelPressed()
    {
        if (Host is null) return;
        var ids = Host.SelectedTreeIds;
        if (ids.Length == 0) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        var idSet = new HashSet<int>(ids);
        foreach (var t in snap.Trees)
        {
            if (!idSet.Contains(t.EntityId)) continue;
            if (!t.HasJob) continue;
            Host.QueueCommand(new CancelJobsInRectCommand(t.Tile, t.Tile));
        }
    }
}
