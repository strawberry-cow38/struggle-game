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
    private readonly List<JobId> _completed = new();

    public ChopSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var choppers = store.Query<WorldPos, BuildTarget, Wanderer>();
        choppers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.ChopTree) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            if (!BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;

            ref var tree = ref job.Entity.GetComponent<Tree>();
            tree.ChopProgressSec += dt;
            if (tree.ChopProgressSec >= ChopTimeSec)
            {
                _completed.Add(job.Id);
            }
        });

        foreach (var id in _completed) _sim.CompleteJob(id);
    }
}
