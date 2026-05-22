using Friflo.Engine.ECS;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick poster: scans wood entities that aren't already reserved or
// already sitting on an allowed stockpile tile, picks the highest
// priority + nearest free stockpile cell, and posts a Haul job for it.
// Execution (walk-to-pickup, walk-to-dropoff) lives in
// DummyController so the haul shares the existing path / claim plumbing.
public sealed class HaulSystem
{
    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    public HaulSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        // Snapshot first to avoid mutating wood entities mid-iteration.
        var candidates = new List<Entity>();
        store.Query<Wood>().ForEachEntity((ref Wood w, Entity ent) =>
        {
            if (ent.HasComponent<HaulReserved>()) return;
            candidates.Add(ent);
        });

        foreach (var ent in candidates)
        {
            var w = ent.GetComponent<Wood>();
            var sourceTile = w.Tile;
            // Already on an allowed stockpile tile? Leave it.
            if (_sim.TryGetStockpileAt(sourceTile, out var pileHere)
                && pileHere.Allows(ItemCatalog.Wood))
            {
                continue;
            }
            if (!_sim.TryFindBestHaulDest(sourceTile, ItemCatalog.Wood,
                out var destTile, out var stockpileId)) continue;

            ent.AddComponent(new HaulPayload
            {
                DestTile = destTile,
                StockpileId = stockpileId,
                ItemPath = ItemCatalog.Wood.FullPath,
            });
            var id = _jobs.Post(JobKind.Haul, sourceTile, ent);
            if (id.IsNone)
            {
                // Tile already had a job — back the component out.
                ent.RemoveComponent<HaulPayload>();
                continue;
            }
            ent.AddComponent(new HaulReserved { JobId = id });
            _sim.ReserveHaulDest(destTile);
        }
    }
}
