using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected door. Shows orientation + open
// amount, exposes Forbid + Locked toggles and a Deconstruct button.
// Forbidden = pathing treats it as a wall. Locked is a stub for the
// not-yet-shipped enemies pass (locked doors won't open for enemies).
public partial class DoorInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 280;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private CheckBox _forbidChk = null!;
    private CheckBox _lockedChk = null!;
    private Button _priorityBtn = null!;
    private DoorPriority _shownPriority = DoorPriority.Medium;
    private Button _deconBtn = null!;

    private TilePos[] _shownTiles = Array.Empty<TilePos>();
    private long _lastSnapshotTick = -1;
    private bool _suppressToggle;

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 220),
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
        _nameLabel = new Label { Text = "Door", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedDoorTiles = Array.Empty<TilePos>();
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);

        _forbidChk = new CheckBox { Text = "Forbidden (acts as wall)" };
        _forbidChk.Toggled += OnForbidToggled;
        vbox.AddChild(_forbidChk);

        _lockedChk = new CheckBox { Text = "Locked (blocks enemies — stub)" };
        _lockedChk.Toggled += OnLockedToggled;
        vbox.AddChild(_lockedChk);

        _priorityBtn = new Button { Text = "Priority: Medium", CustomMinimumSize = new Vector2(0, 28) };
        _priorityBtn.Pressed += OnPriorityCycled;
        vbox.AddChild(_priorityBtn);

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
        var tiles = Host.SelectedDoorTiles;
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
        // Build a lookup of live doors from the snapshot, then prune
        // any selected tile whose door vanished (decon'd / scrubbed).
        var live = new List<DoorRenderState>(tiles.Length);
        var liveTiles = new List<TilePos>(tiles.Length);
        foreach (var t in tiles)
        {
            foreach (var d in snap.Doors)
            {
                if (d.Tile == t) { live.Add(d); liveTiles.Add(t); break; }
            }
        }
        if (live.Count == 0)
        {
            Host!.SelectedDoorTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            Host!.SelectedDoorTiles = liveTiles.ToArray();
        }

        var d0 = live[0];
        int forbidCount = 0, lockedCount = 0;
        foreach (var d in live)
        {
            if (d.Forbidden) forbidCount++;
            if (d.Locked) lockedCount++;
        }

        if (live.Count == 1)
        {
            _nameLabel.Text = $"Door ({d0.Orientation})";
            _tileLabel.Text = $"Tile: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _stateLabel.Text = $"Open: {d0.OpenAmount * 100f:0}%";
        }
        else
        {
            _nameLabel.Text = $"Doors ({live.Count})";
            _tileLabel.Text = $"First: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _stateLabel.Text = $"Forbid {forbidCount}/{live.Count}  Lock {lockedCount}/{live.Count}";
        }

        _suppressToggle = true;
        _forbidChk.ButtonPressed = forbidCount == live.Count;
        _lockedChk.ButtonPressed = lockedCount == live.Count;
        _suppressToggle = false;
        _shownPriority = d0.Priority;
        _priorityBtn.Text = $"Priority: {PriorityLabel(d0.Priority)}";
    }

    private static string PriorityLabel(DoorPriority p) => p switch
    {
        DoorPriority.ExitOnly => "Exit Only",
        DoorPriority.Low      => "Low",
        DoorPriority.Medium   => "Medium",
        DoorPriority.High     => "High",
        _                     => p.ToString(),
    };

    private static DoorPriority NextPriority(DoorPriority p) => p switch
    {
        DoorPriority.ExitOnly => DoorPriority.Low,
        DoorPriority.Low      => DoorPriority.Medium,
        DoorPriority.Medium   => DoorPriority.High,
        DoorPriority.High     => DoorPriority.ExitOnly,
        _                     => DoorPriority.Medium,
    };

    private void OnPriorityCycled()
    {
        if (Host is null) return;
        var next = NextPriority(_shownPriority);
        foreach (var t in Host.SelectedDoorTiles)
            Host.QueueCommand(new SetDoorPriorityCommand(t, next));
    }

    private void OnForbidToggled(bool pressed)
    {
        if (_suppressToggle || Host is null) return;
        foreach (var t in Host.SelectedDoorTiles)
            Host.QueueCommand(new SetDoorForbiddenCommand(t, pressed));
    }

    private void OnLockedToggled(bool pressed)
    {
        if (_suppressToggle || Host is null) return;
        foreach (var t in Host.SelectedDoorTiles)
            Host.QueueCommand(new SetDoorLockedCommand(t, pressed));
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedDoorTiles)
            Host.QueueCommand(new PostDoorDeconCommand(t));
    }
}
