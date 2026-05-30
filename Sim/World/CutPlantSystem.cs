using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick progress for CutPlants jobs. Targets immature trees (<50%
// growth — the chop designator refuses those) and crops at any stage.
// Trees: progress lives on Tree.ChopProgressSec (the field doubles as
// generic work-on-this-tree progress). Crops: progress lives on
// Crop.WorkProgressSec.
public sealed class CutPlantSystem
{
    public const float CutTimeSec = 1.0f;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public CutPlantSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    private readonly List<JobId> _completed = new();

    private ArchetypeQuery<WorldPos, BuildTarget, Wanderer>? _cuttersQ;

    public void Step(EntityStore store, float dt)
    {
        _completed.Clear();
        var cutters = _cuttersQ ??= store.Query<WorldPos, BuildTarget, Wanderer>();
        cutters.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _w, Entity worker) =>
        {
            var job = _jobs.Get(target.JobId);
            if (job is null || job.Kind != JobKind.CutPlants) return;
            if (job.State != JobState.Open && job.State != JobState.Claimed) return;
            if (!BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y)) return;

            if (job.Entity.HasComponent<Tree>())
            {
                ref var t = ref job.Entity.GetComponent<Tree>();
                t.ChopProgressSec += dt * HealthMods.WorkSpeed(worker);
                if (t.ChopProgressSec >= CutTimeSec) _completed.Add(job.Id);
            }
            else if (job.Entity.HasComponent<Crop>())
            {
                ref var c = ref job.Entity.GetComponent<Crop>();
                c.WorkProgressSec += dt * HealthMods.WorkSpeed(worker);
                if (c.WorkProgressSec >= CutTimeSec) _completed.Add(job.Id);
            }
        });

        foreach (var id in _completed) _sim.CompleteJob(id);
    }
}
