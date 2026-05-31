using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Build / decon advancer for stoves. Worker is adjacent to any of the 4
// footprint tiles (3 body + 1 standing). Cook progress is advanced by
// CookSystem, not this one. See BuildableSystem.
public sealed class StoveSystem : BuildableSystem
{
    public const float StoveBuildTimeSec = 4.0f;
    public const float StoveDeconTimeSec = 2.5f;

    public StoveSystem(SimRuntime sim, JobBoard jobs) : base(sim, jobs) { }

    protected override JobKind BuildKind => JobKind.StoveBuild;
    protected override JobKind DeconKind => JobKind.StoveDeconstruct;
    protected override float BuildSeconds => StoveBuildTimeSec;
    protected override float DeconSeconds => StoveDeconTimeSec;

    protected override bool BuildReady(Job job, float px, float py)
    {
        var bp = job.Entity.GetComponent<StoveBlueprint>();
        return AdjacentToFootprint(bp.Origin, bp.Orientation, px, py);
    }

    protected override bool DeconReady(Job job, float px, float py)
    {
        if (!Sim.StoveMap.TryGetValue(job.Tile, out var stoveEnt)) return false;
        var s = stoveEnt.GetComponent<Stove>();
        return AdjacentToFootprint(s.Origin, s.Orientation, px, py);
    }

    protected override ref float BuildProgress(Entity blueprint)
        => ref blueprint.GetComponent<StoveBlueprint>().ProgressSec;

    private static bool AdjacentToFootprint(TilePos origin, StoveOrientation orientation, float px, float py)
    {
        foreach (var t in StoveOrientations.BodyTiles(origin, orientation))
        {
            if (BuildAdjacency.InRange(px, py, t.X, t.Y)) return true;
        }
        var st = StoveOrientations.StandingTile(origin, orientation);
        return BuildAdjacency.InRange(px, py, st.X, st.Y);
    }
}
