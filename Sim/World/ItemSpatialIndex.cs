using Friflo.Engine.ECS;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Incremental spatial index of ground items (Wood + ItemPile) so systems
// stop full-scanning every item entity every tick.
//
// STALENESS — the whole risk — is handled like this:
//   • Add/remove of a Wood/ItemPile component is mirrored here via the
//     EntityStore's OnComponentAdded / OnComponentRemoved events. Those
//     fire at the real structural-change moment, including CommandBuffer
//     playback, so deferred haul pickup (remove) and deliver (add) are
//     covered automatically — no per-call hook to forget.
//   • Friflo does NOT fire OnComponentRemoved when an ENTITY is deleted
//     (verified on 3.4). The only sites that delete an entity while it
//     still holds an item component are the count-drain in
//     TryConsumeFromPile and the two MergeCoincident* passes. Those call
//     OnEntityGone(id) explicitly.
//   • .Count mutations never fire events — but this index stores no
//     counts. A pile drained to zero is deleted (→ OnEntityGone), so any
//     indexed entity is guaranteed Count > 0. Consumers read live .Count
//     off the component when they need the exact number.
//   • Safety net: SimRuntime runs ValidateAgainst() under #if DEBUG every
//     few hundred ticks; it full-scans and throws on any drift, so a
//     missed site is caught loudly in dev and costs nothing in release.
public sealed class ItemSpatialIndex
{
    public const int ChunkShift = 4;          // 16x16 tile chunks
    public const int ChunkSize = 1 << ChunkShift;

    private readonly struct Entry
    {
        public readonly TilePos Tile;
        public readonly bool IsWood;
        public readonly string Path;
        public Entry(TilePos tile, bool isWood, string path) { Tile = tile; IsWood = isWood; Path = path; }
    }

    private readonly Dictionary<int, Entry> _byEntity = new();
    private readonly Dictionary<TilePos, int> _woodTileCount = new();
    private readonly Dictionary<(int, int), List<int>> _chunks = new();
    // Item entities currently carrying HaulReserved (any kind, wood or pile).
    private readonly HashSet<int> _reserved = new();
    // Count of UNRESERVED wood per tile — what wedges a door open. Kept in
    // sync as wood is added/removed and as reservations flip on/off.
    private readonly Dictionary<TilePos, int> _unreservedWoodTileCount = new();
    // Set whenever an item is added; lets the merge passes skip entirely on
    // ticks where nothing new dropped (a coincident stack can only appear
    // when an item is added to an already-occupied tile).
    private bool _addedSinceMergeDrain;

    private static (int, int) ChunkOf(TilePos t) => (t.X >> ChunkShift, t.Y >> ChunkShift);

    // ── maintenance (called from event handlers + delete sites) ──────────

    public void OnItemAdded(int entityId, TilePos tile, bool isWood, string path)
    {
        if (_byEntity.ContainsKey(entityId)) return; // idempotent
        _addedSinceMergeDrain = true;
        _byEntity[entityId] = new Entry(tile, isWood, path);
        if (isWood)
        {
            _woodTileCount[tile] = _woodTileCount.GetValueOrDefault(tile) + 1;
            // Fresh wood isn't reserved yet, but stay defensive in case the
            // reserve event somehow landed first.
            if (!_reserved.Contains(entityId)) Bump(_unreservedWoodTileCount, tile, +1);
        }
        var key = ChunkOf(tile);
        if (!_chunks.TryGetValue(key, out var list)) { list = new List<int>(); _chunks[key] = list; }
        list.Add(entityId);
    }

    public void OnEntityGone(int entityId)
    {
        if (!_byEntity.TryGetValue(entityId, out var e)) return;
        _byEntity.Remove(entityId);
        if (e.IsWood)
        {
            int n = _woodTileCount.GetValueOrDefault(e.Tile) - 1;
            if (n <= 0) _woodTileCount.Remove(e.Tile); else _woodTileCount[e.Tile] = n;
            // Only the unreserved bucket counted it.
            if (!_reserved.Contains(entityId)) Bump(_unreservedWoodTileCount, e.Tile, -1);
        }
        _reserved.Remove(entityId);
        var key = ChunkOf(e.Tile);
        if (_chunks.TryGetValue(key, out var list))
        {
            list.Remove(entityId);
            if (list.Count == 0) _chunks.Remove(key);
        }
    }

    // HaulReserved added/removed (mirrored from the component events). A
    // reserved wood stack no longer wedges a door — a pawn is coming for it.
    public void OnReservedAdded(int entityId)
    {
        if (!_reserved.Add(entityId)) return;
        if (_byEntity.TryGetValue(entityId, out var e) && e.IsWood)
            Bump(_unreservedWoodTileCount, e.Tile, -1);
    }

    public void OnReservedRemoved(int entityId)
    {
        if (!_reserved.Remove(entityId)) return;
        if (_byEntity.TryGetValue(entityId, out var e) && e.IsWood)
            Bump(_unreservedWoodTileCount, e.Tile, +1);
    }

    private static void Bump(Dictionary<TilePos, int> map, TilePos tile, int delta)
    {
        int n = map.GetValueOrDefault(tile) + delta;
        if (n <= 0) map.Remove(tile); else map[tile] = n;
    }

    // True (once) if any item was added since the last call. Lets the
    // merge passes run only on ticks where a new stack actually landed.
    public bool ConsumeMergeFlag()
    {
        if (!_addedSinceMergeDrain) return false;
        _addedSinceMergeDrain = false;
        return true;
    }

    // ── queries ──────────────────────────────────────────────────────────

    // True if any Wood stack sits on the tile (reserved or not). Matches
    // the old all-wood occupancy used by build-blocking checks.
    public bool AnyWoodAt(TilePos tile) => _woodTileCount.ContainsKey(tile);

    // True if an UNRESERVED wood stack sits on the tile — the door-wedge
    // condition (reserved wood is about to be hauled away, doesn't count).
    public bool AnyUnreservedWoodAt(TilePos tile) => _unreservedWoodTileCount.ContainsKey(tile);

    // Nearest (Manhattan) item entity whose path matches, searched chunk
    // ring by chunk ring out from `from`. Returns false if none exist.
    public bool TryGetNearest(TilePos from, string path, out TilePos tile)
    {
        tile = default;
        if (_chunks.Count == 0) return false;
        int best = int.MaxValue;
        var fromChunk = ChunkOf(from);
        // Expand rings until a ring's closest-possible tile can't beat best.
        for (int r = 0; ; r++)
        {
            // Closest a tile in ring r can be: (r-1) full chunks away.
            int ringMin = (r - 1) * ChunkSize;
            if (best != int.MaxValue && ringMin > best) break;
            bool anyChunkThisRing = false;
            for (int cx = fromChunk.Item1 - r; cx <= fromChunk.Item1 + r; cx++)
            for (int cy = fromChunk.Item2 - r; cy <= fromChunk.Item2 + r; cy++)
            {
                // Only the outer shell of the (2r+1) box is new this ring.
                if (r > 0 && Math.Abs(cx - fromChunk.Item1) != r && Math.Abs(cy - fromChunk.Item2) != r) continue;
                if (!_chunks.TryGetValue((cx, cy), out var list)) continue;
                anyChunkThisRing = true;
                foreach (var id in list)
                {
                    var e = _byEntity[id];
                    if (e.Path != path) continue;
                    int d = Math.Abs(e.Tile.X - from.X) + Math.Abs(e.Tile.Y - from.Y);
                    if (d < best) { best = d; tile = e.Tile; }
                }
            }
            // Stop once we've gone a ring past the whole populated extent.
            if (!anyChunkThisRing && best != int.MaxValue && ringMin > best) break;
            // Hard cap so an empty search can't loop forever.
            if (r > SimConstants.MapSize / ChunkSize + 1) break;
        }
        return best != int.MaxValue;
    }

    // ── debug-only consistency check ─────────────────────────────────────

    // Rebuilds the truth from the live store and throws on any divergence.
    // Called from SimRuntime under #if DEBUG only.
    public void ValidateAgainst(EntityStore store)
    {
        var live = new Dictionary<int, Entry>();
        var liveUnreservedWood = new Dictionary<TilePos, int>();
        store.Query<Wood>().ForEachEntity((ref Wood w, Entity e) =>
        {
            live[e.Id] = new Entry(w.Tile, true, ItemCatalog.Wood.FullPath);
            if (!e.HasComponent<HaulReserved>()) Bump(liveUnreservedWood, w.Tile, +1);
        });
        store.Query<ItemPile>().ForEachEntity((ref ItemPile p, Entity e) =>
            live[e.Id] = new Entry(p.Tile, false, p.ItemPath));

        if (liveUnreservedWood.Count != _unreservedWoodTileCount.Count)
            throw new InvalidOperationException($"ItemSpatialIndex drift: unreserved-wood tiles index={_unreservedWoodTileCount.Count} store={liveUnreservedWood.Count}.");
        foreach (var (tile, n) in liveUnreservedWood)
            if (_unreservedWoodTileCount.GetValueOrDefault(tile) != n)
                throw new InvalidOperationException($"ItemSpatialIndex drift: unreserved wood at {tile} index={_unreservedWoodTileCount.GetValueOrDefault(tile)} store={n}.");

        if (live.Count != _byEntity.Count)
            throw new InvalidOperationException($"ItemSpatialIndex drift: index has {_byEntity.Count} entries, store has {live.Count}.");
        foreach (var (id, le) in live)
        {
            if (!_byEntity.TryGetValue(id, out var ie))
                throw new InvalidOperationException($"ItemSpatialIndex drift: entity {id} on tile {le.Tile} missing from index.");
            if (ie.Tile != le.Tile || ie.IsWood != le.IsWood || ie.Path != le.Path)
                throw new InvalidOperationException($"ItemSpatialIndex drift: entity {id} index={ie.Tile}/{ie.Path} store={le.Tile}/{le.Path}.");
        }
    }
}
