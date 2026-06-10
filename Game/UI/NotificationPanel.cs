using System.Collections.Generic;
using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// RimWorld-style "letters" notification stack. Events raise letters that ride
// the sim snapshot; this panel mirrors them as a vertical stack pinned to the
// right-middle of the screen (newest on top):
//   - hover a letter  → styled tooltip with the expanded summary
//   - left-click       → large detail pane with the full text
//   - right-click      → dismiss (clears it from the sim)
// Non-modal: letters never pause the sim. The detail pane dims the screen and
// eats clicks while open, but the sim keeps running behind it.
public partial class NotificationPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int LetterWidth = 190;
    private const int LetterHeight = 34;
    private const int StackGap = 6;
    private const int RightMargin = 16;
    private const float TooltipWidth = 270f;
    private const string AudioDir = "res://Game/Assets/Audio/";

    private Control _root = null!;
    private VBoxContainer _stack = null!;

    // Chime played when a new letter arrives (dc5_4). Pre-existing letters
    // present on the first snapshot don't play it (no spam on load).
    private AudioStreamPlayer _spawnSfx = null!;
    private bool _seeded;

    // Hover tooltip (single reused control).
    private Panel _tooltip = null!;
    private Label _tipTitle = null!;
    private Label _tipBody = null!;

    // Detail pane.
    private Control _detailRoot = null!;
    private Label _detailTitle = null!;
    private Label _detailBody = null!;

    // Live rows keyed by notification id + the data backing each.
    private readonly Dictionary<int, Button> _rows = new();
    private readonly Dictionary<int, GameNotificationState> _data = new();
    private readonly HashSet<int> _present = new();
    private readonly List<int> _toRemove = new();

    public override void _Ready()
    {
        Layer = 160; // above the HUD, below the escape menu (200)

        _root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Ignore, // empty space falls through to the game
        };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // The letter stack: pinned to the right edge, vertically centered,
        // growing downward from the middle. Newest letter is moved to the top.
        _stack = new VBoxContainer
        {
            Name = "LetterStack",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 1f, AnchorRight = 1f,
            AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Both,
            OffsetLeft = -(RightMargin + LetterWidth),
            OffsetRight = -RightMargin,
        };
        _stack.AddThemeConstantOverride("separation", StackGap);
        _root.AddChild(_stack);

        BuildTooltip();
        BuildDetailPane();

        _spawnSfx = new AudioStreamPlayer { Name = "LetterSfx", Bus = "Master" };
        AddChild(_spawnSfx);
        var s = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath(AudioDir + "Letter.ogg"));
        if (s is not null) _spawnSfx.Stream = s;
    }

    private void BuildTooltip()
    {
        _tooltip = new Panel
        {
            Name = "LetterTooltip",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(TooltipWidth, 0),
        };
        _tooltip.AddThemeStyleboxOverride("panel", UiTheme.PanelBox(corner: 8, margin: 10));
        _root.AddChild(_tooltip);

        var vbox = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 10, OffsetTop = 8, OffsetRight = -10, OffsetBottom = -8,
        };
        vbox.AddThemeConstantOverride("separation", 4);
        _tooltip.AddChild(vbox);

        _tipTitle = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _tipTitle.AddThemeFontSizeOverride("font_size", 16);
        _tipTitle.AddThemeColorOverride("font_color", UiTheme.Text);
        vbox.AddChild(_tipTitle);

        _tipBody = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(TooltipWidth - 20, 0),
        };
        _tipBody.AddThemeFontSizeOverride("font_size", 13);
        _tipBody.AddThemeColorOverride("font_color", UiTheme.TextDim);
        vbox.AddChild(_tipBody);
    }

    private void BuildDetailPane()
    {
        _detailRoot = new Control { Name = "DetailRoot", Visible = false };
        _detailRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_detailRoot);

        // Dim backdrop — eats clicks; clicking it closes the pane.
        var dim = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.5f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        dim.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed) CloseDetail();
        };
        _detailRoot.AddChild(dim);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _detailRoot.AddChild(center);

        var panel = new Panel
        {
            CustomMinimumSize = new Vector2(560, 360),
            MouseFilter = Control.MouseFilterEnum.Stop, // don't let clicks inside fall through to the dim
        };
        panel.AddThemeStyleboxOverride("panel", UiTheme.PanelBox(corner: 12, margin: 18));
        center.AddChild(panel);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 20, OffsetTop = 18, OffsetRight = -20, OffsetBottom = -18,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        header.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(header);

        _detailTitle = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _detailTitle.AddThemeFontSizeOverride("font_size", 26);
        header.AddChild(_detailTitle);

        var close = UiTheme.CloseButton();
        close.Pressed += CloseDetail;
        header.AddChild(close);

        vbox.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(scroll);

        _detailBody = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(520, 0),
        };
        _detailBody.AddThemeFontSizeOverride("font_size", 16);
        _detailBody.AddThemeColorOverride("font_color", UiTheme.Text);
        scroll.AddChild(_detailBody);
    }

    public override void _Process(double delta)
    {
        var snap = Host?.LatestSnapshot;
        if (snap is null) return;

        _present.Clear();
        foreach (var note in snap.Notifications)
        {
            _present.Add(note.Id);
            _data[note.Id] = note;
            if (!_rows.ContainsKey(note.Id)) AddRow(note, animate: _seeded);
        }
        _seeded = true;

        // Drop rows for letters the sim has cleared (dismissed elsewhere, etc).
        _toRemove.Clear();
        foreach (var id in _rows.Keys)
            if (!_present.Contains(id)) _toRemove.Add(id);
        foreach (var id in _toRemove) RemoveRow(id);

        // Letters added this/last frame need their resting position captured
        // once the VBox has laid them out, then slide in from the right edge.
        if (_pendingSlide.Count > 0) StartPendingSlides();

        // Keep the tooltip glued to its letter once its size resolves.
        if (_hoveredId != 0 && _rows.TryGetValue(_hoveredId, out var hov))
            PositionTooltip(hov);
    }

    // Buttons awaiting a resting-position capture (one frame after add) before
    // their slide-in tween can start.
    private readonly List<Button> _pendingSlide = new();

    private void AddRow(GameNotificationState note, bool animate)
    {
        var color = KindColor(note.Kind);
        var btn = new Button
        {
            Text = note.Title,
            ClipText = true,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(LetterWidth, LetterHeight),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        btn.AddThemeStyleboxOverride("normal", UiTheme.Box(UiTheme.Button, color, 2, 6, 8, glow: false));
        btn.AddThemeStyleboxOverride("hover", UiTheme.Box(UiTheme.ButtonHover, color, 2, 6, 8, glow: false));
        btn.AddThemeStyleboxOverride("pressed", UiTheme.Box(UiTheme.ButtonHover, color, 2, 6, 8, glow: false));
        btn.AddThemeColorOverride("font_color", color);
        btn.AddThemeColorOverride("font_hover_color", color);
        btn.AddThemeFontSizeOverride("font_size", 15);

        int id = note.Id;
        btn.MouseEntered += () => ShowTooltip(id, btn);
        btn.MouseExited += HideTooltip;
        btn.GuiInput += e => OnLetterInput(e, id, btn);

        _stack.AddChild(btn);
        _stack.MoveChild(btn, 0); // newest on top
        _rows[id] = btn;

        if (animate)
        {
            _spawnSfx.Play();
            btn.Modulate = new Color(1, 1, 1, 0); // hide until the slide kicks in next frame
            _pendingSlide.Add(btn);
        }
    }

    // Once the VBox has placed the freshly added letters, read each resting
    // position, shove it off the right edge, and tween it back in.
    private void StartPendingSlides()
    {
        const float slideDist = LetterWidth + RightMargin + 12f;
        foreach (var btn in _pendingSlide)
        {
            if (!IsInstanceValid(btn)) continue;
            var rest = btn.Position;
            btn.Position = rest + new Vector2(slideDist, 0);
            btn.Modulate = Colors.White;
            var tw = CreateTween().SetParallel();
            tw.TweenProperty(btn, "position", rest, 0.26f)
              .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(btn, "modulate:a", 1f, 0.18f).From(0f);
        }
        _pendingSlide.Clear();
    }

    private void RemoveRow(int id)
    {
        if (_rows.TryGetValue(id, out var btn))
        {
            btn.QueueFree();
            _rows.Remove(id);
        }
        _data.Remove(id);
        if (_hoveredId == id) HideTooltip();
    }

    private void OnLetterInput(InputEvent e, int id, Button btn)
    {
        if (e is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            OpenDetail(id);
            btn.AcceptEvent();
        }
        else if (mb.ButtonIndex == MouseButton.Right)
        {
            Dismiss(id);
            btn.AcceptEvent();
        }
    }

    private int _hoveredId;

    private void ShowTooltip(int id, Button anchor)
    {
        if (!_data.TryGetValue(id, out var note)) return;
        _hoveredId = id;
        _tipTitle.Text = note.Title;
        _tipTitle.AddThemeColorOverride("font_color", KindColor(note.Kind));
        _tipBody.Text = note.Message;
        _tooltip.Visible = true;
        // Position to the LEFT of the stack, vertically aligned to the letter,
        // clamped into the viewport. Size settles next frame; reposition runs
        // in _Process while hovered to catch the resolved height.
        PositionTooltip(anchor);
    }

    private void HideTooltip()
    {
        _hoveredId = 0;
        _tooltip.Visible = false;
    }

    private void PositionTooltip(Button anchor)
    {
        var rect = anchor.GetGlobalRect();
        var vp = GetViewport().GetVisibleRect().Size;
        float h = Mathf.Max(_tooltip.Size.Y, 40f);
        float x = rect.Position.X - TooltipWidth - 8f;
        float y = Mathf.Clamp(rect.Position.Y, 8f, vp.Y - h - 8f);
        _tooltip.Position = new Vector2(Mathf.Max(8f, x), y);
    }

    private void OpenDetail(int id)
    {
        if (!_data.TryGetValue(id, out var note)) return;
        HideTooltip();
        _detailTitle.Text = note.Title;
        _detailTitle.AddThemeColorOverride("font_color", KindColor(note.Kind));
        _detailBody.Text = note.Detail;
        _detailRoot.Visible = true;
    }

    private void CloseDetail() => _detailRoot.Visible = false;

    private void Dismiss(int id)
    {
        Host?.QueueCommand(new DismissNotificationCommand(id));
        // Optimistically pull the row now so it disappears on click; the next
        // snapshot won't carry it back since the sim clears it.
        RemoveRow(id);
    }

    private static Color KindColor(LetterKind kind) => kind switch
    {
        LetterKind.Threat => new Color(1f, 0.45f, 0.38f),    // red-orange
        LetterKind.Negative => new Color(0.98f, 0.74f, 0.42f), // amber
        LetterKind.Positive => new Color(0.55f, 0.92f, 0.62f), // green
        _ => UiTheme.Accent,                                   // cyan
    };

    public override void _Input(InputEvent @event)
    {
        if (!_detailRoot.Visible) return;
        if (@event is InputEventKey ek && ek.Pressed && !ek.Echo && ek.Keycode == Key.Escape)
        {
            CloseDetail();
            GetViewport().SetInputAsHandled();
        }
    }
}
