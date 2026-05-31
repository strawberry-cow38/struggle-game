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

    private readonly List<JobId> _completed = new();
    private ArchetypeQuery<WorldPos, BuildTarget, Wanderer>? _workersQ;

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var workers = _workersQ ??= store.Query<WorldPos, BuildTarget, Wanderer>();
        workers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _w, Entity worker) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.DoorBuild) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            if (!BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;
            if (!_sim.IsBlueprintFunded(job.Entity)) return;

            ref var bp = ref job.Entity.GetComponent<DoorBlueprint>();
            bp.ProgressSec += dt * HealthMods.WorkSpeed(worker);
            if (bp.ProgressSec >= DoorTimeSec)
            {
                _completed.Add(job.Id);
            }
        });

        foreach (var id in _completed) _sim.CompleteJob(id);
    }
}
