using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick: for every colonist whose BuildTarget points at an open
// ChopTree job, if they're 4-adjacent advance ChopProgressSec. When it
// hits ChopTimeSec the job completes — SimRuntime deletes the tree,
// drops a Wood entity, and rebuilds the map view so the tile becomes
// walkable.
//
// Mirrors BuildSystem; kept separate so each verb owns its own gating
// rules (e.g. chop has no "occupant blocks completion" gate since the
// tree tile is unwalkable anyway).
public sealed class ChopSystem
{
    public const float ChopTimeSec = 2.0f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public ChopSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        var completed = new List<JobId>();
        var choppers = store.Query<WorldPos, BuildTarget, Wanderer>();
        choppers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.ChopTree) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            int dx = Math.Abs((int)pos.X - job.Tile.X);
            int dy = Math.Abs((int)pos.Y - job.Tile.Y);
            // Chebyshev-1: must stand on one of the 8 neighbors.
            if (dx > 1 || dy > 1 || (dx == 0 && dy == 0)) return;

            ref var tree = ref job.Entity.GetComponent<Tree>();
            tree.ChopProgressSec += dt;
            if (tree.ChopProgressSec >= ChopTimeSec)
            {
                completed.Add(job.Id);
            }
        });

        foreach (var id in completed) _sim.CompleteJob(id);
    }
}
