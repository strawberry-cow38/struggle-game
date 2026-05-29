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

    // Reused per-tick scratch — cleared at top of Step rather than freshly
    // allocated, so the per-tick path doesn't churn the GC.
    private readonly HashSet<TilePos> _occupied = new();
    private readonly List<JobId> _completed = new();
    private readonly List<(JobId Id, int EntityId)> _releaseBlocked = new();

    public BuildSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        _occupied.Clear();
        _completed.Clear();
        _releaseBlocked.Clear();
        store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            _occupied.Add(new TilePos((int)p.X, (int)p.Y));
        });
        // Wood stacks on the blueprint tile would get buried by the wall.
        // BlueprintClearanceSystem posts a relocate-haul for them; in the
        // meantime, hold the wall one tick under completion. Occupancy now
        // comes from the item index (see _sim.ItemIndex.AnyWoodAt below)
        // instead of a per-tick full Wood scan.

        var builders = store.Query<WorldPos, BuildTarget, Wanderer>();
        builders.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity ent) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.WallBuild) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            if (!BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;

            // Funded check is a no-op when no BlueprintCost is attached, so
            // existing free-blueprint flows are unaffected. God mode short-
            // circuits the check entirely.
            if (!_sim.IsBlueprintFunded(job.Entity)) return;

            ref var blueprint = ref job.Entity.GetComponent<Blueprint>();
            blueprint.ProgressSec += dt;
            if (blueprint.ProgressSec >= BuildTimeSec)
            {
                if (_occupied.Contains(job.Tile) || _sim.ItemIndex.AnyWoodAt(job.Tile))
                {
                    // Hold one tick under completion so as soon as the
                    // tile is free a single tick of work finishes it.
                    blueprint.ProgressSec = BuildTimeSec - dt;
                    _releaseBlocked.Add((job.Id, ent.Id));
                }
                else
                {
                    _completed.Add(job.Id);
                }
            }
        });

        foreach (var (id, entityId) in _releaseBlocked)
        {
            if (store.TryGetEntityById(entityId, out var builder) && builder.HasComponent<BuildTarget>())
            {
                builder.RemoveComponent<BuildTarget>();
            }
            _jobs.Release(id);
        }

        foreach (var id in _completed)
        {
            _sim.CompleteJob(id);
        }
    }
}
