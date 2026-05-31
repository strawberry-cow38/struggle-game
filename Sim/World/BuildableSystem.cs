using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Shared skeleton for build/deconstruct advancers. Each tick every working
// pawn (WorldPos + BuildTarget + Wanderer) in range of its target advances
// the relevant progress field; on completion SimRuntime.CompleteJob
// performs the entity promotion/removal.
//
// Construction is gated on the footprint being clear: a buildable can't pop
// into existence under a pawn or on top of an item stack. When a finished
// build is blocked, progress is held one tick under completion and the
// builder is released (BuildTarget dropped, job re-opened) so it finishes
// the instant the tile clears. Occupancy comes from the shared
// Sim.OccupiedPawnTiles set (rebuilt once per tick) — no per-system pawn
// rescan.
//
// Subclasses supply what differs per buildable: the job kinds, the
// build/decon durations, whether the build is funding-gated, the adjacency
// + target-existence tests (footprint geometry varies), a ref to the
// blueprint's ProgressSec, and which footprint tiles must be clear. The
// decon side is uniform (advances the Decon component on the job entity) so
// it lives here. Build-only buildables (walls, doors — their decon runs in
// the separate DeconSystem) point DeconKind back at BuildKind so the decon
// branch never fires. The abstract ref-returning BuildProgress keeps the
// per-tick path allocation-free.
public abstract class BuildableSystem
{
    private readonly JobBoard _jobs;
    protected readonly SimRuntime Sim;
    private readonly List<JobId> _completed = new();
    private readonly List<(JobId Id, int EntityId)> _blocked = new();
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

    // Same for the DECON job. Build-only subclasses return false.
    protected abstract bool DeconReady(Job job, float px, float py);

    // Ref to the blueprint entity's ProgressSec so the base advances it.
    protected abstract ref float BuildProgress(Entity blueprint);

    // True when any tile the finished buildable will occupy is blocked by a
    // pawn or an item stack — construction holds until it clears.
    protected abstract bool FootprintBlocked(Job buildJob);

    // Footprint helper for subclasses: a single tile blocked by a pawn or item.
    protected bool TileBlocked(TilePos t) => Sim.OccupiedPawnTiles.Contains(t) || Sim.ItemIndex.AnyItemAt(t);

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        _blocked.Clear();
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
                if (progress >= BuildSeconds)
                {
                    if (FootprintBlocked(job))
                    {
                        // Hold one tick under completion + release the builder
                        // so it (or another pawn) finishes once the tile clears.
                        progress = BuildSeconds - dt;
                        _blocked.Add((job.Id, worker.Id));
                    }
                    else
                    {
                        _completed.Add(job.Id);
                    }
                }
            }
            else
            {
                if (!DeconReady(job, pos.X, pos.Y)) return;
                ref var decon = ref job.Entity.GetComponent<Decon>();
                decon.ProgressSec += dt * HealthMods.WorkSpeed(worker);
                if (decon.ProgressSec >= DeconSeconds) _completed.Add(job.Id);
            }
        });

        foreach (var (id, entityId) in _blocked)
        {
            if (store.TryGetEntityById(entityId, out var builder) && builder.HasComponent<BuildTarget>())
            {
                builder.RemoveComponent<BuildTarget>();
            }
            _jobs.Release(id);
        }
        foreach (var id in _completed) Sim.CompleteJob(id);
    }
}
