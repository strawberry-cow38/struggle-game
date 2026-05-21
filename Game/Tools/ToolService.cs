namespace StruggleGame.Game.Tools;

// Shared current-tool state. Toolbar writes Mode; designators read it
// each input event to decide whether to act. Plain POCO — the Bootstrap
// node owns a single instance and hands references to subscribers.
public sealed class ToolService
{
    public event Action<ToolMode>? ModeChanged;

    private ToolMode _mode = ToolMode.None;
    public ToolMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ModeChanged?.Invoke(_mode);
        }
    }
}
