using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build / decon advancer for sandbags. 1x1 footprint — worker stands on
// any 4-adjacent walkable tile (Chebyshev 1). See BuildableSystem.
public sealed class SandbagSystem : BuildableSystem
{
    public const float BuildTimeSec = 2.0f;
    public const float DeconTimeSec = 1.0f;

    public SandbagSystem(SimRuntime sim, JobBoard jobs) : base(sim, jobs) { }

    protected override JobKind BuildKind => JobKind.SandbagBuild;
    protected override JobKind DeconKind => JobKind.SandbagDeconstruct;
    protected override float BuildSeconds => BuildTimeSec;
    protected override float DeconSeconds => DeconTimeSec;

    protected override bool BuildReady(Job job, float px, float py)
    {
        var t = job.Entity.GetComponent<SandbagBlueprint>().Tile;
        return BuildAdjacency.InRange(px, py, t.X, t.Y);
    }

    protected override bool DeconReady(Job job, float px, float py)
        => Sim.SandbagMap.ContainsKey(job.Tile)
        && BuildAdjacency.InRange(px, py, job.Tile.X, job.Tile.Y);

    protected override ref float BuildProgress(Entity blueprint)
        => ref blueprint.GetComponent<SandbagBlueprint>().ProgressSec;
}
