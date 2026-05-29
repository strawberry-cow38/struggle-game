using Friflo.Engine.ECS;

namespace StruggleGame.Sim.World;

// Health → gameplay multipliers. Reads the cached capacities on a pawn's
// Health component (recomputed each rare tick by HealthSystem).
public static class HealthMods
{
    // Walk-speed multiplier from the Moving capacity. Floored so a hurt
    // (but conscious) colonist can still crawl. 1.0 if no Health.
    public static float MoveSpeed(Entity e)
        => e.HasComponent<Health>() ? System.Math.Max(0.1f, e.GetComponent<Health>().Moving) : 1f;

    // Work-speed multiplier from Manipulation (hands) with a smaller Sight
    // contribution. 1.0 at full health, 1.0 if no Health.
    public static float WorkSpeed(Entity e)
    {
        if (!e.HasComponent<Health>()) return 1f;
        var h = e.GetComponent<Health>();
        return System.Math.Max(0f, h.Manipulation * (0.6f + 0.4f * h.Sight));
    }
}
