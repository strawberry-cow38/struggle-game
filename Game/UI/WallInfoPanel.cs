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

    private TilePos? _shownTile;
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
        closeBtn.Pressed += () => Host!.SelectedWallTile = null;
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
        var tile = Host.SelectedWallTile;
        var snap = Host.LatestSnapshot;
        if (tile is null || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownTile = null; }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        if (tile != _shownTile || snap.Tick != _lastSnapshotTick)
        {
            Render(tile.Value);
            _shownTile = tile;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, _root.Size.Y);
    }

    private void Render(TilePos tile)
    {
        if (!Host!.Map.InBounds(tile))
        {
            Host.SelectedWallTile = null;
            return;
        }
        var type = Host.Map.GetWall(tile);
        if (type == WallType.None)
        {
            // Wall was just decon'd from under the panel.
            Host.SelectedWallTile = null;
            return;
        }
        bool isPlayer = Host.IsPlayerWall(tile);
        _nameLabel.Text = $"Wall ({type})";
        _tileLabel.Text = $"Tile: ({tile.X}, {tile.Y})";
        _stateLabel.Text = isPlayer ? "Player-built" : "Pre-placed";
        _deconBtn.Disabled = !isPlayer;
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        var tile = Host.SelectedWallTile;
        if (tile is null) return;
        Host.QueueCommand(new PostWallDeconCommand(tile.Value));
    }
}
