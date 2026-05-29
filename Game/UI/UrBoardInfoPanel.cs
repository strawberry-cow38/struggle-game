using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected Ur board. Shows tile, current player /
// spectator counts, and a Deconstruct button. Multi-select applies decon
// across every selected board.
public partial class UrBoardInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 280;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _playersLabel = null!;
    private Label _spectatorsLabel = null!;
    private Button _deconBtn = null!;

    private TilePos[] _shownTiles = Array.Empty<TilePos>();
    private long _lastSnapshotTick = -1;

    public override void _Ready()
    {
        Layer = 95;
        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 140),
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
        _nameLabel = new Label { Text = "Ur Board", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedUrBoardTiles = Array.Empty<TilePos>();
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);
        _playersLabel = new Label { Text = "" };
        vbox.AddChild(_playersLabel);
        _spectatorsLabel = new Label { Text = "" };
        vbox.AddChild(_spectatorsLabel);

        _deconBtn = new Button { Text = "Deconstruct", CustomMinimumSize = new Vector2(0, 28) };
        _deconBtn.Pressed += OnDeconPressed;
        vbox.AddChild(_deconBtn);

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
        var tiles = Host.SelectedUrBoardTiles;
        var snap = Host.LatestSnapshot;
        if (tiles.Length == 0 || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownTiles = Array.Empty<TilePos>(); }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        if (!TilesEqual(tiles, _shownTiles) || snap.Tick != _lastSnapshotTick)
        {
            Render(snap, tiles);
            _shownTiles = tiles;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private static bool TilesEqual(TilePos[] a, TilePos[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, _root.Size.Y);
    }

    private void Render(SimSnapshot snap, TilePos[] tiles)
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
            Host!.SelectedUrBoardTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            Host!.SelectedUrBoardTiles = liveTiles.ToArray();
        }
        if (live.Count == 1)
        {
            _nameLabel.Text = "Ur Board";
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
            _nameLabel.Text = $"Ur Boards ({live.Count})";
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
