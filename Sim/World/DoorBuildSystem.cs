using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build advancer for door blueprints. Worker stands 4-adjacent; on
// completion the blueprint gains a Door component. Like walls, a door
// won't finish while a pawn or item sits on the tile (BuildableSystem
// holds + re-opens until clear). Door deconstruct runs in DeconSystem, so
// this is build-only (DeconKind points back at BuildKind).
public sealed class DoorBuildSystem : BuildableSystem
{
    public const float DoorTimeSec = 1.5f;

    public DoorBuildSystem(SimRuntime sim, JobBoard jobs) : base(sim, jobs) { }

    protected override JobKind BuildKind => JobKind.DoorBuild;
    protected override JobKind DeconKind => JobKind.DoorBuild; // build-only
    protected override float BuildSeconds => DoorTimeSec;
    protected override float DeconSeconds => 0f;

    protected override bool BuildReady(Job job, float px, float py)
        => BuildAdjacency.InRange(px, py, job.Tile.X, job.Tile.Y);

    protected override bool DeconReady(Job job, float px, float py) => false;

    protected override ref float BuildProgress(Entity blueprint)
        => ref blueprint.GetComponent<DoorBlueprint>().ProgressSec;

    protected override bool FootprintBlocked(Job buildJob) => TileBlocked(buildJob.Tile);
}
