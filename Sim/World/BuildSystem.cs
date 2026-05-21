using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick: after movement, look at every Wanderer whose BuildTarget
// points at an open WallBuild job. If they're 4-adjacent, advance
// ProgressSec. Completed jobs are reported back to SimRuntime which
// mutates the map and bumps MapVersion.
public sealed class BuildSystem
{
    public const float BuildTimeSec = 1.5f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public BuildSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        var completed = new List<JobId>();

        var builders = store.Query<WorldPos, BuildTarget, Wanderer>();
        builders.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.WallBuild) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            int btx = job.Tile.X;
            int bty = job.Tile.Y;
            int ptx = (int)pos.X;
            int pty = (int)pos.Y;
            int dx = Math.Abs(ptx - btx);
            int dy = Math.Abs(pty - bty);
            // 4-connected adjacency only (no diagonal "reach").
            if (dx + dy != 1) return;

            ref var blueprint = ref job.Entity.GetComponent<Blueprint>();
            blueprint.ProgressSec += dt;
            if (blueprint.ProgressSec >= BuildTimeSec)
            {
                completed.Add(job.Id);
            }
        });

        foreach (var id in completed)
        {
            _sim.CompleteJob(id);
        }
    }
}
