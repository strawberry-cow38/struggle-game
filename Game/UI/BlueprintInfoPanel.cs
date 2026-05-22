using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected blueprint or queued job tile. Covers
// wall / floor / door blueprints + decon marks. Surfaces Forbid (pawns
// stop claiming it but the order stays in the queue) + Cancel.
public partial class BlueprintInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 280;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _tileLabel = null!;
    private Label _progressLabel = null!;
    private CheckBox _forbidChk = null!;
    private Button _cancelBtn = null!;

    private TilePos? _shownTile;
    private long _lastSnapshotTick = -1;
    private bool _suppressToggle;

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 200),
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
        _nameLabel = new Label { Text = "Blueprint", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedBlueprintTile = null;
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _progressLabel = new Label { Text = "" };
        vbox.AddChild(_progressLabel);

        _forbidChk = new CheckBox { Text = "Forbidden (no one builds)" };
        _forbidChk.Toggled += OnForbidToggled;
        vbox.AddChild(_forbidChk);

        _cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(0, 28) };
        _cancelBtn.Pressed += OnCancelPressed;
        vbox.AddChild(_cancelBtn);

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
        var tile = Host.SelectedBlueprintTile;
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
        if (TryFind(snap, tile, out string kind, out float progress, out bool forbidden))
        {
            _nameLabel.Text = kind;
            _tileLabel.Text = $"Tile: ({tile.X}, {tile.Y})";
            _progressLabel.Text = $"Progress: {progress * 100f:0}%";
            _suppressToggle = true;
            _forbidChk.ButtonPressed = forbidden;
            _suppressToggle = false;
            return;
        }
        // Blueprint vanished (built / cancelled). Drop selection.
        Host!.SelectedBlueprintTile = null;
    }

    private static bool TryFind(SimSnapshot snap, TilePos tile, out string kind, out float progress, out bool forbidden)
    {
        foreach (var b in snap.Blueprints)
        {
            if (b.Tile == tile) { kind = "Wall Blueprint"; progress = b.Progress; forbidden = b.Forbidden; return true; }
        }
        foreach (var b in snap.FloorBlueprints)
        {
            if (b.Tile == tile) { kind = "Floor Blueprint"; progress = b.Progress; forbidden = b.Forbidden; return true; }
        }
        foreach (var b in snap.DoorBlueprints)
        {
            if (b.Tile == tile) { kind = "Door Blueprint"; progress = b.Progress; forbidden = b.Forbidden; return true; }
        }
        foreach (var d in snap.Decons)
        {
            if (d.Tile == tile) { kind = "Deconstruct"; progress = d.Progress; forbidden = d.Forbidden; return true; }
        }
        kind = ""; progress = 0f; forbidden = false;
        return false;
    }

    private void OnForbidToggled(bool pressed)
    {
        if (_suppressToggle || Host is null) return;
        var tile = Host.SelectedBlueprintTile;
        if (tile is null) return;
        Host.QueueCommand(new SetJobForbiddenCommand(tile.Value, pressed));
    }

    private void OnCancelPressed()
    {
        if (Host is null) return;
        var tile = Host.SelectedBlueprintTile;
        if (tile is null) return;
        Host.QueueCommand(new CancelJobAtTileCommand(tile.Value));
        Host.SelectedBlueprintTile = null;
    }
}
