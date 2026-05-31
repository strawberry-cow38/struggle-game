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
    private Button _deconBtn = null!;

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

        _deconBtn = new Button { Text = "Deconstruct", CustomMinimumSize = new Vector2(0, 28) };
        _deconBtn.Pressed += OnDeconPressed;
        vbox.AddChild(_deconBtn);
    }

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
        _deconBtn.Disabled = playerWalls == 0;
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedWallTiles)
        {
            if (!Host.IsPlayerWall(t)) continue;
            Host.QueueCommand(new PostWallDeconCommand(t));
        }
    }
}
