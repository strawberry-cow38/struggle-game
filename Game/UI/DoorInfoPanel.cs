using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected door. Shows orientation + open amount,
// exposes Forbid + Locked toggles, a priority cycle and a Deconstruct
// button. Forbidden = pathing treats it as a wall. Locked is a stub for
// the not-yet-shipped enemies pass. See TileInfoPanel.
public partial class DoorInfoPanel : TileInfoPanel
{
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private CheckBox _forbidChk = null!;
    private CheckBox _lockedChk = null!;
    private Button _priorityBtn = null!;
    private DoorPriority _shownPriority = DoorPriority.Medium;
    private Button _deconBtn = null!;
    private bool _suppressToggle;

    protected override TilePos[] SelectedTiles
    {
        get => Host!.SelectedDoorTiles;
        set => Host!.SelectedDoorTiles = value;
    }
    protected override string Title => "Door";
    protected override int MinHeight => 220;

    protected override void BuildBody(VBoxContainer vbox)
    {
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
    }

    protected override void Render(SimSnapshot snap, TilePos[] tiles)
    {
        // Build a lookup of live doors from the snapshot, then prune any
        // selected tile whose door vanished (decon'd / scrubbed).
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
            SelectedTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            SelectedTiles = liveTiles.ToArray();
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
            NameLabel.Text = $"Door ({d0.Orientation})";
            _tileLabel.Text = $"Tile: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _stateLabel.Text = $"Open: {d0.OpenAmount * 100f:0}%";
        }
        else
        {
            NameLabel.Text = $"Doors ({live.Count})";
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
