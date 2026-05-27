using Godot;
using StruggleGame.Game.Tools;

namespace StruggleGame.Game.Designation;

// Per-designator input gating. Each designator listens to motion events
// even when its tool isn't active, which means 14+ virtual _UnhandledInput
// dispatches per accumulated motion event walking down the scene tree.
// At 1000+ render fps that's measurable. Use BindInputToMode to wire a
// designator's input processing to ToolService — when the current mode
// doesn't match, Godot skips the dispatch entirely via
// SetProcessUnhandledInput(false).
internal static class DesignatorInputBinding
{
    public static void BindInputToMode(this Node node, ToolService tools, System.Func<ToolMode, bool> match, System.Action? onDeactivate = null)
    {
        bool active = match(tools.Mode);
        node.SetProcessUnhandledInput(active);
        tools.ModeChanged += m =>
        {
            if (!GodotObject.IsInstanceValid(node)) return;
            bool nowActive = match(m);
            node.SetProcessUnhandledInput(nowActive);
            // Preview state lives in the designator and ticks via input
            // events. When the tool deselects, _UnhandledInput stops
            // firing — so the last hover/drag preview gets stranded on
            // screen. Give designators a hook to clear that state.
            if (!nowActive) onDeactivate?.Invoke();
        };
    }
}
