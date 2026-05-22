using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick: for every colonist whose BuildTarget points at an open
// FloorBuild job, if they're 4-adjacent advance ProgressSec on the
// FloorBlueprint component. When it hits FloorTimeSec the job
// completes — SimRuntime stamps the flooring layer with Wood, deletes
// the marker entity, and bumps the map view (no walkability change,
// but renderer needs the new version).
//
// No occupancy gate: floors don't block walking, so a pawn standing on
// the tile while another finishes the floor is fine.
public sealed class FloorSystem
{
    public const float FloorTimeSec = 1.0f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public FloorSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        var completed = new List<JobId>();
        var workers = store.Query<WorldPos, BuildTarget, Wanderer>();
        workers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.FloorBuild) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            if (!BuildAdjacency.InRangeOrOnTile(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;

            ref var bp = ref job.Entity.GetComponent<FloorBlueprint>();
            bp.ProgressSec += dt;
            if (bp.ProgressSec >= FloorTimeSec)
            {
                completed.Add(job.Id);
            }
        });

        foreach (var id in completed) _sim.CompleteJob(id);
    }
}
