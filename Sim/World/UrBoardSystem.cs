using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build / decon advancer for Ur boards. 1x1 footprint — worker stands on
// any 4-adjacent walkable tile (Chebyshev 1). See BuildableSystem.
public sealed class UrBoardSystem : BuildableSystem
{
    public const float BuildTimeSec = 2.5f;
    public const float DeconTimeSec = 1.5f;

    public UrBoardSystem(SimRuntime sim, JobBoard jobs) : base(sim, jobs) { }

    protected override JobKind BuildKind => JobKind.UrBoardBuild;
    protected override JobKind DeconKind => JobKind.UrBoardDeconstruct;
    protected override float BuildSeconds => BuildTimeSec;
    protected override float DeconSeconds => DeconTimeSec;

    protected override bool BuildReady(Job job, float px, float py)
    {
        var t = job.Entity.GetComponent<UrBoardBlueprint>().Tile;
        return BuildAdjacency.InRange(px, py, t.X, t.Y);
    }

    protected override bool DeconReady(Job job, float px, float py)
        => Sim.UrBoardMap.ContainsKey(job.Tile)
        && BuildAdjacency.InRange(px, py, job.Tile.X, job.Tile.Y);

    protected override ref float BuildProgress(Entity blueprint)
        => ref blueprint.GetComponent<UrBoardBlueprint>().ProgressSec;
}
