using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected wall tile. Shows wall type + whether
// the wall is player-built. Deconstruct button is enabled only for
// player walls (procgen + border walls aren't deconstructable).
public partial class WallInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 280;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private Button _deconBtn = null!;

    private TilePos[] _shownTiles = Array.Empty<TilePos>();
    private long _lastSnapshotTick = -1;

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 160),
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
        _nameLabel = new Label { Text = "Wall", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedWallTiles = Array.Empty<TilePos>();
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);

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
        var tiles = Host.SelectedWallTiles;
        var snap = Host.LatestSnapshot;
        if (tiles.Length == 0 || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownTiles = Array.Empty<TilePos>(); }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        if (!TilesEqual(tiles, _shownTiles) || snap.Tick != _lastSnapshotTick)
        {
            Render(tiles);
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

    private void Render(TilePos[] tiles)
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
            Host!.SelectedWallTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            Host!.SelectedWallTiles = live.ToArray();
        }

        var first = live[0];
        if (live.Count == 1)
        {
            var type = Host!.Map.GetWall(first);
            _nameLabel.Text = $"Wall ({type})";
            _tileLabel.Text = $"Tile: ({first.X}, {first.Y})";
            _stateLabel.Text = playerWalls == 1 ? "Player-built" : "Pre-placed";
        }
        else
        {
            _nameLabel.Text = $"Walls ({live.Count})";
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
