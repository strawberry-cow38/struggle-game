using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected bed. Shows origin tile + orientation, an
// assign-to-colonist dropdown, and a Deconstruct button. Multi-select
// applies decon/assign across every selected bed. See TileInfoPanel.
public partial class BedInfoPanel : TileInfoPanel
{
    private Label _tileLabel = null!;
    private Label _orientLabel = null!;
    private HpBar _hp = null!;
    private Label _assignLabel = null!;
    private OptionButton _assignBtn = null!;
    private Button _deconBtn = null!;

    // PawnEntityId at each OptionButton index. Index 0 is always 0 = Unassigned.
    private readonly List<int> _assignIds = new();
    private bool _suppressAssign;
    // Signature of the assign dropdown's contents last built. Clearing +
    // refilling the OptionButton every tick would close it under the
    // player mid-selection; rebuild only when this changes.
    private string _lastAssignSig = "";

    protected override TilePos[] SelectedTiles
    {
        get => Host!.SelectedBedTiles;
        set => Host!.SelectedBedTiles = value;
    }
    protected override string Title => "Bed";
    protected override int MinHeight => 140;

    protected override void BuildBody(VBoxContainer vbox)
    {
        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);
        _orientLabel = new Label { Text = "" };
        vbox.AddChild(_orientLabel);
        _hp = new HpBar();
        vbox.AddChild(_hp);

        _assignLabel = new Label { Text = "Assigned to" };
        vbox.AddChild(_assignLabel);
        _assignBtn = new OptionButton { CustomMinimumSize = new Vector2(0, 28) };
        _assignBtn.ItemSelected += OnAssignSelected;
        vbox.AddChild(_assignBtn);

        _deconBtn = new Button { Text = "Deconstruct", CustomMinimumSize = new Vector2(0, 28) };
        _deconBtn.Pressed += OnDeconPressed;
        vbox.AddChild(_deconBtn);
    }

    protected override void Render(SimSnapshot snap, TilePos[] tiles)
    {
        var live = new List<BedState>(tiles.Length);
        var liveTiles = new List<TilePos>(tiles.Length);
        foreach (var t in tiles)
        {
            foreach (var b in snap.Beds)
            {
                if (b.Origin == t) { live.Add(b); liveTiles.Add(t); break; }
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
        _hp.Set(ThingHp.Bed, ThingHp.Bed);
        if (live.Count == 1)
        {
            NameLabel.Text = "Bed";
            _tileLabel.Text = $"Tile: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _orientLabel.Text = $"Facing: {live[0].Orientation}";
        }
        else
        {
            NameLabel.Text = $"Beds ({live.Count})";
            _tileLabel.Text = $"First: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _orientLabel.Text = "";
        }

        RebuildAssignDropdown(snap, live);
    }

    private void RebuildAssignDropdown(SimSnapshot snap, List<BedState> live)
    {
        // Only rebuild when the roster or the selected assignee changes —
        // keeps the dropdown stable/openable between ticks.
        var sb = new System.Text.StringBuilder();
        int firstAssignee0 = live[0].AssignedPawnEntityId;
        bool allSame0 = true;
        for (int i = 1; i < live.Count; i++)
            if (live[i].AssignedPawnEntityId != firstAssignee0) { allSame0 = false; break; }
        sb.Append(allSame0 ? firstAssignee0 : -1).Append('|');
        foreach (var p in snap.PawnWork) sb.Append(p.EntityId).Append(':').Append(p.Name).Append(';');
        string sig = sb.ToString();
        if (sig == _lastAssignSig) return;
        _lastAssignSig = sig;

        _suppressAssign = true;
        _assignBtn.Clear();
        _assignIds.Clear();

        _assignBtn.AddItem("Unassigned", 0);
        _assignIds.Add(0);

        var pawns = snap.PawnWork;
        for (int i = 0; i < pawns.Length; i++)
        {
            var p = pawns[i];
            _assignBtn.AddItem(p.Name, i + 1);
            _assignIds.Add(p.EntityId);
        }

        int firstAssignee = live[0].AssignedPawnEntityId;
        bool allSame = true;
        for (int i = 1; i < live.Count; i++)
        {
            if (live[i].AssignedPawnEntityId != firstAssignee) { allSame = false; break; }
        }

        if (allSame)
        {
            int idx = _assignIds.IndexOf(firstAssignee);
            _assignBtn.Selected = idx >= 0 ? idx : 0;
        }
        else
        {
            _assignBtn.Selected = -1;
        }
        _suppressAssign = false;
    }

    private void OnAssignSelected(long index)
    {
        if (_suppressAssign || Host is null) return;
        if (index < 0 || index >= _assignIds.Count) return;
        int pawnId = _assignIds[(int)index];
        foreach (var t in Host.SelectedBedTiles)
            Host.QueueCommand(new AssignBedToColonistCommand(t, pawnId));
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedBedTiles)
            Host.QueueCommand(new PostBedDeconCommand(t));
    }
}
