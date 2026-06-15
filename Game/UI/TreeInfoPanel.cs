using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for the selected tree(s). Mirrors CropInfoPanel.
// Single selection shows tile + chop progress. Multi-selection shows
// aggregate counts. Chop / Cancel buttons fire one 1x1 rect command
// per selected tree so the existing rect plumbing handles every tile.
//
// Chrome / lifecycle / positioning / change-detect come from
// EntityInfoPanel; this only supplies the tree body, render + actions.
public partial class TreeInfoPanel : EntityInfoPanel
{
    // Mirror of SimRuntime.ChopMinGrowthStage. Trees below this can't be
    // chopped (TryPostChopJob refuses) so the action button swaps to
    // "Cut" and fires CutPlantsInRectCommand instead.
    private const float ChopMinGrowth = 0.5f;

    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private HpBar _hp = null!;
    private Button _chopBtn = null!;
    private Button _cutBtn = null!;
    private Button _cancelBtn = null!;

    protected override int[] SelectedIds
    {
        get => Host!.SelectedTreeIds;
        set => Host!.SelectedTreeIds = value;
    }

    protected override string Title => "Tree";

    protected override void BuildBody(VBoxContainer vbox)
    {
        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);
        _hp = new HpBar();
        vbox.AddChild(_hp);

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
        vbox.AddChild(btnRow);
    }

    protected override void Render(SimSnapshot snap, int[] ids)
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
            SelectedIds = Array.Empty<int>();
            return;
        }

        float hpGrowth = first is TreeState ft ? ft.GrowthStage : 1f;
        _hp.Set(ThingHp.Tree(hpGrowth), ThingHp.Tree(hpGrowth));
        if (ids.Length == 1 && first is TreeState t1)
        {
            NameLabel.Text = "Tree";
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
            NameLabel.Text = $"Trees ({ids.Length})";
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
