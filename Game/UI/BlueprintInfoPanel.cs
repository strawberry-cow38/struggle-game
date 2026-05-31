using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected blueprint or queued job tile. Covers
// wall / floor / door / lamp / bed blueprints + decon marks. Surfaces
// Forbid (pawns stop claiming it but the order stays queued) + Cancel.
// See TileInfoPanel.
public partial class BlueprintInfoPanel : TileInfoPanel
{
    private Label _tileLabel = null!;
    private Label _progressLabel = null!;
    private Label _resourcesHeader = null!;
    private VBoxContainer _resourcesBox = null!;
    private CheckBox _forbidChk = null!;
    private Button _cancelBtn = null!;
    private bool _suppressToggle;

    protected override TilePos[] SelectedTiles
    {
        get => Host!.SelectedBlueprintTiles;
        set => Host!.SelectedBlueprintTiles = value;
    }
    protected override string Title => "Blueprint";
    protected override int MinHeight => 260;

    protected override void BuildBody(VBoxContainer vbox)
    {
        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _progressLabel = new Label { Text = "" };
        vbox.AddChild(_progressLabel);

        _resourcesHeader = new Label { Text = "Resources" };
        _resourcesHeader.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(_resourcesHeader);

        _resourcesBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _resourcesBox.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(_resourcesBox);

        _forbidChk = new CheckBox { Text = "Forbidden (no one builds)" };
        _forbidChk.Toggled += OnForbidToggled;
        vbox.AddChild(_forbidChk);

        _cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(0, 28) };
        _cancelBtn.Pressed += OnCancelPressed;
        vbox.AddChild(_cancelBtn);
    }

    protected override void Render(SimSnapshot snap, TilePos[] tiles)
    {
        var liveTiles = new List<TilePos>(tiles.Length);
        var liveKinds = new List<string>(tiles.Length);
        var liveCosts = new List<ResourceCostState[]>(tiles.Length);
        float progSum = 0f;
        int forbidCount = 0;
        foreach (var t in tiles)
        {
            if (TryFind(snap, t, out var kind, out var progress, out var forbidden, out var costs))
            {
                liveTiles.Add(t);
                liveKinds.Add(kind);
                liveCosts.Add(costs);
                progSum += progress;
                if (forbidden) forbidCount++;
            }
        }
        if (liveTiles.Count == 0)
        {
            SelectedTiles = Array.Empty<TilePos>();
            return;
        }
        if (liveTiles.Count != tiles.Length)
        {
            SelectedTiles = liveTiles.ToArray();
        }

        if (liveTiles.Count == 1)
        {
            NameLabel.Text = liveKinds[0];
            _tileLabel.Text = $"Tile: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _progressLabel.Text = $"Progress: {progSum * 100f:0}%";
            RenderResourceLines(liveCosts[0]);
        }
        else
        {
            // Mixed-kind selections collapse to "Jobs (N)".
            string headKind = liveKinds[0];
            bool uniform = true;
            for (int i = 1; i < liveKinds.Count; i++) if (liveKinds[i] != headKind) { uniform = false; break; }
            NameLabel.Text = uniform ? $"{headKind}s ({liveTiles.Count})" : $"Jobs ({liveTiles.Count})";
            _tileLabel.Text = $"First: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _progressLabel.Text = $"Avg progress: {(progSum / liveTiles.Count) * 100f:0}%  Forbid {forbidCount}/{liveTiles.Count}";
            RenderResourceLines(SumCosts(liveCosts));
        }

        _suppressToggle = true;
        _forbidChk.ButtonPressed = forbidCount == liveTiles.Count;
        _suppressToggle = false;
    }

    private void RenderResourceLines(ResourceCostState[] costs)
    {
        foreach (var child in _resourcesBox.GetChildren()) child.QueueFree();
        if (costs.Length == 0)
        {
            _resourcesHeader.Visible = false;
            _resourcesBox.Visible = false;
            return;
        }
        _resourcesHeader.Visible = true;
        _resourcesBox.Visible = true;
        foreach (var c in costs)
        {
            string name = ItemCatalog.ItemsByPath.TryGetValue(c.ItemPath, out var def) ? def.DisplayName : c.ItemPath;
            int dep = Math.Min(c.Deposited, c.Needed);
            var row = new Label { Text = $"  {name}: {dep}/{c.Needed}" };
            if (dep >= c.Needed) row.AddThemeColorOverride("font_color", new Color(0.55f, 0.85f, 0.55f));
            else row.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.45f));
            _resourcesBox.AddChild(row);
        }
    }

    private static ResourceCostState[] SumCosts(List<ResourceCostState[]> all)
    {
        var totals = new Dictionary<string, (int Need, int Dep)>();
        foreach (var arr in all)
        {
            foreach (var c in arr)
            {
                totals.TryGetValue(c.ItemPath, out var cur);
                totals[c.ItemPath] = (cur.Need + c.Needed, cur.Dep + c.Deposited);
            }
        }
        if (totals.Count == 0) return Array.Empty<ResourceCostState>();
        var outArr = new ResourceCostState[totals.Count];
        int i = 0;
        foreach (var (path, sums) in totals) outArr[i++] = new ResourceCostState(path, sums.Need, sums.Dep);
        return outArr;
    }

    private static bool TryFind(SimSnapshot snap, TilePos tile, out string kind, out float progress, out bool forbidden, out ResourceCostState[] costs)
    {
        foreach (var b in snap.Blueprints)
        {
            if (b.Tile == tile) { kind = "Wall Blueprint"; progress = b.Progress; forbidden = b.Forbidden; costs = b.Costs; return true; }
        }
        foreach (var b in snap.FloorBlueprints)
        {
            if (b.Tile == tile) { kind = "Floor Blueprint"; progress = b.Progress; forbidden = b.Forbidden; costs = b.Costs; return true; }
        }
        foreach (var b in snap.DoorBlueprints)
        {
            if (b.Tile == tile) { kind = "Door Blueprint"; progress = b.Progress; forbidden = b.Forbidden; costs = b.Costs; return true; }
        }
        foreach (var b in snap.LampBlueprints)
        {
            if (b.Tile == tile) { kind = "Lamp Blueprint"; progress = b.Progress; forbidden = b.Forbidden; costs = b.Costs; return true; }
        }
        foreach (var b in snap.BedBlueprints)
        {
            if (b.Origin == tile) { kind = "Bed Blueprint"; progress = b.Progress; forbidden = b.Forbidden; costs = b.Costs; return true; }
        }
        foreach (var d in snap.Decons)
        {
            if (d.Tile == tile) { kind = "Deconstruct"; progress = d.Progress; forbidden = d.Forbidden; costs = Array.Empty<ResourceCostState>(); return true; }
        }
        kind = ""; progress = 0f; forbidden = false; costs = Array.Empty<ResourceCostState>();
        return false;
    }

    private void OnForbidToggled(bool pressed)
    {
        if (_suppressToggle || Host is null) return;
        foreach (var t in Host.SelectedBlueprintTiles)
            Host.QueueCommand(new SetJobForbiddenCommand(t, pressed));
    }

    private void OnCancelPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedBlueprintTiles)
            Host.QueueCommand(new CancelJobAtTileCommand(t));
        Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
    }
}
