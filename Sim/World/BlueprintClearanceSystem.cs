using Friflo.Engine.ECS;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick poster for "get this stack off the blueprint tile" hauls.
// Walls, doors, and beds all bury whatever's sitting on their tile when
// they finish, so we relocate any Wood stack to the nearest walkable
// non-blueprint tile before building can proceed. Floors and lamps are
// intentionally excluded — a floor under a wood pile is harmless, and
// lamps don't change tile geometry.
//
// Runs ahead of BlueprintHaulSystem so the relocate-haul gets a chance
// to reserve the wood before the blueprint funding pass tries to suck
// the same stack into itself. BlueprintHaulSystem will only see wood
// without HaulReserved set.
public sealed class BlueprintClearanceSystem
{
    private readonly SimRuntime _sim;
    private readonly JobBoard _jobs;

    private readonly HashSet<TilePos> _blockingBpTiles = new();
    private readonly HashSet<TilePos> _itemTiles = new();
    private readonly List<(Entity Ent, TilePos Tile, int Count, string Path)> _candidates = new();

    // Ring search bounds for "nearest safe tile". Generous enough that
    // a moderately cluttered base will always find a drop spot; bail
    // beyond that rather than searching the whole map.
    private const int MaxRelocateRadius = 10;

    private ArchetypeQuery<Blueprint>? _blueprintQ;
    private ArchetypeQuery<DoorBlueprint>? _doorBlueprintQ;
    private ArchetypeQuery<BedBlueprint>? _bedBlueprintQ;
    private ArchetypeQuery<ItemPile>? _itemPileQ;

    public BlueprintClearanceSystem(SimRuntime sim, JobBoard jobs)
    {
        _sim = sim;
        _jobs = jobs;
    }

    public void Step(EntityStore store, float dt)
    {
        _blockingBpTiles.Clear();
        _itemTiles.Clear();

        (_blueprintQ ??= store.Query<Blueprint>()).ForEachEntity((ref Blueprint bp, Entity _) =>
        {
            _blockingBpTiles.Add(bp.Tile);
        });
        (_doorBlueprintQ ??= store.Query<DoorBlueprint>()).ForEachEntity((ref DoorBlueprint bp, Entity _) =>
        {
            _blockingBpTiles.Add(bp.Tile);
        });
        (_bedBlueprintQ ??= store.Query<BedBlueprint>()).ForEachEntity((ref BedBlueprint bp, Entity _) =>
        {
            _blockingBpTiles.Add(bp.Origin);
            _blockingBpTiles.Add(BedOrientations.Foot(bp.Origin, bp.Orientation));
        });
        if (_blockingBpTiles.Count == 0) return;

        // Tiles holding any item stack, used by the safe-tile scorer to
        // prefer empty tiles over ones that'd cause an immediate merge.
        (_itemPileQ ??= store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity _) =>
        {
            _itemTiles.Add(p.Tile);
        });

        var view = _sim.MapView;

        // Any dropped item on a blueprint tile must move — relocate it,
        // carrying its own kind.
        _candidates.Clear();
        (_itemPileQ ??= store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity ent) =>
        {
            if (ent.HasComponent<HaulReserved>()) return;
            if (ent.HasComponent<Forbidden>()) return;
            if (!_blockingBpTiles.Contains(p.Tile)) return;
            _candidates.Add((ent, p.Tile, p.Count, p.ItemPath));
        });

        // Mutate outside the query loop — Friflo throws
        // StructuralChangeException on AddComponent inside ForEachEntity.
        foreach (var c in _candidates)
        {
            if (!TryFindSafeTile(view, c.Tile, out var safe)) continue;

            c.Ent.AddComponent(new HaulPayload
            {
                DestTile = safe,
                StockpileId = 0,
                ItemPath = c.Path,
                Count = c.Count,
                BlueprintEntityId = 0,
            });
            // skipTileIndex: the build job already owns the byTile slot
            // for this tile. The clearance haul's lifecycle is owned by
            // the wood entity's HaulReserved JobId, so no byTile lookup
            // is needed.
            var id = _jobs.Post(JobKind.Haul, c.Tile, c.Ent, null, skipTileIndex: true);
            if (id.IsNone)
            {
                c.Ent.RemoveComponent<HaulPayload>();
                continue;
            }
            c.Ent.AddComponent(new HaulReserved { JobId = id });
            _sim.ReserveHaulDest(safe);
            _itemTiles.Add(safe);
        }
    }

    private bool TryFindSafeTile(MapView view, TilePos source, out TilePos safe)
    {
        safe = default;
        for (int r = 1; r <= MaxRelocateRadius; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    // Only the ring of radius r — skip interior cells
                    // already covered by smaller rings.
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    var t = new TilePos(source.X + dx, source.Y + dy);
                    if (!view.InBounds(t)) continue;
                    if (!view.Walkable(t)) continue;
                    if (_blockingBpTiles.Contains(t)) continue;
                    if (_sim.IsHaulDestReserved(t)) continue;
                    if (_itemTiles.Contains(t)) continue;
                    safe = t;
                    return true;
                }
            }
        }
        return false;
    }
}
