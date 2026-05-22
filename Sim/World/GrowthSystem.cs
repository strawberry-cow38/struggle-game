using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick plant growth advancer. Any entity with Growth + a tile-bearing
// component (Tree today, Crop later) ticks Stage toward 1.0 at a rate of
// 1.0 / SecondsToFullGrow per sim-second. Gated by light (outdoor tile)
// and temperature (stub: always comfortable since the whole map is 21°C).
//
// Sim-seconds scale with the speed multiplier automatically — dt is
// always 1/TickHz but ticks per real-second scales with TickHz, so a 4x
// sim accumulates 4× the growth in the same wall-clock time.
public sealed class GrowthSystem
{
    // 1 in-game day = 24 real minutes @ 1x speed = 1440 sim-seconds.
    public const float SecondsToFullGrow = 24f * 60f;

    private readonly SimRuntime _sim;

    public GrowthSystem(SimRuntime sim)
    {
        _sim = sim;
    }

    public void Step(EntityStore store, float dt)
    {
        float perSec = 1f / SecondsToFullGrow;
        store.Query<Growth, Tree>().ForEachEntity((ref Growth g, ref Tree t, Entity _) =>
        {
            if (g.Stage >= 1f) return;
            if (!_sim.IsTileOutdoor(t.Tile)) return;
            if (!_sim.IsTileGrowTemperature(t.Tile)) return;
            g.Stage = Math.Min(1f, g.Stage + dt * perSec);
        });
    }
}
