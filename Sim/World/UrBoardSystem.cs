using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Build / decon advancer for Ur boards. 1x1 footprint — worker stands
// on any 4-adjacent walkable tile (Chebyshev 1). SimRuntime.CompleteJob
// handles the actual blueprint→board entity promotion.
public sealed class UrBoardSystem
{
    public const float BuildTimeSec = 2.5f;
    public const float DeconTimeSec = 1.5f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;
    private readonly List<JobId> _completed = new();

    public UrBoardSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var workers = store.Query<WorldPos, BuildTarget, Wanderer>();
        workers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null) return;
            if (job.Kind != JobKind.UrBoardBuild && job.Kind != JobKind.UrBoardDeconstruct) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            Map.TilePos t;
            if (job.Kind == JobKind.UrBoardBuild)
            {
                var bp = job.Entity.GetComponent<UrBoardBlueprint>();
                t = bp.Tile;
            }
            else
            {
                if (!_sim.UrBoardMap.TryGetValue(job.Tile, out _)) return;
                t = job.Tile;
            }

            if (!BuildAdjacency.InRange(pos.X, pos.Y, t.X, t.Y)) return;

            if (job.Kind == JobKind.UrBoardBuild)
            {
                if (!_sim.IsBlueprintFunded(job.Entity)) return;
                ref var bp = ref job.Entity.GetComponent<UrBoardBlueprint>();
                bp.ProgressSec += dt;
                if (bp.ProgressSec >= BuildTimeSec) _completed.Add(job.Id);
            }
            else
            {
                ref var decon = ref job.Entity.GetComponent<Decon>();
                decon.ProgressSec += dt;
                if (decon.ProgressSec >= DeconTimeSec) _completed.Add(job.Id);
            }
        });
        foreach (var id in _completed) _sim.CompleteJob(id);
    }
}
