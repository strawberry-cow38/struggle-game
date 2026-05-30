using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build / decon advancer for sandbags. 1x1 footprint — worker stands on
// any 4-adjacent walkable tile (Chebyshev 1). SimRuntime.CompleteJob
// handles the blueprint→sandbag entity promotion. Mirrors UrBoardSystem.
public sealed class SandbagSystem
{
    public const float BuildTimeSec = 2.0f;
    public const float DeconTimeSec = 1.0f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;
    private readonly List<JobId> _completed = new();

    private ArchetypeQuery<WorldPos, BuildTarget, Wanderer>? _workersQ;

    public SandbagSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var workers = _workersQ ??= store.Query<WorldPos, BuildTarget, Wanderer>();
        workers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _w, Entity worker) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null) return;
            if (job.Kind != JobKind.SandbagBuild && job.Kind != JobKind.SandbagDeconstruct) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            Map.TilePos t;
            if (job.Kind == JobKind.SandbagBuild)
            {
                var bp = job.Entity.GetComponent<SandbagBlueprint>();
                t = bp.Tile;
            }
            else
            {
                if (!_sim.SandbagMap.TryGetValue(job.Tile, out _)) return;
                t = job.Tile;
            }

            if (!BuildAdjacency.InRange(pos.X, pos.Y, t.X, t.Y)) return;

            if (job.Kind == JobKind.SandbagBuild)
            {
                if (!_sim.IsBlueprintFunded(job.Entity)) return;
                ref var bp = ref job.Entity.GetComponent<SandbagBlueprint>();
                bp.ProgressSec += dt * HealthMods.WorkSpeed(worker);
                if (bp.ProgressSec >= BuildTimeSec) _completed.Add(job.Id);
            }
            else
            {
                ref var decon = ref job.Entity.GetComponent<Decon>();
                decon.ProgressSec += dt * HealthMods.WorkSpeed(worker);
                if (decon.ProgressSec >= DeconTimeSec) _completed.Add(job.Id);
            }
        });
        foreach (var id in _completed) _sim.CompleteJob(id);
    }
}
