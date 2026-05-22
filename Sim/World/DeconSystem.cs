using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick: for every colonist whose BuildTarget points at an open
// Deconstruct job, if they're 4-adjacent advance ProgressSec on the
// job entity's Decon component. When it hits DeconTimeSec the job
// completes — SimRuntime reverts the wall to Floor, drops half-cost
// wood, and rebuilds the map view.
public sealed class DeconSystem
{
    public const float DeconTimeSec = 2.0f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public DeconSystem(SimRuntime sim, JobBoard jobs)
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
            if (job is null) return;
            bool isFloor = job.Kind == JobKind.FloorDeconstruct;
            if (job.Kind != JobKind.Deconstruct && !isFloor && job.Kind != JobKind.DoorDeconstruct) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            bool inRange = isFloor
                ? BuildAdjacency.InRangeOrOnTile(pos.X, pos.Y, job.Tile.X, job.Tile.Y)
                : BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y);
            if (!inRange) return;

            ref var decon = ref job.Entity.GetComponent<Decon>();
            decon.ProgressSec += dt;
            if (decon.ProgressSec >= DeconTimeSec)
            {
                completed.Add(job.Id);
            }
        });

        foreach (var id in completed) _sim.CompleteJob(id);
    }
}
