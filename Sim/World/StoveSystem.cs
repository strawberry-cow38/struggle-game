using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Build / decon advancer for stoves. Worker is adjacent to any of the
// 4 footprint tiles (3 body + 1 standing). Cook progress is advanced by
// CookSystem, not this one.
public sealed class StoveSystem
{
    public const float StoveBuildTimeSec = 4.0f;
    public const float StoveDeconTimeSec = 2.5f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public StoveSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    private readonly List<JobId> _completed = new();

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var workers = store.Query<WorldPos, BuildTarget, Wanderer>();
        workers.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _w, Entity worker) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null) return;
            if (job.Kind != JobKind.StoveBuild && job.Kind != JobKind.StoveDeconstruct) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;

            TilePos origin;
            StoveOrientation orientation;
            if (job.Kind == JobKind.StoveBuild)
            {
                var bp = job.Entity.GetComponent<StoveBlueprint>();
                origin = bp.Origin;
                orientation = bp.Orientation;
            }
            else
            {
                if (!_sim.StoveMap.TryGetValue(job.Tile, out var stoveEnt)) return;
                var s = stoveEnt.GetComponent<Stove>();
                origin = s.Origin;
                orientation = s.Orientation;
            }

            bool adjacent = false;
            foreach (var t in StoveOrientations.BodyTiles(origin, orientation))
            {
                if (BuildAdjacency.InRange(pos.X, pos.Y, t.X, t.Y)) { adjacent = true; break; }
            }
            if (!adjacent)
            {
                var st = StoveOrientations.StandingTile(origin, orientation);
                if (BuildAdjacency.InRange(pos.X, pos.Y, st.X, st.Y)) adjacent = true;
            }
            if (!adjacent) return;

            if (job.Kind == JobKind.StoveBuild)
            {
                if (!_sim.IsBlueprintFunded(job.Entity)) return;
                ref var bp = ref job.Entity.GetComponent<StoveBlueprint>();
                bp.ProgressSec += dt * HealthMods.WorkSpeed(worker);
                if (bp.ProgressSec >= StoveBuildTimeSec) _completed.Add(job.Id);
            }
            else
            {
                ref var decon = ref job.Entity.GetComponent<Decon>();
                decon.ProgressSec += dt * HealthMods.WorkSpeed(worker);
                if (decon.ProgressSec >= StoveDeconTimeSec) _completed.Add(job.Id);
            }
        });
        foreach (var id in _completed) _sim.CompleteJob(id);
    }
}
