using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.Stockpiles;

// Player-set haul preference. Higher wins when multiple zones could
// accept the same item — pawns deliver into Critical before High and
// so on. Ordering is enum value so callers can compare with > / <.
public enum StockpilePriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

// A named zone composed of an arbitrary set of tiles (rectangular at
// first, compound once expand/shrink ships in phase 5). Holds the
// per-zone haul filter as a set of ItemDef.FullPath strings — paths
// (not ItemDef refs) so a future serialize/reload survives a process
// restart of the static catalog.
public sealed class Stockpile
{
    public int Id { get; }
    public string Name { get; set; }
    public StockpilePriority Priority { get; set; }
    public HashSet<TilePos> Tiles { get; }
    public HashSet<string> AllowedItemPaths { get; }

    public Stockpile(int id, string name, StockpilePriority priority, IEnumerable<TilePos> tiles)
    {
        Id = id;
        Name = name;
        Priority = priority;
        Tiles = new HashSet<TilePos>(tiles);
        AllowedItemPaths = new HashSet<string>();
        // Default-allow everything in the catalog — players opt OUT
        // of categories from the tweak panel.
        foreach (var def in ItemCatalog.ItemsByPath.Values) AllowedItemPaths.Add(def.FullPath);
    }

    // True if the zone is willing to store this item.
    public bool Allows(ItemDef def) => AllowedItemPaths.Contains(def.FullPath);

    // Toggle by category (whole subtree) — used by stockpile panel's
    // "Resources" checkbox to flip every item under it at once.
    public void SetCategoryAllowed(ItemCategory category, bool allowed)
    {
        foreach (var def in ItemCatalog.ItemsByPath.Values)
        {
            if (!ItemCatalog.IsUnder(def, category)) continue;
            if (allowed) AllowedItemPaths.Add(def.FullPath);
            else AllowedItemPaths.Remove(def.FullPath);
        }
    }
}
