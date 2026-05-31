using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Shared skeleton for build/deconstruct advancers that pair a "build a
// blueprint" job with a "deconstruct the finished thing" job. Each tick
// every working pawn (WorldPos + BuildTarget + Wanderer) in range of its
// target advances the relevant progress field; on completion
// SimRuntime.CompleteJob performs the entity promotion/removal.
//
// Subclasses supply only what differs between buildables: the job kinds,
// the build/decon durations, whether the build is funding-gated, the
// adjacency + target-existence test (footprint geometry varies), and a
// ref to the blueprint's ProgressSec. The decon side is uniform — every
// deconstruct job advances the Decon component on the job entity — so it
// lives entirely here. The abstract ref-returning BuildProgress keeps the
// per-tick path allocation-free: no delegates, no interface boxing.
public abstract class BuildableSystem
{
    private readonly JobBoard _jobs;
    protected readonly SimRuntime Sim;
    private readonly List<JobId> _completed = new();
    private ArchetypeQuery<WorldPos, BuildTarget, Wanderer>? _workersQ;

    protected BuildableSystem(SimRuntime sim, JobBoard jobs)
    {
        Sim = sim;
        _jobs = jobs;
    }

    protected abstract JobKind BuildKind { get; }
    protected abstract JobKind DeconKind { get; }
    protected abstract float BuildSeconds { get; }
    protected abstract float DeconSeconds { get; }

    // Build jobs only start once their material cost is funded. Free
    // buildables (lamps) override this to false.
    protected virtual bool RequiresFunding => true;

    // True when `worker` at (px,py) may advance the BUILD job — blueprint
    // still present and worker in range of its footprint.
    protected abstract bool BuildReady(Job job, float px, float py);

    // Same for the DECON job: target entity still present and worker in
    // range. Implementations resolve footprint geometry from the live
    // entity (via the per-system map) since the blueprint is gone.
    protected abstract bool DeconReady(Job job, float px, float py);

    // Ref to the blueprint entity's ProgressSec so the base advances it.
    protected abstract ref float BuildProgress(Entity blueprint);

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var workers = _workersQ ??= store.Query<WorldPos, BuildTarget, Wanderer>();
        workers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _w, Entity worker) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null) return;
            if (job.Kind != BuildKind && job.Kind != DeconKind) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            if (job.Kind == BuildKind)
            {
                if (!BuildReady(job, pos.X, pos.Y)) return;
                if (RequiresFunding && !Sim.IsBlueprintFunded(job.Entity)) return;
                ref float progress = ref BuildProgress(job.Entity);
                progress += dt * HealthMods.WorkSpeed(worker);
                if (progress >= BuildSeconds) _completed.Add(job.Id);
            }
            else
            {
                if (!DeconReady(job, pos.X, pos.Y)) return;
                ref var decon = ref job.Entity.GetComponent<Decon>();
                decon.ProgressSec += dt * HealthMods.WorkSpeed(worker);
                if (decon.ProgressSec >= DeconSeconds) _completed.Add(job.Id);
            }
        });
        foreach (var id in _completed) Sim.CompleteJob(id);
    }
}
