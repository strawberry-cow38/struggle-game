using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build / decon advancer for lamps. Mirror of FloorSystem / RoofSystem:
// the worker stands on (or 4-adjacent to) the tile, ProgressSec ticks
// up while present, and SimRuntime.CompleteJob handles the actual entity
// promotion + light recompute.
public sealed class LampSystem
{
    public const float LampBuildTimeSec = 1.5f;
    public const float LampDeconTimeSec = 0.8f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public LampSystem(SimRuntime sim, JobBoard jobs)
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
            if (job.Kind != JobKind.LampBuild && job.Kind != JobKind.LampDeconstruct) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;
            if (!BuildAdjacency.InRangeOrOnTile(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;

            if (job.Kind == JobKind.LampBuild)
            {
                ref var bp = ref job.Entity.GetComponent<LampBlueprint>();
                bp.ProgressSec += dt;
                if (bp.ProgressSec >= LampBuildTimeSec) completed.Add(job.Id);
            }
            else
            {
                ref var decon = ref job.Entity.GetComponent<Decon>();
                decon.ProgressSec += dt;
                if (decon.ProgressSec >= LampDeconTimeSec) completed.Add(job.Id);
            }
        });
        foreach (var id in completed) _sim.CompleteJob(id);
    }
}
