using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for the selected crop(s). Mirrors TreeInfoPanel.
// Action button is "Harvest" when the selection contains any crop at
// >= HarvestMinGrowth, otherwise "Cut" (the verb that clears immature
// crops with no yield).
//
// Chrome / lifecycle / positioning / change-detect all come from
// EntityInfoPanel; this only supplies the crop body, render + actions.
public partial class CropInfoPanel : EntityInfoPanel
{
    // Mirror of SimRuntime.HarvestMinGrowthStage. Below this crops yield
    // nothing on harvest, so the action button swaps to "Cut".
    private const float HarvestMinGrowth = 0.75f;

    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private Button _harvestBtn = null!;
    private Button _cutBtn = null!;
    private Button _cancelBtn = null!;

    protected override int[] SelectedIds
    {
        get => Host!.SelectedCropIds;
        set => Host!.SelectedCropIds = value;
    }

    protected override string Title => "Crop";

    protected override void BuildBody(VBoxContainer vbox)
    {
        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);

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
        vbox.AddChild(btnRow);
    }

    protected override void Render(SimSnapshot snap, int[] ids)
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
            SelectedIds = Array.Empty<int>();
            return;
        }

        if (ids.Length == 1 && first is CropState c1)
        {
            NameLabel.Text = c1.Kind.ToString();
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
            NameLabel.Text = $"Crops ({ids.Length})";
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
