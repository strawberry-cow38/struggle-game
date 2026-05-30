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

    // One selected dropped stack, normalized across Wood + ItemPile.
    private readonly record struct Stack(int Id, Sim.Map.TilePos Tile, int Count, string Path, bool Forbidden, string? Label);

    private static void CollectSelected(SimSnapshot snap, HashSet<int> idSet, List<Stack> outList)
    {
        foreach (var p in snap.ItemPiles)
            if (idSet.Contains(p.EntityId))
                outList.Add(new Stack(p.EntityId, p.Tile, p.Count, p.ItemPath, p.Forbidden, p.Label));
    }

    private void Render(SimSnapshot snap, int[] ids)
    {
        var stacks = new List<Stack>(ids.Length);
        CollectSelected(snap, new HashSet<int>(ids), stacks);
        if (stacks.Count == 0)
        {
            // All stacks vanished (picked up / merged).
            Host!.SelectedWoodIds = Array.Empty<int>();
            return;
        }

        int totalCount = 0, forbidden = 0, haulable = 0;
        foreach (var s in stacks)
        {
            totalCount += s.Count;
            if (s.Forbidden) forbidden++; else haulable++;
        }

        if (stacks.Count == 1)
        {
            var s = stacks[0];
            string name = s.Label ?? (ItemCatalog.ItemsByPath.TryGetValue(s.Path, out var def)
                ? def.DisplayName : s.Path);
            _nameLabel.Text = name;
            _countLabel.Text = $"Count: {s.Count}";
            _tileLabel.Text = $"Tile: ({s.Tile.X}, {s.Tile.Y})";
            _stateLabel.Text = s.Forbidden ? "Forbidden" : "Haulable";
            _selectionForbidden = s.Forbidden;
            _forbidBtn.Text = s.Forbidden ? "Unforbid" : "Forbid";
        }
        else
        {
            _nameLabel.Text = $"Items ({stacks.Count})";
            _countLabel.Text = $"Total: {totalCount}";
            _tileLabel.Text = $"First: ({stacks[0].Tile.X}, {stacks[0].Tile.Y})";
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
        var stacks = new List<Stack>(ids.Length);
        CollectSelected(snap, new HashSet<int>(ids), stacks);
        foreach (var s in stacks)
        {
            if (s.Forbidden == target) continue;
            Host.QueueCommand(new ForbidStackCommand(s.Id, target));
        }
    }
}
