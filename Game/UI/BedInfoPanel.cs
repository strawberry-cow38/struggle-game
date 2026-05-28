using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected bed. Shows origin tile + orientation
// and a Deconstruct button (queues a BedDeconstruct job). Multi-select
// applies decon across every selected bed.
public partial class BedInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 280;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _orientLabel = null!;
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
        _nameLabel = new Label { Text = "Bed", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedBedTiles = Array.Empty<TilePos>();
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);
        _orientLabel = new Label { Text = "" };
        vbox.AddChild(_orientLabel);

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
        var tiles = Host.SelectedBedTiles;
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
        var live = new List<BedState>(tiles.Length);
        var liveTiles = new List<TilePos>(tiles.Length);
        foreach (var t in tiles)
        {
            foreach (var b in snap.Beds)
            {
                if (b.Origin == t) { live.Add(b); liveTiles.Add(t); break; }
            }
        }
        if (live.Count == 0)
        {
            Host!.SelectedBedTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            Host!.SelectedBedTiles = liveTiles.ToArray();
        }
        if (live.Count == 1)
        {
            _nameLabel.Text = "Bed";
            _tileLabel.Text = $"Tile: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _orientLabel.Text = $"Facing: {live[0].Orientation}";
        }
        else
        {
            _nameLabel.Text = $"Beds ({live.Count})";
            _tileLabel.Text = $"First: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _orientLabel.Text = "";
        }
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedBedTiles)
            Host.QueueCommand(new PostBedDeconCommand(t));
    }
}
