using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected wall tile. Shows wall type + whether the
// wall is player-built. Deconstruct is enabled only for player walls
// (procgen + border walls aren't deconstructable). See TileInfoPanel.
public partial class WallInfoPanel : TileInfoPanel
{
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private Button _uninstallBtn = null!;
    private Button _reinstallBtn = null!;
    private Button _deconBtn = null!;
    private bool _deconQueued; // selection already has a decon job → button cancels

    protected override TilePos[] SelectedTiles
    {
        get => Host!.SelectedWallTiles;
        set => Host!.SelectedWallTiles = value;
    }
    protected override string Title => "Wall";

    protected override void BuildBody(VBoxContainer vbox)
    {
        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);

        // Spacer pushes the action row to the bottom of the panel.
        vbox.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

        // Bottom action row: Uninstall · Reinstall at · Deconstruct (equal width).
        var btnRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btnRow.AddThemeConstantOverride("separation", 6);
        _uninstallBtn = UiTheme.ActionButton("Uninstall");
        _uninstallBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _uninstallBtn.Pressed += OnUninstallPressed;
        btnRow.AddChild(_uninstallBtn);
        _reinstallBtn = UiTheme.ActionButton("Reinstall at");
        _reinstallBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _reinstallBtn.Pressed += OnReinstallPressed;
        btnRow.AddChild(_reinstallBtn);
        _deconBtn = UiTheme.ActionButton("Deconstruct");
        _deconBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _deconBtn.Pressed += OnDeconPressed;
        btnRow.AddChild(_deconBtn);
        vbox.AddChild(btnRow);
    }

    // Stubs for now — wired when minify/reinstall lands.
    private void OnUninstallPressed() { }
    private void OnReinstallPressed() { }

    protected override void Render(SimSnapshot snap, TilePos[] tiles)
    {
        // Drop any selected tile whose wall is gone; if nothing's left,
        // close the panel.
        var live = new List<TilePos>(tiles.Length);
        int playerWalls = 0;
        foreach (var t in tiles)
        {
            if (!Host!.Map.InBounds(t)) continue;
            if (Host.Map.GetWall(t) == WallType.None) continue;
            live.Add(t);
            if (Host.IsPlayerWall(t)) playerWalls++;
        }
        if (live.Count == 0)
        {
            SelectedTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            SelectedTiles = live.ToArray();
        }

        var first = live[0];
        if (live.Count == 1)
        {
            var type = Host!.Map.GetWall(first);
            NameLabel.Text = $"Wall ({type})";
            _tileLabel.Text = $"Tile: ({first.X}, {first.Y})";
            _stateLabel.Text = playerWalls == 1 ? "Player-built" : "Pre-placed";
        }
        else
        {
            NameLabel.Text = $"Walls ({live.Count})";
            _tileLabel.Text = $"First: ({first.X}, {first.Y})";
            _stateLabel.Text = playerWalls == live.Count
                ? "All player-built"
                : (playerWalls == 0 ? "All pre-placed" : $"{playerWalls}/{live.Count} player-built");
        }

        // Flip the button to Cancel when the selection already has a decon job.
        var deconTiles = new HashSet<TilePos>();
        foreach (var d in snap.Decons) deconTiles.Add(d.Tile);
        int queued = 0;
        foreach (var t in live) if (deconTiles.Contains(t)) queued++;
        _deconQueued = queued > 0;
        _deconBtn.Text = _deconQueued ? "Cancel" : "Deconstruct";
        _deconBtn.Disabled = !_deconQueued && playerWalls == 0;
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedWallTiles)
        {
            if (_deconQueued) Host.QueueCommand(new CancelJobsInRectCommand(t, t));
            else if (Host.IsPlayerWall(t)) Host.QueueCommand(new PostWallDeconCommand(t));
        }
    }
}
