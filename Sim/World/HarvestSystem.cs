using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick progress for Harvest jobs. Targets crops at ≥75% growth only.
// Progress lives on Crop.WorkProgressSec; completion drops the crop's
// yield item (carrots for now, scaled linearly between min/full).
public sealed class HarvestSystem
{
    public const float HarvestTimeSec = 1.5f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public HarvestSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    private readonly List<JobId> _completed = new();

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var harvesters = store.Query<WorldPos, BuildTarget, Wanderer>();
        harvesters.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.Harvest) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;
            if (!BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;
            if (!job.Entity.HasComponent<Crop>()) return;

            ref var c = ref job.Entity.GetComponent<Crop>();
            c.WorkProgressSec += dt;
            if (c.WorkProgressSec >= HarvestTimeSec) _completed.Add(job.Id);
        });

        foreach (var id in _completed) _sim.CompleteJob(id);
    }
}
