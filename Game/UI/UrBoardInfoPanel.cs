using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected Ur board. Shows tile, current player /
// spectator counts, and a Deconstruct button. Multi-select applies decon
// across every selected board. See TileInfoPanel.
public partial class UrBoardInfoPanel : TileInfoPanel
{
    private Label _tileLabel = null!;
    private Label _playersLabel = null!;
    private Label _spectatorsLabel = null!;
    private HpBar _hp = null!;
    private Button _deconBtn = null!;

    protected override TilePos[] SelectedTiles
    {
        get => Host!.SelectedUrBoardTiles;
        set => Host!.SelectedUrBoardTiles = value;
    }
    protected override string Title => "Ur Board";
    protected override int MinHeight => 140;

    protected override void BuildBody(VBoxContainer vbox)
    {
        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);
        _playersLabel = new Label { Text = "" };
        vbox.AddChild(_playersLabel);
        _spectatorsLabel = new Label { Text = "" };
        vbox.AddChild(_spectatorsLabel);
        _hp = new HpBar();
        vbox.AddChild(_hp);

        _deconBtn = new Button { Text = "Deconstruct", CustomMinimumSize = new Vector2(0, 28) };
        _deconBtn.Pressed += OnDeconPressed;
        vbox.AddChild(_deconBtn);
    }

    protected override void Render(SimSnapshot snap, TilePos[] tiles)
    {
        var live = new List<UrBoardState>(tiles.Length);
        var liveTiles = new List<TilePos>(tiles.Length);
        foreach (var t in tiles)
        {
            foreach (var ub in snap.UrBoards)
            {
                if (ub.Tile == t) { live.Add(ub); liveTiles.Add(t); break; }
            }
        }
        if (live.Count == 0)
        {
            SelectedTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            SelectedTiles = liveTiles.ToArray();
        }
        _hp.Set(ThingHp.UrBoard, ThingHp.UrBoard);
        if (live.Count == 1)
        {
            NameLabel.Text = "Ur Board";
            _tileLabel.Text = $"Tile: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _playersLabel.Text = $"Players: {live[0].PlayerCount} / 2";
            _spectatorsLabel.Text = $"Spectators: {live[0].SpectatorCount} / 8";
        }
        else
        {
            int totalPlayers = 0, totalSpectators = 0;
            foreach (var ub in live)
            {
                totalPlayers += ub.PlayerCount;
                totalSpectators += ub.SpectatorCount;
            }
            NameLabel.Text = $"Ur Boards ({live.Count})";
            _tileLabel.Text = $"First: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _playersLabel.Text = $"Players: {totalPlayers}";
            _spectatorsLabel.Text = $"Spectators: {totalSpectators}";
        }
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedUrBoardTiles)
            Host.QueueCommand(new PostUrBoardDeconCommand(t));
    }
}
