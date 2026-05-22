using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

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
    private Button _deconBtn = null!;

    private TilePos? _shownTile;
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
        closeBtn.Pressed += () => Host!.SelectedDoorTile = null;
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
        var tile = Host.SelectedDoorTile;
        var snap = Host.LatestSnapshot;
        if (tile is null || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownTile = null; }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        if (tile != _shownTile || snap.Tick != _lastSnapshotTick)
        {
            Render(snap, tile.Value);
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

    private void Render(SimSnapshot snap, TilePos tile)
    {
        DoorRenderState? door = null;
        foreach (var d in snap.Doors)
        {
            if (d.Tile == tile) { door = d; break; }
        }
        if (door is null)
        {
            // Door vanished (decon'd / scrubbed); drop selection.
            Host!.SelectedDoorTile = null;
            return;
        }
        var d0 = door.Value;
        _nameLabel.Text = $"Door ({d0.Orientation})";
        _tileLabel.Text = $"Tile: ({tile.X}, {tile.Y})";
        _stateLabel.Text = $"Open: {d0.OpenAmount * 100f:0}%";

        _suppressToggle = true;
        _forbidChk.ButtonPressed = d0.Forbidden;
        _lockedChk.ButtonPressed = d0.Locked;
        _suppressToggle = false;
    }

    private void OnForbidToggled(bool pressed)
    {
        if (_suppressToggle || Host is null) return;
        var tile = Host.SelectedDoorTile;
        if (tile is null) return;
        Host.QueueCommand(new SetDoorForbiddenCommand(tile.Value, pressed));
    }

    private void OnLockedToggled(bool pressed)
    {
        if (_suppressToggle || Host is null) return;
        var tile = Host.SelectedDoorTile;
        if (tile is null) return;
        Host.QueueCommand(new SetDoorLockedCommand(tile.Value, pressed));
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        var tile = Host.SelectedDoorTile;
        if (tile is null) return;
        Host.QueueCommand(new PostDoorDeconCommand(tile.Value));
    }
}
