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
//
// Chrome / lifecycle / positioning / change-detect come from
// EntityInfoPanel; this only supplies the item body, render + actions.
public partial class ItemInfoPanel : EntityInfoPanel
{
    private Label _countLabel = null!;
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private HpBar _hp = null!;
    private Button _forbidBtn = null!;

    private bool _selectionForbidden;

    protected override int[] SelectedIds
    {
        get => Host!.SelectedWoodIds;
        set => Host!.SelectedWoodIds = value;
    }

    protected override string Title => "Item";

    protected override void BuildBody(VBoxContainer vbox)
    {
        _countLabel = new Label { Text = "" };
        vbox.AddChild(_countLabel);

        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);
        _hp = new HpBar();
        vbox.AddChild(_hp);

        _forbidBtn = new Button { Text = "Forbid", CustomMinimumSize = new Vector2(0, 28) };
        _forbidBtn.Pressed += OnForbidPressed;
        vbox.AddChild(_forbidBtn);

        var hint = new Label { Text = "Hotkey: F", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        hint.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(hint);
    }

    // One selected dropped stack, normalized across Wood + ItemPile.
    private readonly record struct Stack(int Id, Sim.Map.TilePos Tile, int Count, string Path, bool Forbidden, string? Label);

    private static void CollectSelected(SimSnapshot snap, HashSet<int> idSet, List<Stack> outList)
    {
        foreach (var p in snap.ItemPiles)
            if (idSet.Contains(p.EntityId))
                outList.Add(new Stack(p.EntityId, p.Tile, p.Count, p.ItemPath, p.Forbidden, p.Label));
    }

    protected override void Render(SimSnapshot snap, int[] ids)
    {
        var stacks = new List<Stack>(ids.Length);
        CollectSelected(snap, new HashSet<int>(ids), stacks);
        if (stacks.Count == 0)
        {
            // All stacks vanished (picked up / merged).
            SelectedIds = Array.Empty<int>();
            return;
        }

        int totalCount = 0, forbidden = 0, haulable = 0;
        foreach (var s in stacks)
        {
            totalCount += s.Count;
            if (s.Forbidden) forbidden++; else haulable++;
        }
        _hp.Set(ThingHp.Item, ThingHp.Item);

        if (stacks.Count == 1)
        {
            var s = stacks[0];
            string name = s.Label ?? (ItemCatalog.ItemsByPath.TryGetValue(s.Path, out var def)
                ? def.DisplayName : s.Path);
            NameLabel.Text = name;
            _countLabel.Text = $"Count: {s.Count}";
            _tileLabel.Text = $"Tile: ({s.Tile.X}, {s.Tile.Y})";
            _stateLabel.Text = s.Forbidden ? "Forbidden" : "Haulable";
            _selectionForbidden = s.Forbidden;
            _forbidBtn.Text = s.Forbidden ? "Unforbid" : "Forbid";
        }
        else
        {
            NameLabel.Text = $"Items ({stacks.Count})";
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
