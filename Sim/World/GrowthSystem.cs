using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick plant growth advancer. Any entity with Growth + a tile-bearing
// component (Tree today, Crop later) ticks Stage toward 1.0 at a rate of
// 1.0 / SecondsToFullGrow per sim-second. Gated by light (>= GrowLightThreshold,
// today equivalent to "unroofed") and temperature (18..25°C).
//
// Sim-seconds scale with the speed multiplier automatically — dt is
// always 1/TickHz but ticks per real-second scales with TickHz, so a 4x
// sim accumulates 4× the growth in the same wall-clock time.
public sealed class GrowthSystem
{
    // 1 in-game day = 24 real minutes @ 1x speed = 1440 sim-seconds.
    public const float SecondsToFullGrow = 24f * 60f;
    // Minimum light fraction (0..1) for plants to grow. Half-lit or
    // brighter passes; anything under a roof (light = 0 with the current
    // stub) fails.
    public const float GrowLightThreshold = 0.51f;

    // RimWorld-style "rare tick": plants don't need 60 Hz resolution. Each
    // plant advances once every GrowTickInterval ticks, by that many ticks'
    // worth of growth — so the average rate is identical to ticking every
    // frame, but the work happens ~250× less often. The (tick + entityId)
    // phase staggers plants across buckets so they don't all recompute on
    // the same frame (no spike).
    public const int GrowTickInterval = 250;

    private readonly SimRuntime _sim;

    public GrowthSystem(SimRuntime sim)
    {
        _sim = sim;
    }

    public void Step(EntityStore store, float dt)
    {
        long tick = _sim.Tick;
        // dt accrued over the whole interval since this plant last ticked.
        float step = dt * GrowTickInterval / SecondsToFullGrow;
        store.Query<Growth, Tree>().ForEachEntity((ref Growth g, ref Tree t, Entity e) =>
        {
            if (g.Stage >= 1f) return;
            if ((tick + e.Id) % GrowTickInterval != 0) return;
            if (_sim.LightAt(t.Tile) < GrowLightThreshold) return;
            if (!_sim.IsTileGrowTemperature(t.Tile)) return;
            g.Stage = Math.Min(1f, g.Stage + step);
        });
        store.Query<Growth, Crop>().ForEachEntity((ref Growth g, ref Crop c, Entity e) =>
        {
            if (g.Stage >= 1f) return;
            if ((tick + e.Id) % GrowTickInterval != 0) return;
            if (_sim.LightAt(c.Tile) < GrowLightThreshold) return;
            if (!_sim.IsTileGrowTemperature(c.Tile)) return;
            g.Stage = Math.Min(1f, g.Stage + step);
        });
    }
}
