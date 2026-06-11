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

    private const int PanelWidth = 560;
    private const int MarginLeft = 12;
    private const int MarginBottom = 16;
    private const int PanelPad = 10;

    private Panel _root = null!;
    private VBoxContainer _vbox = null!;
    private ScanlineStyleBox _panelBox = null!;
    private double _glowT;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private HpBar _hp = null!;
    private Button _chopBtn = null!;
    private Button _cutBtn = null!;
    private Button _cancelBtn = null!;

    private int _shownCount = -1;
    private int _shownFirstId = -1;
    private long _lastSnapshotTick = -1;

    // Mirror of SimRuntime.ChopMinGrowthStage. Trees below this can't be
    // chopped (TryPostChopJob refuses) so the action button swaps to
    // "Cut" and fires CutPlantsInRectCommand instead.
    private const float ChopMinGrowth = 0.5f;

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
        _nameLabel = new Label { Text = "Tree", CustomMinimumSize = new Vector2(0, 28) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 22);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = UiTheme.CloseButton();
        closeBtn.Pressed += () => Host!.SelectedTreeIds = Array.Empty<int>();
        headerRow.AddChild(closeBtn);
        _vbox.AddChild(headerRow);

        _vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        _vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        _vbox.AddChild(_stateLabel);
        _hp = new HpBar();
        _vbox.AddChild(_hp);

        var btnRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btnRow.AddThemeConstantOverride("separation", 6);
        _chopBtn = new Button { Text = "Chop", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _chopBtn.Pressed += OnChopPressed;
        btnRow.AddChild(_chopBtn);
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
        GetTree().Root.SizeChanged -= Reposition;
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
        float h = Math.Max(350, _vbox.GetCombinedMinimumSize().Y + PanelPad * 2);
        _root.Size = new Vector2(PanelWidth, h);
        _root.Position = new Vector2(MarginLeft, vp.Y - h - MarginBottom);
    }

    private void Render(SimSnapshot snap, int[] ids)
    {
        var idSet = new HashSet<int>(ids);
        int withJob = 0, standing = 0, missing = 0;
        int standingMature = 0, standingImmature = 0;
        TreeState? first = null;
        foreach (var t in snap.Trees)
        {
            if (!idSet.Contains(t.EntityId)) continue;
            if (first is null) first = t;
            if (t.HasJob) withJob++;
            else
            {
                standing++;
                if (t.GrowthStage >= ChopMinGrowth) standingMature++;
                else standingImmature++;
            }
            idSet.Remove(t.EntityId);
        }
        // Anything left in idSet was felled out from under the selection.
        missing = idSet.Count;
        if (missing > 0 && withJob + standing == 0)
        {
            Host!.SelectedTreeIds = Array.Empty<int>();
            return;
        }

        float hpGrowth = first is TreeState ft ? ft.GrowthStage : 1f;
        _hp.Set(ThingHp.Tree(hpGrowth), ThingHp.Tree(hpGrowth));
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
        // Chop is enabled when any mature tree is in the selection; Cut
        // is enabled when any immature tree is. Mixed selections show
        // both buttons live so the player can pick which job to post.
        // A button with nothing to act on stays hidden so the row
        // doesn't carry phantom controls.
        _chopBtn.Visible = standingMature > 0;
        _cutBtn.Visible = standingImmature > 0;
        _chopBtn.Disabled = standingMature == 0;
        _cutBtn.Disabled = standingImmature == 0;
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
            if (t.GrowthStage < ChopMinGrowth) continue;
            Host.QueueCommand(new ChopTreesInRectCommand(t.Tile, t.Tile));
        }
    }

    private void OnCutPressed()
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
            if (t.GrowthStage >= ChopMinGrowth) continue;
            Host.QueueCommand(new CutPlantsInRectCommand(t.Tile, t.Tile));
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
