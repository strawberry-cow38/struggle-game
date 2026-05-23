using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim.GrowZones;

// Player-painted plot. Mirrors Stockpile shape: an arbitrary tile set
// the manager system scans periodically to schedule farming work.
//
// - CropKind selects what gets sown (when AllowSowing) and what counts
//   as "matching" for the cut filter.
// - AllowCutting auto-posts CutPlants on every non-matching crop and on
//   immature trees within the zone.
// - AllowSowing auto-posts Sow on empty walkable zone tiles.
// - Always-on: auto-post Harvest on matching crops at 100% growth.
//   (Per spec: a zone over a wild carrot patch with both toggles OFF
//   still harvests the carrots once they ripen.)
public sealed class GrowZone
{
    public int Id { get; }
    public string Name { get; set; }
    public CropKind CropKind { get; set; }
    public bool AllowCutting { get; set; }
    public bool AllowSowing { get; set; }
    public HashSet<TilePos> Tiles { get; }

    public GrowZone(int id, string name, CropKind cropKind, IEnumerable<TilePos> tiles)
    {
        Id = id;
        Name = name;
        CropKind = cropKind;
        AllowCutting = false;
        AllowSowing = true;
        Tiles = new HashSet<TilePos>(tiles);
    }
}
