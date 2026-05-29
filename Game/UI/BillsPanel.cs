using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Center-screen bills editor for a selected stove. RimWorld-style:
// each row picks recipe + repeat mode + count + output dest + (optional)
// specific stockpile. Up/Down reorders. Trash removes. Bottom "Add" picks
// a recipe + appends a new bill.
public partial class BillsPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 640;
    private const int PanelHeight = 460;

    private Panel _root = null!;
    private VBoxContainer _list = null!;
    private OptionButton _addRecipeBtn = null!;
    private int _stoveEntityId;
    private long _lastShownTick = -1;
    private int _lastBillCount = -1;

    public override void _Ready()
    {
        Layer = 100;
        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, PanelHeight),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        AddChild(_root);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 12, OffsetTop = 12, OffsetRight = -12, OffsetBottom = -12,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddThemeConstantOverride("separation", 6);
        _root.AddChild(vbox);

        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var title = new Label { Text = "Bills" };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += Close;
        header.AddChild(closeBtn);
        vbox.AddChild(header);

        vbox.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddChild(scroll);

        _list = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_list);

        vbox.AddChild(new HSeparator());

        var addRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        addRow.AddChild(new Label { Text = "Add bill:" });
        _addRecipeBtn = new OptionButton { CustomMinimumSize = new Vector2(220, 28) };
        foreach (var r in Recipes.All)
        {
            _addRecipeBtn.AddItem(r.DisplayName, (int)r.Id);
        }
        _addRecipeBtn.Selected = 0;
        addRow.AddChild(_addRecipeBtn);
        var addBtn = new Button { Text = "Add", CustomMinimumSize = new Vector2(80, 28) };
        addBtn.Pressed += OnAddBill;
        addRow.AddChild(addBtn);
        vbox.AddChild(addRow);

        GetTree().Root.SizeChanged += Reposition;
        CallDeferred(nameof(Reposition));
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    public void Open(int stoveEntityId)
    {
        _stoveEntityId = stoveEntityId;
        _root.Visible = true;
        _lastShownTick = -1;
        _lastBillCount = -1;
        Reposition();
    }

    public void Close()
    {
        _root.Visible = false;
        _stoveEntityId = 0;
    }

    public override void _Process(double delta)
    {
        if (!_root.Visible || Host is null) return;
        if (_stoveEntityId == 0) { Close(); return; }
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        // Bail if the stove disappeared (deconstructed, etc.).
        StoveState? found = null;
        foreach (var s in snap.Stoves)
        {
            if (s.EntityId == _stoveEntityId) { found = s; break; }
        }
        if (found is null) { Close(); return; }
        // Repopulate when bill count changes, otherwise leave OptionButtons
        // alone so the user can interact without inputs being reset.
        var st = found.Value;
        if (st.Bills.Length != _lastBillCount)
        {
            BuildRows(st);
            _lastBillCount = st.Bills.Length;
        }
        _lastShownTick = snap.Tick;
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _root.Position = new Vector2((vp.X - PanelWidth) * 0.5f, (vp.Y - PanelHeight) * 0.5f);
        _root.Size = new Vector2(PanelWidth, PanelHeight);
    }

    private void BuildRows(StoveState st)
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();

        for (int i = 0; i < st.Bills.Length; i++)
        {
            var row = MakeBillRow(st.Bills[i], i, st.Bills.Length);
            _list.AddChild(row);
        }
    }

    private Control MakeBillRow(BillState bill, int idx, int total)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 6);
        panel.AddChild(row);

        var recipe = Recipes.Get(bill.Recipe);
        var name = new Label { Text = recipe.DisplayName, CustomMinimumSize = new Vector2(140, 0) };
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(name);

        var modeBtn = new OptionButton { CustomMinimumSize = new Vector2(120, 26) };
        modeBtn.AddItem("Forever", (int)BillRepeatMode.Forever);
        modeBtn.AddItem("Until X", (int)BillRepeatMode.DoUntilCount);
        modeBtn.AddItem("Do X", (int)BillRepeatMode.DoXTimes);
        modeBtn.Selected = (int)bill.RepeatMode;
        row.AddChild(modeBtn);

        var countSpin = new SpinBox
        {
            MinValue = 1,
            MaxValue = 9999,
            Value = bill.RepeatMode == BillRepeatMode.DoUntilCount ? bill.TargetCount : bill.RemainingCount,
            Step = 1,
            CustomMinimumSize = new Vector2(80, 26),
        };
        countSpin.Editable = bill.RepeatMode != BillRepeatMode.Forever;
        row.AddChild(countSpin);

        var destBtn = new OptionButton { CustomMinimumSize = new Vector2(130, 26) };
        destBtn.AddItem("Drop", (int)BillOutputDest.DropAtWorkbench);
        destBtn.AddItem("Any pile", (int)BillOutputDest.AnyStockpile);
        destBtn.AddItem("Specific", (int)BillOutputDest.SpecificStockpile);
        destBtn.Selected = (int)bill.OutputDest;
        row.AddChild(destBtn);

        var stockBtn = new OptionButton { CustomMinimumSize = new Vector2(110, 26) };
        var stockIds = new List<int> { 0 };
        stockBtn.AddItem("(none)", 0);
        if (Host?.LatestSnapshot is not null)
        {
            foreach (var sp in Host.LatestSnapshot.Stockpiles)
            {
                stockBtn.AddItem(sp.Name, sp.Id);
                stockIds.Add(sp.Id);
            }
        }
        int curStockIdx = stockIds.IndexOf(bill.StockpileEntityId);
        stockBtn.Selected = curStockIdx >= 0 ? curStockIdx : 0;
        stockBtn.Visible = bill.OutputDest == BillOutputDest.SpecificStockpile;
        row.AddChild(stockBtn);

        var upBtn = new Button { Text = "↑", CustomMinimumSize = new Vector2(28, 26) };
        upBtn.Disabled = idx == 0;
        upBtn.Pressed += () => OnReorder(idx, idx - 1);
        row.AddChild(upBtn);
        var downBtn = new Button { Text = "↓", CustomMinimumSize = new Vector2(28, 26) };
        downBtn.Disabled = idx == total - 1;
        downBtn.Pressed += () => OnReorder(idx, idx + 1);
        row.AddChild(downBtn);
        var delBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 26) };
        delBtn.Pressed += () => OnDelete(idx);
        row.AddChild(delBtn);

        // Wire mutation handlers — any of mode/count/dest/stock sends a
        // single UpdateBillCommand with the row's current state.
        void PushUpdate()
        {
            if (Host is null) return;
            var mode = (BillRepeatMode)modeBtn.GetSelectedId();
            int count = (int)countSpin.Value;
            int target = mode == BillRepeatMode.DoUntilCount ? count : 0;
            int remaining = mode == BillRepeatMode.DoXTimes ? count : 0;
            var dest = (BillOutputDest)destBtn.GetSelectedId();
            int spId = stockBtn.GetSelectedId();
            Host.QueueCommand(new UpdateBillCommand(
                _stoveEntityId, idx, mode, target, remaining, dest, spId));
        }
        modeBtn.ItemSelected += _ =>
        {
            var mode = (BillRepeatMode)modeBtn.GetSelectedId();
            countSpin.Editable = mode != BillRepeatMode.Forever;
            PushUpdate();
        };
        countSpin.ValueChanged += _ => PushUpdate();
        destBtn.ItemSelected += _ =>
        {
            stockBtn.Visible = (BillOutputDest)destBtn.GetSelectedId() == BillOutputDest.SpecificStockpile;
            PushUpdate();
        };
        stockBtn.ItemSelected += _ => PushUpdate();

        return panel;
    }

    private void OnReorder(int from, int to)
    {
        if (Host is null) return;
        Host.QueueCommand(new ReorderBillCommand(_stoveEntityId, from, to));
        _lastBillCount = -1;
    }

    private void OnDelete(int idx)
    {
        if (Host is null) return;
        Host.QueueCommand(new RemoveBillCommand(_stoveEntityId, idx));
        _lastBillCount = -1;
    }

    private void OnAddBill()
    {
        if (Host is null) return;
        var id = (RecipeId)_addRecipeBtn.GetSelectedId();
        Host.QueueCommand(new AddBillCommand(
            _stoveEntityId, id, BillRepeatMode.Forever, 0, 0, BillOutputDest.DropAtWorkbench, 0));
        _lastBillCount = -1;
    }
}
