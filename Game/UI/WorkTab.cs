using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.Work;

namespace StruggleGame.Game.UI;

// Work tab panel. Rows = colonists, columns = WorkType. Two modes:
//   Checkmark — each cell is on/off; on cells use DefaultPriority.
//   Priority 1-8 — each cell shows "-" or a digit (1 highest..8 lowest).
//                  LMB increments (… → 8 → 0 → 1 → 2 …), RMB decrements.
// Switching modes preserves the priority numbers — flipping back to
// priority mode shows the previous tuning.
public partial class WorkTab : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int RowHeight = 28;
    private const int NameColumnWidth = 140;
    private const int CellWidth = 64;
    private const int Padding = 10;
    private const int MarginTop = 80;

    private Panel _root = null!;
    private Button _modeBtn = null!;
    private GridContainer _grid = null!;
    private bool _open;
    private long _lastSnapshotTick = -1;
    private bool _suppressEvents;
    // Cache the latest published per-pawn rows so cell handlers can read
    // current values without round-tripping through the snapshot.
    private PawnWorkState[] _rows = System.Array.Empty<PawnWorkState>();
    private bool _checkmarkMode = true;
    // Content signature of the grid last built. The grid holds clickable
    // Button cells; rebuilding it every snapshot tick would QueueFree a
    // cell mid-click and swallow the press. Rebuild only when this changes.
    private string _lastGridSig = "";

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
        var header = new Label { Text = "Work" };
        header.AddThemeFontSizeOverride("font_size", 18);
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(header);
        _modeBtn = new Button { Text = "Mode: Checkmark", CustomMinimumSize = new Vector2(160, 28) };
        _modeBtn.Pressed += OnModePressed;
        headerRow.AddChild(_modeBtn);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 28) };
        closeBtn.Pressed += Close;
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(scroll);

        _grid = new GridContainer
        {
            Columns = 1 + WorkTypes.Count,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _grid.AddThemeConstantOverride("h_separation", 4);
        _grid.AddThemeConstantOverride("v_separation", 4);
        scroll.AddChild(_grid);

        GetTree().Root.SizeChanged += Reposition;
        CallDeferred(nameof(Reposition));
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    public void Toggle()
    {
        if (_open) Close(); else Open();
    }

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
        int gridWidth = NameColumnWidth + (CellWidth + 4) * WorkTypes.Count + 2 * Padding + 24;
        int width = Math.Min(gridWidth, (int)vp.X - 32);
        int height = (int)Math.Min(480f, vp.Y - MarginTop - 96);
        _root.Position = new Vector2((vp.X - width) * 0.5f, MarginTop);
        _root.Size = new Vector2(width, height);
    }

    private void Render(SimSnapshot snap)
    {
        _suppressEvents = true;
        _checkmarkMode = snap.CheckmarkMode;
        _modeBtn.Text = _checkmarkMode ? "Mode: Checkmark" : "Mode: Priority";

        var pw = snap.PawnWork;
        _rows = new PawnWorkState[pw.Length];
        for (int i = 0; i < pw.Length; i++) _rows[i] = pw[i];

        // Skip the node rebuild when nothing the grid shows has changed
        // (keeps cell Buttons alive across ticks so clicks register).
        string sig = BuildGridSignature(_checkmarkMode, _rows);
        if (sig == _lastGridSig) { _suppressEvents = false; return; }
        _lastGridSig = sig;

        foreach (var child in _grid.GetChildren()) child.QueueFree();

        // Header row.
        var nameHdr = new Label
        {
            Text = "Colonist",
            CustomMinimumSize = new Vector2(NameColumnWidth, RowHeight),
        };
        nameHdr.AddThemeFontSizeOverride("font_size", 12);
        _grid.AddChild(nameHdr);
        for (int c = 0; c < WorkTypes.Count; c++)
        {
            var hl = new Label
            {
                Text = WorkTypes.Names[c],
                HorizontalAlignment = HorizontalAlignment.Center,
                CustomMinimumSize = new Vector2(CellWidth, RowHeight),
            };
            hl.AddThemeFontSizeOverride("font_size", 12);
            _grid.AddChild(hl);
        }

        // One row per pawn.
        for (int r = 0; r < _rows.Length; r++)
        {
            var pawn = _rows[r];
            var nameLbl = new Label
            {
                Text = pawn.Name,
                CustomMinimumSize = new Vector2(NameColumnWidth, RowHeight),
            };
            _grid.AddChild(nameLbl);

            for (int c = 0; c < WorkTypes.Count; c++)
            {
                var type = (WorkType)c;
                int pawnId = pawn.EntityId;
                byte priority = c < pawn.Priorities.Length ? pawn.Priorities[c] : (byte)0;
                bool allowed = c < pawn.Allowed.Length && pawn.Allowed[c];
                var cell = new Button
                {
                    Text = CellLabel(_checkmarkMode, priority, allowed),
                    CustomMinimumSize = new Vector2(CellWidth, RowHeight),
                    FocusMode = Control.FocusModeEnum.None,
                    MouseFilter = Control.MouseFilterEnum.Stop,
                };
                cell.GuiInput += @e => OnCellInput(@e, pawnId, type);
                _grid.AddChild(cell);
            }
        }

        _suppressEvents = false;
    }

    private static string BuildGridSignature(bool checkmark, PawnWorkState[] rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(checkmark ? '1' : '0').Append('|');
        foreach (var r in rows)
        {
            sb.Append(r.EntityId).Append(':').Append(r.Name).Append(':');
            foreach (var p in r.Priorities) sb.Append(p).Append(',');
            sb.Append(';');
            foreach (var a in r.Allowed) sb.Append(a ? '1' : '0');
            sb.Append('#');
        }
        return sb.ToString();
    }

    private static string CellLabel(bool checkmark, byte priority, bool allowed)
    {
        if (checkmark) return allowed ? "✓" : " ";
        return priority == 0 ? "-" : priority.ToString();
    }

    private void OnCellInput(InputEvent e, int pawnId, WorkType type)
    {
        if (_suppressEvents || Host is null) return;
        if (e is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Left && mb.ButtonIndex != MouseButton.Right) return;

        // Find current row state. If pawn isn't in the cache (just spawned)
        // skip — next snapshot tick will repaint.
        PawnWorkState? row = null;
        foreach (var r in _rows) if (r.EntityId == pawnId) { row = r; break; }
        if (row is null) return;
        int idx = (int)type;

        if (_checkmarkMode)
        {
            // Either button just flips the allowed bit — there's no "up"
            // and "down" when each cell is a binary toggle.
            bool now = idx < row.Value.Allowed.Length && row.Value.Allowed[idx];
            Host.QueueCommand(new SetWorkCheckmarkCommand(pawnId, type, !now));
            return;
        }

        byte cur = idx < row.Value.Priorities.Length ? row.Value.Priorities[idx] : (byte)0;
        byte next;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            // LMB cycles up: 0→1→2→…→8→0
            next = (byte)((cur + 1) % 9);
        }
        else
        {
            // RMB cycles down: 0→8→7→…→1→0
            next = cur == 0 ? (byte)8 : (byte)(cur - 1);
        }
        Host.QueueCommand(new SetWorkPriorityCommand(pawnId, type, next));
    }

    private void OnModePressed()
    {
        if (Host is null) return;
        Host.QueueCommand(new SetCheckmarkModeCommand(!_checkmarkMode));
    }
}
