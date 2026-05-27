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

    // Reused per-tick scratch so HaulSystem.Step doesn't allocate a fresh
    // List/Dictionary every 1/60s. Cleared at the top of Step.
    private readonly List<Entity> _candidates = new();
    private readonly Dictionary<TilePos, int> _woodAt = new();

    public HaulSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        // Single pass over the Wood query: collect haul candidates AND
        // build the tile→count index used to score dest tiles. Prior
        // version paid an O(W) Query<Wood> scan per candidate inside
        // TryFindBestHaulDest, making per-tick cost O(W²).
        // MergeCoincidentWood guarantees at-most-one Wood entity per
        // tile so direct assignment (not aggregation) is correct.
        _candidates.Clear();
        _woodAt.Clear();
        store.Query<Wood>().ForEachEntity((ref Wood w, Entity ent) =>
        {
            _woodAt[w.Tile] = w.Count;
            if (ent.HasComponent<HaulReserved>()) return;
            if (ent.HasComponent<Forbidden>()) return;
            _candidates.Add(ent);
        });

        foreach (var ent in _candidates)
        {
            var w = ent.GetComponent<Wood>();
            var sourceTile = w.Tile;
            int count = w.Count;

            bool onAllowedStockpile = _sim.TryGetStockpileAt(sourceTile, out var pileHere)
                && pileHere.Allows(ItemCatalog.Wood);

            if (!_sim.TryFindBestHaulDest(sourceTile, ItemCatalog.Wood, count, _woodAt,
                out var destTile, out var stockpileId)) continue;

            // Already on an allowed stockpile tile: only post a merge haul
            // if dest is a different tile that holds at least as much. The
            // (y,x) tiebreak makes equal piles consolidate onto the lower
            // tile (one direction wins, no swap loop).
            if (onAllowedStockpile)
            {
                if (destTile == sourceTile) continue;
                int existing = _woodAt.TryGetValue(destTile, out var ec) ? ec : 0;
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
