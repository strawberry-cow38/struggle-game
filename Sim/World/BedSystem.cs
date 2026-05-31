using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build / decon advancer for beds. Worker must be adjacent (Chebyshev 1)
// to either tile of the 2-tile footprint; standing on a footprint tile is
// impossible since the bed (or blueprint) blocks both. See BuildableSystem.
public sealed class BedSystem : BuildableSystem
{
    public const float BedBuildTimeSec = 2.5f;
    public const float BedDeconTimeSec = 1.5f;

    public BedSystem(SimRuntime sim, JobBoard jobs) : base(sim, jobs) { }

    protected override JobKind BuildKind => JobKind.BedBuild;
    protected override JobKind DeconKind => JobKind.BedDeconstruct;
    protected override float BuildSeconds => BedBuildTimeSec;
    protected override float DeconSeconds => BedDeconTimeSec;

    protected override bool BuildReady(Job job, float px, float py)
    {
        var bp = job.Entity.GetComponent<BedBlueprint>();
        return AdjacentToFootprint(bp.Origin, bp.Orientation, px, py);
    }

    protected override bool DeconReady(Job job, float px, float py)
    {
        if (!Sim.BedMap.TryGetValue(job.Tile, out var bedEnt)) return false;
        var bed = bedEnt.GetComponent<Bed>();
        return AdjacentToFootprint(bed.Origin, bed.Orientation, px, py);
    }

    protected override ref float BuildProgress(Entity blueprint)
        => ref blueprint.GetComponent<BedBlueprint>().ProgressSec;

    protected override bool FootprintBlocked(Job buildJob)
    {
        var bp = buildJob.Entity.GetComponent<BedBlueprint>();
        return TileBlocked(bp.Origin) || TileBlocked(BedOrientations.Foot(bp.Origin, bp.Orientation));
    }

    private static bool AdjacentToFootprint(Map.TilePos origin, BedOrientation orientation, float px, float py)
    {
        var foot = BedOrientations.Foot(origin, orientation);
        return BuildAdjacency.InRange(px, py, origin.X, origin.Y)
            || BuildAdjacency.InRange(px, py, foot.X, foot.Y);
    }
}
