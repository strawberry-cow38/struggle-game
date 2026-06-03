using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for the selected crop(s). Mirrors TreeInfoPanel.
// Action button is "Harvest" when the selection contains any crop at
// >= HarvestMinGrowth, otherwise "Cut" (the verb that clears immature
// crops with no yield).
public partial class CropInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 560;
    private const int MarginLeft = 12;
    private const int MarginBottom = 16;
    private const int PanelPad = 10;

    // Mirror of SimRuntime.HarvestMinGrowthStage. Below this crops yield
    // nothing on harvest, so the action button swaps to "Cut".
    private const float HarvestMinGrowth = 0.75f;

    private Panel _root = null!;
    private VBoxContainer _vbox = null!;
    private StyleBoxFlat _panelBox = null!;
    private double _glowT;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private Button _harvestBtn = null!;
    private Button _cutBtn = null!;
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
        AddChild(new GlassBackdrop { Target = _root, Corner = 12f }); // frosted blur behind
        AddChild(_root);

        // Same dreamcore glass frame as the colonist pane.
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
        _nameLabel = new Label { Text = "Crop", CustomMinimumSize = new Vector2(0, 28) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 22);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = UiTheme.CloseButton();
        closeBtn.Pressed += () => Host!.SelectedCropIds = Array.Empty<int>();
        headerRow.AddChild(closeBtn);
        _vbox.AddChild(headerRow);

        _vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        _vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        _vbox.AddChild(_stateLabel);

        var btnRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btnRow.AddThemeConstantOverride("separation", 6);
        _harvestBtn = new Button { Text = "Harvest", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _harvestBtn.Pressed += OnHarvestPressed;
        btnRow.AddChild(_harvestBtn);
        _cutBtn = new Button { Text = "Cut", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _cutBtn.Pressed += OnCutPressed;
        btnRow.AddChild(_cutBtn);
        _cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _cancelBtn.Pressed += OnCancelPressed;
        btnRow.AddChild(_cancelBtn);
        _vbox.AddChild(btnRow);

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
        var ids = Host.SelectedCropIds;
        var snap = Host.LatestSnapshot;
        if (ids.Length == 0 || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownCount = -1; }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        _glowT += delta;
        UiTheme.AnimateGlow(_panelBox, _glowT);
        Reposition();
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
        float h = Math.Max(180, _vbox.GetCombinedMinimumSize().Y + PanelPad * 2);
        _root.Size = new Vector2(PanelWidth, h);
        _root.Position = new Vector2(MarginLeft, vp.Y - h - MarginBottom);
    }

    private void Render(SimSnapshot snap, int[] ids)
    {
        var idSet = new HashSet<int>(ids);
        int withJob = 0, growing = 0;
        int growingMature = 0, growingImmature = 0;
        CropState? first = null;
        foreach (var c in snap.Crops)
        {
            if (!idSet.Contains(c.EntityId)) continue;
            if (first is null) first = c;
            if (c.ActiveJob is not null) withJob++;
            else
            {
                growing++;
                if (c.GrowthStage >= HarvestMinGrowth) growingMature++;
                else growingImmature++;
            }
            idSet.Remove(c.EntityId);
        }
        // Anything left in idSet was cut/harvested out from under the selection.
        int missing = idSet.Count;
        if (missing > 0 && withJob + growing == 0)
        {
            Host!.SelectedCropIds = Array.Empty<int>();
            return;
        }

        if (ids.Length == 1 && first is CropState c1)
        {
            _nameLabel.Text = c1.Kind.ToString();
            _tileLabel.Text = $"Tile: ({c1.Tile.X}, {c1.Tile.Y})";
            int growPct = Mathf.Clamp((int)Mathf.Round(c1.GrowthStage * 100f), 0, 100);
            string growth = $"Growth {growPct}%";
            if (c1.ActiveJob is not null)
            {
                int pct = Mathf.Clamp((int)Mathf.Round(c1.WorkProgress * 100f), 0, 100);
                _stateLabel.Text = $"{c1.ActiveJob} job queued ({pct}%)\n{growth}";
            }
            else
            {
                _stateLabel.Text = $"Growing\n{growth}";
            }
        }
        else
        {
            _nameLabel.Text = $"Crops ({ids.Length})";
            _tileLabel.Text = first is CropState f
                ? $"First: ({f.Tile.X}, {f.Tile.Y})"
                : "";
            _stateLabel.Text = $"{withJob} queued · {growing} growing";
        }
        // Harvest = mature only; Cut = immature only. Mixed selections
        // show both buttons so the player picks which job to post.
        _harvestBtn.Visible = growingMature > 0;
        _cutBtn.Visible = growingImmature > 0;
        _harvestBtn.Disabled = growingMature == 0;
        _cutBtn.Disabled = growingImmature == 0;
        _cancelBtn.Disabled = withJob == 0;
    }

    private void OnHarvestPressed()
    {
        if (Host is null) return;
        var ids = Host.SelectedCropIds;
        if (ids.Length == 0) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        var idSet = new HashSet<int>(ids);
        foreach (var c in snap.Crops)
        {
            if (!idSet.Contains(c.EntityId)) continue;
            if (c.ActiveJob is not null) continue;
            if (c.GrowthStage < HarvestMinGrowth) continue;
            Host.QueueCommand(new HarvestInRectCommand(c.Tile, c.Tile));
        }
    }

    private void OnCutPressed()
    {
        if (Host is null) return;
        var ids = Host.SelectedCropIds;
        if (ids.Length == 0) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        var idSet = new HashSet<int>(ids);
        foreach (var c in snap.Crops)
        {
            if (!idSet.Contains(c.EntityId)) continue;
            if (c.ActiveJob is not null) continue;
            if (c.GrowthStage >= HarvestMinGrowth) continue;
            Host.QueueCommand(new CutPlantsInRectCommand(c.Tile, c.Tile));
        }
    }

    private void OnCancelPressed()
    {
        if (Host is null) return;
        var ids = Host.SelectedCropIds;
        if (ids.Length == 0) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        var idSet = new HashSet<int>(ids);
        foreach (var c in snap.Crops)
        {
            if (!idSet.Contains(c.EntityId)) continue;
            if (c.ActiveJob is null) continue;
            Host.QueueCommand(new CancelJobsInRectCommand(c.Tile, c.Tile));
        }
    }
}
