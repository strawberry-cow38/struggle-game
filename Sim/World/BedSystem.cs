using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build / decon advancer for beds. Worker must be adjacent (Chebyshev 1)
// to either tile of the 2-tile footprint; standing on a footprint tile
// is impossible since the bed (or blueprint) blocks both. Completion +
// the actual entity promotion / removal happen in SimRuntime.CompleteJob.
public sealed class BedSystem
{
    public const float BedBuildTimeSec = 2.5f;
    public const float BedDeconTimeSec = 1.5f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public BedSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    private readonly List<JobId> _completed = new();

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var workers = store.Query<WorldPos, BuildTarget, Wanderer>();
        workers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null) return;
            if (job.Kind != JobKind.BedBuild && job.Kind != JobKind.BedDeconstruct) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            Map.TilePos origin, foot;
            if (job.Kind == JobKind.BedBuild)
            {
                var bp = job.Entity.GetComponent<BedBlueprint>();
                origin = bp.Origin;
                foot = BedOrientations.Foot(bp.Origin, bp.Orientation);
            }
            else
            {
                if (!_sim.BedMap.TryGetValue(job.Tile, out var bedEnt)) return;
                var bed = bedEnt.GetComponent<Bed>();
                origin = bed.Origin;
                foot = BedOrientations.Foot(bed.Origin, bed.Orientation);
            }

            if (!BuildAdjacency.InRange(pos.X, pos.Y, origin.X, origin.Y)
             && !BuildAdjacency.InRange(pos.X, pos.Y, foot.X, foot.Y)) return;

            if (job.Kind == JobKind.BedBuild)
            {
                ref var bp = ref job.Entity.GetComponent<BedBlueprint>();
                bp.ProgressSec += dt;
                if (bp.ProgressSec >= BedBuildTimeSec) _completed.Add(job.Id);
            }
            else
            {
                ref var decon = ref job.Entity.GetComponent<Decon>();
                decon.ProgressSec += dt;
                if (decon.ProgressSec >= BedDeconTimeSec) _completed.Add(job.Id);
            }
        });
        foreach (var id in _completed) _sim.CompleteJob(id);
    }
}
