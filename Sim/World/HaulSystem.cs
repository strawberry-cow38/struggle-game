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
    // Haul posting doesn't need 60 Hz — a freshly dropped stack getting a
    // haul job a few ticks late is invisible. Re-evaluate every N ticks
    // instead of every tick. (Pawns still claim the posted job the next
    // tick via the JobBoard version bump.)
    public const int ScanIntervalTicks = 6;

    private readonly JobBoard _jobs;
    private readonly SimRuntime _sim;

    // Reused per-tick scratch so HaulSystem.Step doesn't allocate a fresh
    // List/Dictionary every 1/60s. Cleared at the top of Step.
    private readonly List<(Entity Ent, TilePos Tile, int Count, ItemDef Def)> _candidates = new();
    // Per-item-path tile→count index. Built once per tick from both Wood
    // entities (always Wood path) and ItemPile entities (whatever ItemPath
    // they carry). TryFindBestHaulDest needs the per-item view so a
    // carrot pile doesn't try to merge into a Wood stack.
    private readonly Dictionary<string, Dictionary<TilePos, int>> _stackAt = new();

    // Cached queries — Store.Query<>() allocates a query object per call.
    private ArchetypeQuery<ItemPile>? _itemPileQ;

    public HaulSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        if (_sim.Tick % ScanIntervalTicks != 0) return;
        _candidates.Clear();
        foreach (var kv in _stackAt) kv.Value.Clear();

        // One query — wood is just an ItemPile of the wood path now.
        (_itemPileQ ??= store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity ent) =>
        {
            if (!ItemCatalog.ItemsByPath.TryGetValue(p.ItemPath, out var def)) return;
            var idx = GetOrCreateIndex(p.ItemPath);
            idx[p.Tile] = p.Count;
            if (ent.HasComponent<HaulReserved>()) return;
            if (ent.HasComponent<Forbidden>()) return;
            _candidates.Add((ent, p.Tile, p.Count, def));
        });

        foreach (var cand in _candidates)
        {
            var sourceTile = cand.Tile;
            int count = cand.Count;
            var def = cand.Def;
            var idx = _stackAt[def.FullPath];

            bool onAllowedStockpile = _sim.TryGetStockpileAt(sourceTile, out var pileHere)
                && pileHere.Allows(def);

            if (!_sim.TryFindBestHaulDest(sourceTile, def, count, idx,
                out var destTile, out var stockpileId)) continue;

            // Already on an allowed stockpile tile: only post a merge haul
            // if dest is a different tile that holds at least as much. The
            // (y,x) tiebreak makes equal piles consolidate onto the lower
            // tile (one direction wins, no swap loop).
            if (onAllowedStockpile)
            {
                if (destTile == sourceTile) continue;
                int existing = idx.TryGetValue(destTile, out var ec) ? ec : 0;
                if (existing < count) continue;
                if (existing == count)
                {
                    bool destIsLower = destTile.Y < sourceTile.Y
                        || (destTile.Y == sourceTile.Y && destTile.X < sourceTile.X);
                    if (!destIsLower) continue;
                }
            }

            cand.Ent.AddComponent(new HaulPayload
            {
                DestTile = destTile,
                StockpileId = stockpileId,
                ItemPath = def.FullPath,
                Count = count,
            });
            var id = _jobs.Post(JobKind.Haul, sourceTile, cand.Ent);
            if (id.IsNone)
            {
                // Tile already had a job — back the component out.
                cand.Ent.RemoveComponent<HaulPayload>();
                continue;
            }
            cand.Ent.AddComponent(new HaulReserved { JobId = id });
            _sim.ReserveHaulDest(destTile);
        }
    }

    private Dictionary<TilePos, int> GetOrCreateIndex(string path)
    {
        if (!_stackAt.TryGetValue(path, out var idx))
        {
            idx = new Dictionary<TilePos, int>();
            _stackAt[path] = idx;
        }
        return idx;
    }
}
