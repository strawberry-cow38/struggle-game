using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build / decon advancer for lamps. Worker stands on (or 4-adjacent to)
// the tile; lamps are free (no funding gate). See BuildableSystem.
public sealed class LampSystem : BuildableSystem
{
    public const float LampBuildTimeSec = 1.5f;
    public const float LampDeconTimeSec = 0.8f;

    public LampSystem(SimRuntime sim, JobBoard jobs) : base(sim, jobs) { }

    protected override JobKind BuildKind => JobKind.LampBuild;
    protected override JobKind DeconKind => JobKind.LampDeconstruct;
    protected override float BuildSeconds => LampBuildTimeSec;
    protected override float DeconSeconds => LampDeconTimeSec;
    protected override bool RequiresFunding => false;

    protected override bool BuildReady(Job job, float px, float py)
        => BuildAdjacency.InRangeOrOnTile(px, py, job.Tile.X, job.Tile.Y);

    protected override bool DeconReady(Job job, float px, float py)
        => BuildAdjacency.InRangeOrOnTile(px, py, job.Tile.X, job.Tile.Y);

    protected override ref float BuildProgress(Entity blueprint)
        => ref blueprint.GetComponent<LampBlueprint>().ProgressSec;

    protected override bool FootprintBlocked(Job buildJob) => TileBlocked(buildJob.Tile);
}
