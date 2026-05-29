using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected stove. Shows origin tile, orientation,
// cook progress, and a Deconstruct button + Bills button (opens BillsPanel).
// Multi-select applies decon across every selected stove.
public partial class StoveInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }
    public BillsPanel? Bills { get; set; }

    private const int PanelWidth = 280;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _orientLabel = null!;
    private Label _progressLabel = null!;
    private Label _billsLabel = null!;
    private Button _billsBtn = null!;
    private Button _deconBtn = null!;

    private TilePos[] _shownTiles = Array.Empty<TilePos>();
    private long _lastSnapshotTick = -1;

    public override void _Ready()
    {
        Layer = 95;
        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 180),
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
        _nameLabel = new Label { Text = "Stove", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedStoveTiles = Array.Empty<TilePos>();
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);
        _orientLabel = new Label { Text = "" };
        vbox.AddChild(_orientLabel);
        _progressLabel = new Label { Text = "" };
        vbox.AddChild(_progressLabel);
        _billsLabel = new Label { Text = "" };
        vbox.AddChild(_billsLabel);

        _billsBtn = new Button { Text = "Bills...", CustomMinimumSize = new Vector2(0, 28) };
        _billsBtn.Pressed += OnBillsPressed;
        vbox.AddChild(_billsBtn);

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
        var tiles = Host.SelectedStoveTiles;
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
        var live = new List<StoveState>(tiles.Length);
        var liveTiles = new List<TilePos>(tiles.Length);
        foreach (var t in tiles)
        {
            foreach (var s in snap.Stoves)
            {
                if (s.Origin == t) { live.Add(s); liveTiles.Add(t); break; }
            }
        }
        if (live.Count == 0)
        {
            Host!.SelectedStoveTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            Host!.SelectedStoveTiles = liveTiles.ToArray();
        }
        if (live.Count == 1)
        {
            var s = live[0];
            _nameLabel.Text = "Stove";
            _tileLabel.Text = $"Tile: ({s.Origin.X}, {s.Origin.Y})";
            _orientLabel.Text = $"Facing: {s.Orientation}";
            if (s.CurrentBillIndex >= 0)
            {
                _progressLabel.Text = $"Cooking: {Mathf.RoundToInt(s.CookProgress * 100f)}%";
            }
            else
            {
                _progressLabel.Text = "Idle";
            }
            _billsLabel.Text = $"Bills: {s.Bills.Length}";
            _billsBtn.Disabled = false;
        }
        else
        {
            _nameLabel.Text = $"Stoves ({live.Count})";
            _tileLabel.Text = $"First: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _orientLabel.Text = "";
            _progressLabel.Text = "";
            _billsLabel.Text = "";
            // Bills UI only makes sense for a single stove.
            _billsBtn.Disabled = true;
        }
    }

    private void OnBillsPressed()
    {
        if (Host is null || Bills is null) return;
        var tiles = Host.SelectedStoveTiles;
        if (tiles.Length != 1) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        foreach (var s in snap.Stoves)
        {
            if (s.Origin == tiles[0]) { Bills.Open(s.EntityId); return; }
        }
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedStoveTiles)
            Host.QueueCommand(new DeconstructStoveCommand(t));
    }
}
