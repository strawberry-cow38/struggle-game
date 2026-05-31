using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected stove. Shows origin tile, orientation,
// cook progress, and a Deconstruct + Bills button (opens BillsPanel).
// Multi-select applies decon across every selected stove. See TileInfoPanel.
public partial class StoveInfoPanel : TileInfoPanel
{
    public BillsPanel? Bills { get; set; }

    private Label _tileLabel = null!;
    private Label _orientLabel = null!;
    private Label _progressLabel = null!;
    private Label _billsLabel = null!;
    private Button _billsBtn = null!;
    private Button _deconBtn = null!;

    protected override TilePos[] SelectedTiles
    {
        get => Host!.SelectedStoveTiles;
        set => Host!.SelectedStoveTiles = value;
    }
    protected override string Title => "Stove";
    protected override int MinHeight => 180;

    protected override void BuildBody(VBoxContainer vbox)
    {
        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);
        _orientLabel = new Label { Text = "" };
        vbox.AddChild(_orientLabel);
        _progressLabel = new Label { Text = "" };
        vbox.AddChild(_progressLabel);
        _billsLabel = new Label { Text = "" };
        vbox.AddChild(_billsLabel);

        _billsBtn = new Button { Text = "Bills...", CustomMinimumSize = new Vector2(0, 28) };
        _billsBtn.Pressed += OnBillsPressed;
        vbox.AddChild(_billsBtn);

        _deconBtn = new Button { Text = "Deconstruct", CustomMinimumSize = new Vector2(0, 28) };
        _deconBtn.Pressed += OnDeconPressed;
        vbox.AddChild(_deconBtn);
    }

    protected override void Render(SimSnapshot snap, TilePos[] tiles)
    {
        var live = new List<StoveState>(tiles.Length);
        var liveTiles = new List<TilePos>(tiles.Length);
        foreach (var t in tiles)
        {
            foreach (var s in snap.Stoves)
            {
                if (s.Origin == t) { live.Add(s); liveTiles.Add(t); break; }
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
        if (live.Count == 1)
        {
            var s = live[0];
            NameLabel.Text = "Stove";
            _tileLabel.Text = $"Tile: ({s.Origin.X}, {s.Origin.Y})";
            _orientLabel.Text = $"Facing: {s.Orientation}";
            if (s.CurrentBillIndex >= 0)
            {
                _progressLabel.Text = $"Cooking: {Mathf.RoundToInt(s.CookProgress * 100f)}%";
            }
            else
            {
                _progressLabel.Text = "Idle";
            }
            _billsLabel.Text = $"Bills: {s.Bills.Length}";
            _billsBtn.Disabled = false;
        }
        else
        {
            NameLabel.Text = $"Stoves ({live.Count})";
            _tileLabel.Text = $"First: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _orientLabel.Text = "";
            _progressLabel.Text = "";
            _billsLabel.Text = "";
            // Bills UI only makes sense for a single stove.
            _billsBtn.Disabled = true;
        }
    }

    private void OnBillsPressed()
    {
        if (Host is null || Bills is null) return;
        var tiles = Host.SelectedStoveTiles;
        if (tiles.Length != 1) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        foreach (var s in snap.Stoves)
        {
            if (s.Origin == tiles[0]) { Bills.Open(s.EntityId); return; }
        }
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedStoveTiles)
            Host.QueueCommand(new DeconstructStoveCommand(t));
    }
}
