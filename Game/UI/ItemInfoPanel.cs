using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for the currently selected dropped item stack.
// Shows display name, stack count, tile, forbidden state, and a
// toggle button. F key on Bootstrap forwards to the same command.
public partial class ItemInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 280;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _countLabel = null!;
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private Button _forbidBtn = null!;

    private int _shownEntityId = -1;
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
        _nameLabel = new Label { Text = "Item", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedWoodId = null;
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _countLabel = new Label { Text = "" };
        vbox.AddChild(_countLabel);

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);

        _forbidBtn = new Button { Text = "Forbid", CustomMinimumSize = new Vector2(0, 28) };
        _forbidBtn.Pressed += OnForbidPressed;
        vbox.AddChild(_forbidBtn);

        var hint = new Label { Text = "Hotkey: F", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        hint.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(hint);

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
        int? sel = Host.SelectedWoodId;
        var snap = Host.LatestSnapshot;
        if (sel is null || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownEntityId = -1; }
            return;
        }
        WoodState? found = null;
        foreach (var w in snap.Wood)
        {
            if (w.EntityId == sel.Value) { found = w; break; }
        }
        if (found is null)
        {
            // Stack consumed (picked up / merged) — clear selection.
            Host.SelectedWoodId = null;
            _root.Visible = false;
            _shownEntityId = -1;
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        if (found.Value.EntityId != _shownEntityId || snap.Tick != _lastSnapshotTick)
        {
            Render(found.Value);
            _shownEntityId = found.Value.EntityId;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, _root.Size.Y);
    }

    private void Render(WoodState w)
    {
        string name = ItemCatalog.ItemsByPath.TryGetValue(w.ItemPath, out var def)
            ? def.DisplayName : w.ItemPath;
        _nameLabel.Text = name;
        _countLabel.Text = $"Count: {w.Count}";
        _tileLabel.Text = $"Tile: ({w.Tile.X}, {w.Tile.Y})";
        _stateLabel.Text = w.Forbidden ? "Forbidden" : "Haulable";
        _forbidBtn.Text = w.Forbidden ? "Unforbid" : "Forbid";
    }

    private void OnForbidPressed()
    {
        if (Host is null) return;
        int? sel = Host.SelectedWoodId;
        if (sel is null) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        bool currentlyForbidden = false;
        foreach (var w in snap.Wood)
        {
            if (w.EntityId == sel.Value) { currentlyForbidden = w.Forbidden; break; }
        }
        Host.QueueCommand(new ForbidStackCommand(sel.Value, !currentlyForbidden));
    }
}
