using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Per-tick progress for Sow jobs. The job entity holds a SowSite
// component; ProgressSec ticks while a sower is adjacent. On completion
// SimRuntime.CompleteJob spawns a Crop with Growth.Stage = 0 of the
// site's CropKind on the same tile.
public sealed class SowSystem
{
    public const float SowTimeSec = 2.0f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public SowSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        var completed = new List<JobId>();
        var sowers = store.Query<WorldPos, BuildTarget, Wanderer>();
        sowers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.Sow) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;
            if (!BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;
            if (!job.Entity.HasComponent<SowSite>()) return;

            ref var s = ref job.Entity.GetComponent<SowSite>();
            s.ProgressSec += dt;
            if (s.ProgressSec >= SowTimeSec) completed.Add(job.Id);
        });

        foreach (var id in completed) _sim.CompleteJob(id);
    }
}
