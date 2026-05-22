using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick: for every colonist whose BuildTarget points at an open
// DoorBuild job, if they're 4-adjacent advance ProgressSec on the
// DoorBlueprint. When it hits DoorTimeSec the job completes — the
// blueprint entity gains a Door component (closed) and the marker is
// dropped. Doors don't change walkability, so no map rebuild is needed.
public sealed class DoorBuildSystem
{
    public const float DoorTimeSec = 1.5f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public DoorBuildSystem(SimRuntime sim, JobBoard jobs)
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
            if (job is null || job.Kind != JobKind.DoorBuild) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            int dx = Math.Abs((int)pos.X - job.Tile.X);
            int dy = Math.Abs((int)pos.Y - job.Tile.Y);
            // Chebyshev-1: must stand on one of the 8 neighbors.
            if (dx > 1 || dy > 1 || (dx == 0 && dy == 0)) return;

            ref var bp = ref job.Entity.GetComponent<DoorBlueprint>();
            bp.ProgressSec += dt;
            if (bp.ProgressSec >= DoorTimeSec)
            {
                completed.Add(job.Id);
            }
        });

        foreach (var id in completed) _sim.CompleteJob(id);
    }
}
