using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected grow zone. Name field, crop dropdown,
// Allow Cutting + Allow Sowing toggles, Expand / Shrink / Delete.
// Mirrors StockpilePanel but the per-zone state is much smaller so the
// layout collapses to a single column.
public partial class GrowZonePanel : CanvasLayer
{
    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private const int PanelWidth = 320;
    private const int MarginRight = 16;
    private const int MarginTop = 16;
    private const int MarginBottom = 96;

    private Panel _root = null!;
    private LineEdit _nameEdit = null!;
    private OptionButton _cropOpt = null!;
    private CheckBox _allowCuttingChk = null!;
    private CheckBox _allowSowingChk = null!;
    private Button _expandBtn = null!;
    private Button _shrinkBtn = null!;
    private Button _deleteBtn = null!;
    private Label _summaryLabel = null!;

    private int _shownZoneId = -1;
    private long _lastSnapshotTick = -1;
    private bool _suppressEvents;

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 320),
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
        var header = new Label { Text = "Grow Zone", CustomMinimumSize = new Vector2(0, 24) };
        header.AddThemeFontSizeOverride("font_size", 18);
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(header);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => Host!.SelectedGrowZoneId = null;
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        vbox.AddChild(new Label { Text = "Name" });
        _nameEdit = new LineEdit { CustomMinimumSize = new Vector2(0, 28) };
        _nameEdit.TextSubmitted += OnNameSubmitted;
        _nameEdit.FocusExited += () => OnNameSubmitted(_nameEdit.Text);
        vbox.AddChild(_nameEdit);

        vbox.AddChild(new Label { Text = "Crop" });
        _cropOpt = new OptionButton { CustomMinimumSize = new Vector2(0, 28) };
        // Only one crop right now — dropdown still in place so adding a
        // second is a one-line change here + a CropKind enum entry.
        _cropOpt.AddItem("Carrot", (int)CropKind.Carrot);
        _cropOpt.ItemSelected += OnCropChanged;
        vbox.AddChild(_cropOpt);

        _allowCuttingChk = new CheckBox { Text = "Allow Cutting" };
        _allowCuttingChk.Toggled += OnAllowCuttingToggled;
        vbox.AddChild(_allowCuttingChk);

        _allowSowingChk = new CheckBox { Text = "Allow Sowing" };
        _allowSowingChk.Toggled += OnAllowSowingToggled;
        vbox.AddChild(_allowSowingChk);

        var btnRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btnRow.AddThemeConstantOverride("separation", 6);
        _expandBtn = new Button { Text = "Expand", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _expandBtn.Pressed += () => { if (Tools is not null) Tools.Mode = ToolMode.GrowZoneExpand; };
        btnRow.AddChild(_expandBtn);
        _shrinkBtn = new Button { Text = "Shrink", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _shrinkBtn.Pressed += () => { if (Tools is not null) Tools.Mode = ToolMode.GrowZoneShrink; };
        btnRow.AddChild(_shrinkBtn);
        _deleteBtn = new Button { Text = "Delete", CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _deleteBtn.Pressed += OnDeletePressed;
        btnRow.AddChild(_deleteBtn);
        vbox.AddChild(btnRow);

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
        GetTree().Root.SizeChanged -= Reposition;
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        int? sel = Host.SelectedGrowZoneId;
        var snap = Host.LatestSnapshot;
        if (sel is null || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownZoneId = -1; }
            return;
        }
        var zone = FindZone(snap, sel.Value);
        if (zone is null)
        {
            Host.SelectedGrowZoneId = null;
            _root.Visible = false;
            _shownZoneId = -1;
            return;
        }
        if (!_root.Visible) _root.Visible = true;

        bool zoneChanged = zone.Value.Id != _shownZoneId;
        bool tickChanged = snap.Tick != _lastSnapshotTick;
        if (zoneChanged || tickChanged)
        {
            Render(zone.Value, snapshotChanged: zoneChanged);
            _shownZoneId = zone.Value.Id;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        float height = Math.Max(280f, vp.Y - MarginTop - MarginBottom);
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, height);
    }

    private static GrowZoneState? FindZone(SimSnapshot snap, int id)
    {
        foreach (var z in snap.GrowZones)
        {
            if (z.Id == id) return z;
        }
        return null;
    }

    private void Render(GrowZoneState z, bool snapshotChanged)
    {
        _suppressEvents = true;
        if (snapshotChanged || !_nameEdit.HasFocus()) _nameEdit.Text = z.Name;
        if (snapshotChanged) _cropOpt.Selected = (int)z.CropKind;
        _allowCuttingChk.ButtonPressed = z.AllowCutting;
        _allowSowingChk.ButtonPressed = z.AllowSowing;
        _summaryLabel.Text = $"{z.Tiles.Length} tile(s) · always-harvest matching crops at 100%";
        _suppressEvents = false;
    }

    private void OnNameSubmitted(string text)
    {
        if (_suppressEvents || Host is null) return;
        int? sel = Host.SelectedGrowZoneId;
        if (sel is null) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        Host.QueueCommand(new RenameGrowZoneCommand(sel.Value, text.Trim()));
    }

    private void OnCropChanged(long index)
    {
        if (_suppressEvents || Host is null) return;
        int? sel = Host.SelectedGrowZoneId;
        if (sel is null) return;
        var kind = (CropKind)_cropOpt.GetItemId((int)index);
        Host.QueueCommand(new SetGrowZoneCropKindCommand(sel.Value, kind));
    }

    private void OnAllowCuttingToggled(bool pressed)
    {
        if (_suppressEvents || Host is null) return;
        int? sel = Host.SelectedGrowZoneId;
        if (sel is null) return;
        Host.QueueCommand(new SetGrowZoneAllowCuttingCommand(sel.Value, pressed));
    }

    private void OnAllowSowingToggled(bool pressed)
    {
        if (_suppressEvents || Host is null) return;
        int? sel = Host.SelectedGrowZoneId;
        if (sel is null) return;
        Host.QueueCommand(new SetGrowZoneAllowSowingCommand(sel.Value, pressed));
    }

    private void OnDeletePressed()
    {
        if (Host is null) return;
        int? sel = Host.SelectedGrowZoneId;
        if (sel is null) return;
        Host.QueueCommand(new DeleteGrowZoneCommand(sel.Value));
        Host.SelectedGrowZoneId = null;
    }
}
