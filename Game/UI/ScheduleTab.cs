using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Schedule panel. Rows = pawns, columns = 24 hours. Click a category
// swatch up top to arm a paint color, then click or drag across cells
// to repaint them. The schedule is a soft guide — DummyController
// consults it only when looking for a *new* job, so mid-job pawns
// finish what they're doing regardless of the current slot.
public partial class ScheduleTab : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int RowHeight = 22;
    private const int NameColumnWidth = 140;
    private const int CellWidth = 22;
    private const int Padding = 10;
    private const int MarginTop = 80;

    private static readonly Color AnyColor        = new(0.55f, 0.55f, 0.55f);
    private static readonly Color WorkColor       = new(0.90f, 0.65f, 0.20f);
    private static readonly Color SleepColor      = new(0.20f, 0.30f, 0.60f);
    private static readonly Color RecreationColor = new(0.30f, 0.70f, 0.40f);

    private Panel _root = null!;
    private GridContainer _grid = null!;
    private Label _hourCursorLabel = null!;
    private bool _open;
    private long _lastSnapshotTick = -1;
    private bool _suppressEvents;
    private PawnWorkState[] _rows = System.Array.Empty<PawnWorkState>();
    // Content signature of the grid last built. The grid cells carry
    // GuiInput / MouseEntered drag-paint handlers; rebuilding every tick
    // would tear a cell out mid-drag. Rebuild only when this changes.
    private string _lastGridSig = "";
    private ScheduleCategory _activePaint = ScheduleCategory.Work;
    private readonly Button[] _swatches = new Button[4];
    // Maps each cell ColorRect (one per pawn × hour) back to its
    // (entityId, hour) so the MouseEntered drag handler can issue the
    // right paint command without per-cell lambdas owning that state.
    private readonly Dictionary<int, (int EntityId, int Hour)> _cellLookup = new();

    public override void _Ready()
    {
        Layer = 96;

        _root = new Panel
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        AddChild(_root);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = Padding, OffsetTop = Padding,
            OffsetRight = -Padding, OffsetBottom = -Padding,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddThemeConstantOverride("separation", 6);
        _root.AddChild(vbox);

        var headerRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var header = new Label { Text = "Schedule" };
        header.AddThemeFontSizeOverride("font_size", 18);
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(header);
        _hourCursorLabel = new Label { Text = "" };
        _hourCursorLabel.AddThemeFontSizeOverride("font_size", 12);
        headerRow.AddChild(_hourCursorLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 28) };
        closeBtn.Pressed += Close;
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        var swatchRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        swatchRow.AddThemeConstantOverride("separation", 6);
        _swatches[0] = MakeSwatch(swatchRow, ScheduleCategory.Any, "Any", AnyColor);
        _swatches[1] = MakeSwatch(swatchRow, ScheduleCategory.Work, "Work", WorkColor);
        _swatches[2] = MakeSwatch(swatchRow, ScheduleCategory.Sleep, "Sleep", SleepColor);
        _swatches[3] = MakeSwatch(swatchRow, ScheduleCategory.Recreation, "Rec", RecreationColor);
        vbox.AddChild(swatchRow);
        UpdateSwatchPressedStates();

        vbox.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(scroll);

        _grid = new GridContainer
        {
            Columns = 1 + Schedule.Hours,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _grid.AddThemeConstantOverride("h_separation", 2);
        _grid.AddThemeConstantOverride("v_separation", 2);
        scroll.AddChild(_grid);

        GetTree().Root.SizeChanged += Reposition;
        CallDeferred(nameof(Reposition));
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    public void Toggle() { if (_open) Close(); else Open(); }

    public void Open()
    {
        _open = true;
        _root.Visible = true;
        _lastSnapshotTick = -1;
    }

    public void Close()
    {
        _open = false;
        _root.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!_open || Host is null) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        if (snap.Tick == _lastSnapshotTick) return;
        _lastSnapshotTick = snap.Tick;
        Render(snap);
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        int gridWidth = NameColumnWidth + (CellWidth + 2) * Schedule.Hours + 2 * Padding + 24;
        int width = Math.Min(gridWidth, (int)vp.X - 32);
        int height = (int)Math.Min(480f, vp.Y - MarginTop - 96);
        _root.Position = new Vector2((vp.X - width) * 0.5f, MarginTop);
        _root.Size = new Vector2(width, height);
    }

    private Button MakeSwatch(HBoxContainer row, ScheduleCategory cat, string label, Color color)
    {
        var btn = new Button
        {
            Text = label,
            ToggleMode = true,
            CustomMinimumSize = new Vector2(60, 24),
            FocusMode = Control.FocusModeEnum.None,
        };
        var sb = new StyleBoxFlat { BgColor = color };
        btn.AddThemeStyleboxOverride("normal", sb);
        btn.AddThemeStyleboxOverride("hover", sb);
        btn.AddThemeStyleboxOverride("pressed", sb);
        btn.Pressed += () =>
        {
            _activePaint = cat;
            UpdateSwatchPressedStates();
        };
        row.AddChild(btn);
        return btn;
    }

    private void UpdateSwatchPressedStates()
    {
        for (int i = 0; i < 4; i++)
        {
            var cat = (ScheduleCategory)i;
            // Swatches are indexed in the order Any, Work, Sleep, Recreation;
            // mirror that to the category enum.
            if (_swatches[i] is null) continue;
            _swatches[i].SetPressedNoSignal(cat == _activePaint);
        }
    }

    private void Render(SimSnapshot snap)
    {
        _suppressEvents = true;
        int curHour = ((int)Math.Floor(snap.WorldTimeSec / 3600.0)) % 24;
        if (curHour < 0) curHour += 24;
        _hourCursorLabel.Text = $"Hour {curHour:00} · paint = {_activePaint}";

        var pw = snap.PawnWork;
        _rows = new PawnWorkState[pw.Length];
        for (int i = 0; i < pw.Length; i++) _rows[i] = pw[i];

        // Rebuild the grid only when schedule content or the highlighted
        // current hour changes — otherwise the persistent cells keep their
        // drag-paint handlers alive across ticks.
        string sig = BuildGridSignature(curHour, _rows);
        if (sig == _lastGridSig) { _suppressEvents = false; return; }
        _lastGridSig = sig;

        foreach (var child in _grid.GetChildren()) child.QueueFree();
        _cellLookup.Clear();

        var nameHdr = new Label
        {
            Text = "Colonist",
            CustomMinimumSize = new Vector2(NameColumnWidth, RowHeight),
        };
        nameHdr.AddThemeFontSizeOverride("font_size", 12);
        _grid.AddChild(nameHdr);
        for (int h = 0; h < Schedule.Hours; h++)
        {
            var hl = new Label
            {
                Text = h.ToString("00"),
                HorizontalAlignment = HorizontalAlignment.Center,
                CustomMinimumSize = new Vector2(CellWidth, RowHeight),
            };
            hl.AddThemeFontSizeOverride("font_size", 9);
            if (h == curHour) hl.Modulate = new Color(1.4f, 1.4f, 0.5f);
            _grid.AddChild(hl);
        }

        for (int r = 0; r < _rows.Length; r++)
        {
            var pawn = _rows[r];
            var nameLbl = new Label
            {
                Text = pawn.Name,
                CustomMinimumSize = new Vector2(NameColumnWidth, RowHeight),
            };
            _grid.AddChild(nameLbl);

            for (int h = 0; h < Schedule.Hours; h++)
            {
                byte slot = h < pawn.Schedule.Length ? pawn.Schedule[h] : (byte)0;
                var cell = new ColorRect
                {
                    Color = ColorFor((ScheduleCategory)slot),
                    CustomMinimumSize = new Vector2(CellWidth, RowHeight),
                    MouseFilter = Control.MouseFilterEnum.Stop,
                };
                int pawnId = pawn.EntityId;
                int hour = h;
                _cellLookup[cell.GetInstanceId().GetHashCode()] = (pawnId, hour);
                cell.GuiInput += e => OnCellInput(e, pawnId, hour);
                cell.MouseEntered += () => OnCellHovered(pawnId, hour);
                if (h == curHour)
                {
                    cell.Modulate = new Color(1.3f, 1.3f, 1.3f);
                }
                _grid.AddChild(cell);
            }
        }

        _suppressEvents = false;
    }

    private readonly System.Text.StringBuilder _sigSb = new();
    private string BuildGridSignature(int curHour, PawnWorkState[] rows)
    {
        var sb = _sigSb;
        sb.Clear();
        sb.Append(curHour).Append('|');
        foreach (var r in rows)
        {
            sb.Append(r.EntityId).Append(':').Append(r.Name).Append(':');
            foreach (var h in r.Schedule) sb.Append(h).Append(',');
            sb.Append('#');
        }
        return sb.ToString();
    }

    private static Color ColorFor(ScheduleCategory cat) => cat switch
    {
        ScheduleCategory.Work => WorkColor,
        ScheduleCategory.Sleep => SleepColor,
        ScheduleCategory.Recreation => RecreationColor,
        _ => AnyColor,
    };

    private void OnCellInput(InputEvent e, int pawnId, int hour)
    {
        if (_suppressEvents || Host is null) return;
        if (e is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Left) return;
        Host.QueueCommand(new PaintScheduleCommand(pawnId, hour, hour, _activePaint));
    }

    private void OnCellHovered(int pawnId, int hour)
    {
        if (_suppressEvents || Host is null) return;
        if (!Input.IsMouseButtonPressed(MouseButton.Left)) return;
        Host.QueueCommand(new PaintScheduleCommand(pawnId, hour, hour, _activePaint));
    }
}
