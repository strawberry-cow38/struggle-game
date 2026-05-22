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
            if (ent.HasComponent<Forbidden>()) return;
            candidates.Add(ent);
        });

        foreach (var ent in candidates)
        {
            var w = ent.GetComponent<Wood>();
            var sourceTile = w.Tile;
            int count = w.Count;

            bool onAllowedStockpile = _sim.TryGetStockpileAt(sourceTile, out var pileHere)
                && pileHere.Allows(ItemCatalog.Wood);

            if (!_sim.TryFindBestHaulDest(sourceTile, ItemCatalog.Wood, count,
                out var destTile, out var stockpileId)) continue;

            // Already on an allowed stockpile tile: only post a merge haul
            // if dest is a different tile that holds at least as much. The
            // (y,x) tiebreak makes equal piles consolidate onto the lower
            // tile (one direction wins, no swap loop).
            if (onAllowedStockpile)
            {
                if (destTile == sourceTile) continue;
                int existing = _sim.WoodCountAtTile(destTile);
                if (existing < count) continue;
                if (existing == count)
                {
                    bool destIsLower = destTile.Y < sourceTile.Y
                        || (destTile.Y == sourceTile.Y && destTile.X < sourceTile.X);
                    if (!destIsLower) continue;
                }
            }

            ent.AddComponent(new HaulPayload
            {
                DestTile = destTile,
                StockpileId = stockpileId,
                ItemPath = ItemCatalog.Wood.FullPath,
                Count = count,
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
