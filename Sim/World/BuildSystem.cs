using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick: after movement, look at every Wanderer whose BuildTarget
// points at an open WallBuild job. If they're 4-adjacent, advance
// ProgressSec. Completed jobs are reported back to SimRuntime which
// mutates the map and bumps MapVersion.
//
// Completion is gated on the blueprint tile being unoccupied: another
// wandering pawn standing on the tile would get a wall spawned under
// them. When blocked, progress is clamped one dt below BuildTimeSec and
// the builder is released (BuildTarget dropped, job back to Open) so
// any pawn can re-claim once the tile clears.
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
        var occupied = new HashSet<TilePos>();
        store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            occupied.Add(new TilePos((int)p.X, (int)p.Y));
        });

        var completed = new List<JobId>();
        var releaseBlocked = new List<(JobId Id, int EntityId)>();

        var builders = store.Query<WorldPos, BuildTarget, Wanderer>();
        builders.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity ent) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.WallBuild) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            if (!BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;

            ref var blueprint = ref job.Entity.GetComponent<Blueprint>();
            blueprint.ProgressSec += dt;
            if (blueprint.ProgressSec >= BuildTimeSec)
            {
                if (occupied.Contains(job.Tile))
                {
                    // Hold one tick under completion so as soon as the
                    // tile is free a single tick of work finishes it.
                    blueprint.ProgressSec = BuildTimeSec - dt;
                    releaseBlocked.Add((job.Id, ent.Id));
                }
                else
                {
                    completed.Add(job.Id);
                }
            }
        });

        foreach (var (id, entityId) in releaseBlocked)
        {
            if (store.TryGetEntityById(entityId, out var builder) && builder.HasComponent<BuildTarget>())
            {
                builder.RemoveComponent<BuildTarget>();
            }
            _jobs.Release(id);
        }

        foreach (var id in completed)
        {
            _sim.CompleteJob(id);
        }
    }
}
