using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;

namespace StruggleGame.Sim.World;

// Per-tick: advance ProgressSec on every RoofBuild / RoofRemove job
// whose claimant is standing on (or 4-adjacent to) the job tile. Roofs
// are built from underneath, so the worker may stand on the tile itself
// — same adjacency rule as flooring.
//
// On completion SimRuntime.CompleteJob flips the roof bit, bumps
// RoofVersion, and deletes the marker entity.
public sealed class RoofSystem
{
    public const float RoofBuildTimeSec = 1.5f;
    public const float RoofRemoveTimeSec = 0.8f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public RoofSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        var completed = new List<JobId>();
        var workers = store.Query<WorldPos, BuildTarget, Wanderer>();
        workers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null) return;
            if (job.Kind != JobKind.RoofBuild && job.Kind != JobKind.RoofRemove) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            if (!BuildAdjacency.InRangeOrOnTile(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;

            ref var bp = ref job.Entity.GetComponent<RoofBlueprint>();
            bp.ProgressSec += dt;
            float target_t = job.Kind == JobKind.RoofBuild ? RoofBuildTimeSec : RoofRemoveTimeSec;
            if (bp.ProgressSec >= target_t)
            {
                completed.Add(job.Id);
            }
        });
        foreach (var id in completed) _sim.CompleteJob(id);
    }
}
