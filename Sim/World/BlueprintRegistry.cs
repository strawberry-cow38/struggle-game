using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Tile→Entity index for active blueprints, kept in sync with adds/removes
// in SimRuntime. Lookup by tile is hot path during builder targeting.
public sealed class BlueprintRegistry
{
    private readonly Dictionary<TilePos, Entity> _byTile = new();

    public int Count => _byTile.Count;
    public IReadOnlyCollection<TilePos> Tiles => _byTile.Keys;

    public bool Has(TilePos tile) => _byTile.ContainsKey(tile);

    public bool TryGet(TilePos tile, out Entity entity)
    {
        if (_byTile.TryGetValue(tile, out var e))
        {
            entity = e;
            return true;
        }
        entity = default;
        return false;
    }

    public void Add(TilePos tile, Entity entity) => _byTile[tile] = entity;
    public void Remove(TilePos tile) => _byTile.Remove(tile);

    public IEnumerable<KeyValuePair<TilePos, Entity>> All => _byTile;
}
