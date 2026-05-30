using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Incremental spatial index of ground items (the single ItemPile kind —
// wood, carrots, meals are all ItemPiles) so systems stop full-scanning
// every item entity every tick.
//
// STALENESS — the whole risk — is handled like this:
//   • Add/remove of an ItemPile component is mirrored here via the
//     EntityStore's OnComponentAdded / OnComponentRemoved events. Those
//     fire at the real structural-change moment, including CommandBuffer
//     playback, so deferred haul pickup (remove) and deliver (add) are
//     covered automatically — no per-call hook to forget.
//   • Friflo does NOT fire OnComponentRemoved when an ENTITY is deleted
//     (verified on 3.4). The only sites that delete an entity while it
//     still holds an ItemPile are the count-drain in TryConsumeFromPile,
//     the merge pass, and the spill pass. Those call OnEntityGone(id).
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
        public readonly string Path;
        public Entry(TilePos tile, string path) { Tile = tile; Path = path; }
    }

    private readonly Dictionary<int, Entry> _byEntity = new();
    private readonly Dictionary<(int, int), List<int>> _chunks = new();
    // Item entities currently carrying HaulReserved.
    private readonly HashSet<int> _reserved = new();
    // Any item per tile — what blocks a blueprint from completing.
    private readonly Dictionary<TilePos, int> _itemTileCount = new();
    // Number of tiles holding >=2 item stacks — the only tiles the merge/spill
    // passes can ever act on. Lets SimRuntime skip those full scans entirely
    // when no tile is coincident (the common case).
    private int _coincidentTileCount;
    // Unreserved items per tile — what wedges a door open (a reserved
    // stack is about to be hauled away, so it doesn't count).
    private readonly Dictionary<TilePos, int> _unreservedItemTileCount = new();

    private ArchetypeQuery<ItemPile>? _itemPileQ;

    private static (int, int) ChunkOf(TilePos t) => (t.X >> ChunkShift, t.Y >> ChunkShift);

    // ── maintenance (called from event handlers + delete sites) ──────────

    public void OnItemAdded(int entityId, TilePos tile, string path)
    {
        if (_byEntity.ContainsKey(entityId)) return; // idempotent
        _byEntity[entityId] = new Entry(tile, path);
        BumpItemCount(tile, +1);
        if (!_reserved.Contains(entityId)) Bump(_unreservedItemTileCount, tile, +1);
        var key = ChunkOf(tile);
        if (!_chunks.TryGetValue(key, out var list)) { list = new List<int>(); _chunks[key] = list; }
        list.Add(entityId);
    }

    public void OnEntityGone(int entityId)
    {
        if (!_byEntity.TryGetValue(entityId, out var e)) return;
        _byEntity.Remove(entityId);
        BumpItemCount(e.Tile, -1);
        if (!_reserved.Contains(entityId)) Bump(_unreservedItemTileCount, e.Tile, -1);
        _reserved.Remove(entityId);
        var key = ChunkOf(e.Tile);
        if (_chunks.TryGetValue(key, out var list))
        {
            list.Remove(entityId);
            if (list.Count == 0) _chunks.Remove(key);
        }
    }

    // HaulReserved added/removed (mirrored from the component events). A
    // reserved stack no longer wedges a door — a pawn is coming for it.
    public void OnReservedAdded(int entityId)
    {
        if (!_reserved.Add(entityId)) return;
        if (_byEntity.TryGetValue(entityId, out var e))
            Bump(_unreservedItemTileCount, e.Tile, -1);
    }

    public void OnReservedRemoved(int entityId)
    {
        if (!_reserved.Remove(entityId)) return;
        if (_byEntity.TryGetValue(entityId, out var e))
            Bump(_unreservedItemTileCount, e.Tile, +1);
    }

    private static void Bump(Dictionary<TilePos, int> map, TilePos tile, int delta)
    {
        int n = map.GetValueOrDefault(tile) + delta;
        if (n <= 0) map.Remove(tile); else map[tile] = n;
    }

    // _itemTileCount bump that also maintains the coincident-tile counter
    // (tiles crossing the 1<->2 boundary).
    private void BumpItemCount(TilePos tile, int delta)
    {
        int before = _itemTileCount.GetValueOrDefault(tile);
        int after = before + delta;
        if (after <= 0) _itemTileCount.Remove(tile); else _itemTileCount[tile] = after;
        bool wasCo = before >= 2, isCo = after >= 2;
        if (isCo && !wasCo) _coincidentTileCount++;
        else if (wasCo && !isCo) _coincidentTileCount--;
    }

    // True if any tile holds >=2 item stacks (the merge/spill passes are pure
    // no-ops otherwise, so SimRuntime can skip them).
    public bool HasCoincidentTiles => _coincidentTileCount > 0;

    // ── queries ──────────────────────────────────────────────────────────

    // True if any item stack sits on the tile (reserved or not). Used by
    // build-blocking — any dropped item holds a blueprint off the tile.
    public bool AnyItemAt(TilePos tile) => _itemTileCount.ContainsKey(tile);

    // True if an UNRESERVED item sits on the tile — the door-wedge
    // condition (a reserved stack is about to be hauled away).
    public bool AnyUnreservedItemAt(TilePos tile) => _unreservedItemTileCount.ContainsKey(tile);

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
        var liveItem = new Dictionary<TilePos, int>();
        var liveUnreserved = new Dictionary<TilePos, int>();
        (_itemPileQ ??= store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity e) =>
        {
            live[e.Id] = new Entry(p.Tile, p.ItemPath);
            Bump(liveItem, p.Tile, +1);
            if (!e.HasComponent<HaulReserved>()) Bump(liveUnreserved, p.Tile, +1);
        });

        CheckTileMap("item", liveItem, _itemTileCount);
        CheckTileMap("unreserved-item", liveUnreserved, _unreservedItemTileCount);

        int liveCoincident = 0;
        foreach (var n in liveItem.Values) if (n >= 2) liveCoincident++;
        if (liveCoincident != _coincidentTileCount)
            throw new InvalidOperationException($"ItemSpatialIndex drift: coincident tiles index={_coincidentTileCount} store={liveCoincident}.");

        if (live.Count != _byEntity.Count)
            throw new InvalidOperationException($"ItemSpatialIndex drift: index has {_byEntity.Count} entries, store has {live.Count}.");
        foreach (var (id, le) in live)
        {
            if (!_byEntity.TryGetValue(id, out var ie))
                throw new InvalidOperationException($"ItemSpatialIndex drift: entity {id} on tile {le.Tile} missing from index.");
            if (ie.Tile != le.Tile || ie.Path != le.Path)
                throw new InvalidOperationException($"ItemSpatialIndex drift: entity {id} index={ie.Tile}/{ie.Path} store={le.Tile}/{le.Path}.");
        }
    }

    private void CheckTileMap(string label, Dictionary<TilePos, int> live, Dictionary<TilePos, int> index)
    {
        if (live.Count != index.Count)
            throw new InvalidOperationException($"ItemSpatialIndex drift: {label} tiles index={index.Count} store={live.Count}.");
        foreach (var (tile, n) in live)
            if (index.GetValueOrDefault(tile) != n)
                throw new InvalidOperationException($"ItemSpatialIndex drift: {label} at {tile} index={index.GetValueOrDefault(tile)} store={n}.");
    }
}
