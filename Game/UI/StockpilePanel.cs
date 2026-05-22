using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.Stockpiles;

namespace StruggleGame.Game.UI;

// Right-side panel that appears when a stockpile zone is selected.
// Editable name, priority dropdown, searchable category/item checklist,
// and Expand / Shrink / Delete buttons. The Tree control's CellMode.Check
// gives us the checkbox UX for free; allowed state mirrors the snapshot.
public partial class StockpilePanel : CanvasLayer
{
    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private const int PanelWidth = 360;
    private const int MarginRight = 16;
    private const int MarginTop = 16;
    private const int MarginBottom = 96;

    private Panel _root = null!;
    private LineEdit _nameEdit = null!;
    private OptionButton _priorityOpt = null!;
    private LineEdit _searchEdit = null!;
    private Tree _filterTree = null!;
    private Button _expandBtn = null!;
    private Button _shrinkBtn = null!;
    private Button _deleteBtn = null!;
    private Label _summaryLabel = null!;

    private int _shownStockpileId = -1;
    private long _lastSnapshotTick = -1;
    // Suppress re-firing change handlers when we rebuild the tree from
    // a snapshot — otherwise the rebuild would echo back as user clicks.
    private bool _suppressEvents;
    private string _searchFilter = string.Empty;

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 480),
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
        var header = new Label { Text = "Stockpile", CustomMinimumSize = new Vector2(0, 24) };
        header.AddThemeFontSizeOverride("font_size", 18);
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(header);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedStockpileId = null;
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        vbox.AddChild(new Label { Text = "Name" });
        _nameEdit = new LineEdit { CustomMinimumSize = new Vector2(0, 28) };
        _nameEdit.TextSubmitted += OnNameSubmitted;
        _nameEdit.FocusExited += () => OnNameSubmitted(_nameEdit.Text);
        vbox.AddChild(_nameEdit);

        vbox.AddChild(new Label { Text = "Priority" });
        _priorityOpt = new OptionButton { CustomMinimumSize = new Vector2(0, 28) };
        _priorityOpt.AddItem("Low", (int)StockpilePriority.Low);
        _priorityOpt.AddItem("Normal", (int)StockpilePriority.Normal);
        _priorityOpt.AddItem("High", (int)StockpilePriority.High);
        _priorityOpt.AddItem("Critical", (int)StockpilePriority.Critical);
        _priorityOpt.ItemSelected += OnPriorityChanged;
        vbox.AddChild(_priorityOpt);

        var btnRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btnRow.AddThemeConstantOverride("separation", 6);
        _expandBtn = new Button { Text = "Expand", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _expandBtn.Pressed += () => { if (Tools is not null) Tools.Mode = ToolMode.StockpileExpand; };
        btnRow.AddChild(_expandBtn);
        _shrinkBtn = new Button { Text = "Shrink", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _shrinkBtn.Pressed += () => { if (Tools is not null) Tools.Mode = ToolMode.StockpileShrink; };
        btnRow.AddChild(_shrinkBtn);
        _deleteBtn = new Button { Text = "Delete", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _deleteBtn.Pressed += OnDeletePressed;
        btnRow.AddChild(_deleteBtn);
        vbox.AddChild(btnRow);

        vbox.AddChild(new HSeparator());

        vbox.AddChild(new Label { Text = "Filter" });
        _searchEdit = new LineEdit { PlaceholderText = "Search…", CustomMinimumSize = new Vector2(0, 28) };
        _searchEdit.TextChanged += s => { _searchFilter = s ?? string.Empty; RebuildTree(); };
        vbox.AddChild(_searchEdit);

        _filterTree = new Tree
        {
            HideRoot = true,
            CustomMinimumSize = new Vector2(0, 200),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _filterTree.ItemEdited += OnTreeItemEdited;
        vbox.AddChild(_filterTree);

        _summaryLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _summaryLabel.AddThemeFontSizeOverride("font_size", 12);
        vbox.AddChild(_summaryLabel);

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
        int? sel = Host.SelectedStockpileId;
        var snap = Host.LatestSnapshot;
        if (sel is null || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownStockpileId = -1; }
            return;
        }
        var sp = FindStockpile(snap, sel.Value);
        if (sp is null)
        {
            // Zone deleted under us — clear selection.
            Host.SelectedStockpileId = null;
            _root.Visible = false;
            _shownStockpileId = -1;
            return;
        }
        if (!_root.Visible) _root.Visible = true;

        bool zoneChanged = sp.Value.Id != _shownStockpileId;
        bool tickChanged = snap.Tick != _lastSnapshotTick;
        if (zoneChanged || tickChanged)
        {
            Render(sp.Value, snapshotChanged: zoneChanged);
            _shownStockpileId = sp.Value.Id;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        float height = Math.Max(420f, vp.Y - MarginTop - MarginBottom);
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, height);
    }

    private static StockpileState? FindStockpile(SimSnapshot snap, int id)
    {
        foreach (var sp in snap.Stockpiles)
        {
            if (sp.Id == id) return sp;
        }
        return null;
    }

    private void Render(StockpileState sp, bool snapshotChanged)
    {
        _suppressEvents = true;
        // Don't clobber the name field if the user is currently typing
        // into it — only refresh when the underlying zone changed.
        if (snapshotChanged || !_nameEdit.HasFocus()) _nameEdit.Text = sp.Name;
        if (snapshotChanged) _priorityOpt.Selected = (int)sp.Priority;
        _summaryLabel.Text = $"{sp.Tiles.Length} tile(s) · {sp.AllowedItemPaths.Length} item(s) allowed";
        _suppressEvents = false;

        RebuildTree(sp);
    }

    private void RebuildTree(StockpileState? spOverride = null)
    {
        if (Host is null) return;
        int? sel = Host.SelectedStockpileId;
        if (sel is null) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        var sp = spOverride ?? FindStockpile(snap, sel.Value);
        if (sp is null) return;

        _suppressEvents = true;
        _filterTree.Clear();
        var root = _filterTree.CreateItem();
        var allowed = new HashSet<string>(sp.Value.AllowedItemPaths);

        string filter = _searchFilter.Trim().ToLowerInvariant();
        foreach (var cat in ItemCatalog.Roots)
        {
            BuildCategoryNode(root, cat, allowed, filter);
        }
        _suppressEvents = false;
    }

    // Returns true if this subtree contributed any visible row, so the
    // parent can prune itself when no match exists.
    private bool BuildCategoryNode(TreeItem parent, ItemCategory cat, HashSet<string> allowed, string filter)
    {
        bool nameMatch = string.IsNullOrEmpty(filter) || cat.DisplayName.ToLowerInvariant().Contains(filter);

        var item = _filterTree.CreateItem(parent);
        item.SetText(0, cat.DisplayName);
        item.SetCellMode(0, TreeItem.TreeCellMode.Check);
        item.SetEditable(0, true);
        item.SetMetadata(0, "cat:" + cat.FullPath);
        // A category checkbox is "checked" iff every item beneath it is
        // currently allowed — same convention as the sim's setter.
        bool allAllowed = true;
        foreach (var def in EnumerateItemsUnder(cat))
        {
            if (!allowed.Contains(def.FullPath)) { allAllowed = false; break; }
        }
        item.SetChecked(0, allAllowed);

        bool anyVisibleChild = false;
        foreach (var sub in cat.Subcategories)
        {
            if (BuildCategoryNode(item, sub, allowed, filter)) anyVisibleChild = true;
        }
        foreach (var def in cat.Items)
        {
            bool itemMatch = string.IsNullOrEmpty(filter) || def.DisplayName.ToLowerInvariant().Contains(filter);
            if (!itemMatch && !nameMatch) continue;
            var leaf = _filterTree.CreateItem(item);
            leaf.SetText(0, def.DisplayName);
            leaf.SetCellMode(0, TreeItem.TreeCellMode.Check);
            leaf.SetEditable(0, true);
            leaf.SetMetadata(0, "item:" + def.FullPath);
            leaf.SetChecked(0, allowed.Contains(def.FullPath));
            anyVisibleChild = true;
        }

        if (!nameMatch && !anyVisibleChild)
        {
            // No descendants matched and the category itself didn't —
            // hide the row to keep the search filter honest.
            item.Free();
            return false;
        }
        return true;
    }

    private static IEnumerable<ItemDef> EnumerateItemsUnder(ItemCategory cat)
    {
        foreach (var def in cat.Items) yield return def;
        foreach (var sub in cat.Subcategories)
        {
            foreach (var def in EnumerateItemsUnder(sub)) yield return def;
        }
    }

    private void OnTreeItemEdited()
    {
        if (_suppressEvents || Host is null) return;
        int? sel = Host.SelectedStockpileId;
        if (sel is null) return;
        var edited = _filterTree.GetEdited();
        if (edited is null) return;
        var meta = edited.GetMetadata(0).AsString();
        bool checkedNow = edited.IsChecked(0);
        if (meta.StartsWith("cat:"))
        {
            string path = meta.Substring("cat:".Length);
            Host.QueueCommand(new SetStockpileCategoryAllowedCommand(sel.Value, path, checkedNow));
        }
        else if (meta.StartsWith("item:"))
        {
            string path = meta.Substring("item:".Length);
            Host.QueueCommand(new SetStockpileItemAllowedCommand(sel.Value, path, checkedNow));
        }
    }

    private void OnNameSubmitted(string text)
    {
        if (_suppressEvents || Host is null) return;
        int? sel = Host.SelectedStockpileId;
        if (sel is null) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        Host.QueueCommand(new RenameStockpileCommand(sel.Value, text.Trim()));
    }

    private void OnPriorityChanged(long index)
    {
        if (_suppressEvents || Host is null) return;
        int? sel = Host.SelectedStockpileId;
        if (sel is null) return;
        var p = (StockpilePriority)_priorityOpt.GetItemId((int)index);
        Host.QueueCommand(new SetStockpilePriorityCommand(sel.Value, p));
    }

    private void OnDeletePressed()
    {
        if (Host is null) return;
        int? sel = Host.SelectedStockpileId;
        if (sel is null) return;
        Host.QueueCommand(new DeleteStockpileCommand(sel.Value));
        Host.SelectedStockpileId = null;
    }
}
