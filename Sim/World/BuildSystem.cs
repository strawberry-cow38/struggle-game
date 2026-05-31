using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build advancer for wall blueprints. Worker stands 4-adjacent; the wall
// can't complete while a pawn or item occupies the tile (it would get
// buried) — BuildableSystem holds progress one tick under and re-opens the
// job until the tile clears. Wall deconstruct runs in DeconSystem, so this
// is build-only (DeconKind points back at BuildKind).
public sealed class BuildSystem : BuildableSystem
{
    public const float BuildTimeSec = 1.5f;

    public BuildSystem(SimRuntime sim, JobBoard jobs) : base(sim, jobs) { }

    protected override JobKind BuildKind => JobKind.WallBuild;
    protected override JobKind DeconKind => JobKind.WallBuild; // build-only
    protected override float BuildSeconds => BuildTimeSec;
    protected override float DeconSeconds => 0f;

    protected override bool BuildReady(Job job, float px, float py)
        => BuildAdjacency.InRange(px, py, job.Tile.X, job.Tile.Y);

    protected override bool DeconReady(Job job, float px, float py) => false;

    protected override ref float BuildProgress(Entity blueprint)
        => ref blueprint.GetComponent<Blueprint>().ProgressSec;

    protected override bool FootprintBlocked(Job buildJob) => TileBlocked(buildJob.Tile);
}
