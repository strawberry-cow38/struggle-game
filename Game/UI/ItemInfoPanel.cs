using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for the selected dropped item stack(s). Single
// selection shows display name + count + tile + forbid state.
// Multi-selection aggregates count and exposes a Forbid All button
// that toggles based on the majority state of the selection.
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

    private int _shownCount = -1;
    private int _shownFirstId = -1;
    private long _lastSnapshotTick = -1;
    private bool _selectionForbidden;

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
        closeBtn.Pressed += () => Host!.SelectedWoodIds = Array.Empty<int>();
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
        var ids = Host.SelectedWoodIds;
        var snap = Host.LatestSnapshot;
        if (ids.Length == 0 || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownCount = -1; }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        int first = ids[0];
        if (ids.Length != _shownCount || first != _shownFirstId || snap.Tick != _lastSnapshotTick)
        {
            Render(snap, ids);
            _shownCount = ids.Length;
            _shownFirstId = first;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, _root.Size.Y);
    }

    private void Render(SimSnapshot snap, int[] ids)
    {
        var idSet = new HashSet<int>(ids);
        WoodState? first = null;
        int totalCount = 0;
        int forbidden = 0;
        int haulable = 0;
        foreach (var w in snap.Wood)
        {
            if (!idSet.Contains(w.EntityId)) continue;
            if (first is null) first = w;
            totalCount += w.Count;
            if (w.Forbidden) forbidden++; else haulable++;
            idSet.Remove(w.EntityId);
        }
        int missing = idSet.Count;
        int found = forbidden + haulable;
        if (found == 0)
        {
            // All stacks vanished (picked up / merged).
            Host!.SelectedWoodIds = Array.Empty<int>();
            return;
        }

        if (ids.Length == 1 && first is WoodState w1)
        {
            string name = ItemCatalog.ItemsByPath.TryGetValue(w1.ItemPath, out var def)
                ? def.DisplayName : w1.ItemPath;
            _nameLabel.Text = name;
            _countLabel.Text = $"Count: {w1.Count}";
            _tileLabel.Text = $"Tile: ({w1.Tile.X}, {w1.Tile.Y})";
            _stateLabel.Text = w1.Forbidden ? "Forbidden" : "Haulable";
            _selectionForbidden = w1.Forbidden;
            _forbidBtn.Text = w1.Forbidden ? "Unforbid" : "Forbid";
        }
        else
        {
            _nameLabel.Text = $"Items ({found})";
            _countLabel.Text = $"Total: {totalCount}";
            _tileLabel.Text = first is WoodState f
                ? $"First: ({f.Tile.X}, {f.Tile.Y})"
                : "";
            _stateLabel.Text = $"{forbidden} forbidden · {haulable} haulable";
            // Majority rules: if more than half are forbidden, button unforbids.
            _selectionForbidden = forbidden > haulable;
            _forbidBtn.Text = _selectionForbidden ? "Unforbid All" : "Forbid All";
        }
    }

    private void OnForbidPressed()
    {
        if (Host is null) return;
        var ids = Host.SelectedWoodIds;
        if (ids.Length == 0) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        bool target = !_selectionForbidden;
        var idSet = new HashSet<int>(ids);
        foreach (var w in snap.Wood)
        {
            if (!idSet.Contains(w.EntityId)) continue;
            if (w.Forbidden == target) continue;
            Host.QueueCommand(new ForbidStackCommand(w.EntityId, target));
        }
    }
}
