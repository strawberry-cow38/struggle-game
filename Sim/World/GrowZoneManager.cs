using StruggleGame.Sim.GrowZones;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Periodic scan over every grow zone, queuing the farm work the zone is
// asking for. Doesn't run every tick — re-checking N hundred zone tiles
// per tick would be wasted work since plants change state slowly.
//
// Per zone, per tile:
//   - skip if a job already exists on the tile
//   - if a matching mature crop sits here, post Harvest
//   - if AllowCutting and a non-matching crop sits here, post CutPlants
//   - if AllowCutting and an immature tree sits here, post CutPlants
//   - if AllowSowing and the tile is empty + walkable, post Sow
public sealed class GrowZoneManager
{
    public const float ScanIntervalSec = 2.0f;

    private readonly SimRuntime _sim;
    private float _accumSec;

    public GrowZoneManager(SimRuntime sim)
    {
        _sim = sim;
    }

    public void Step(float dt)
    {
        _accumSec += dt;
        if (_accumSec < ScanIntervalSec) return;
        _accumSec = 0f;
        foreach (var zone in _sim.GrowZones) ScanZone(zone);
    }

    private void ScanZone(GrowZone zone)
    {
        foreach (var tile in zone.Tiles)
        {
            if (_sim.Jobs.HasTile(tile)) continue;

            if (_sim.TryGetCrop(tile, out var cropEnt))
            {
                var crop = cropEnt.GetComponent<Crop>();
                float stage = cropEnt.HasComponent<Growth>() ? cropEnt.GetComponent<Growth>().Stage : 0f;
                if (crop.Kind == zone.CropKind)
                {
                    if (stage >= SimRuntime.HarvestMinGrowthStage && stage >= 1f - 1e-4f)
                    {
                        _sim.TryPostHarvestJob(tile);
                    }
                    // Matching crop not yet ripe — leave it alone.
                }
                else if (zone.AllowCutting)
                {
                    _sim.TryPostCutPlantJob(tile);
                }
                continue;
            }

            if (_sim.TryGetTree(tile, out var treeEnt))
            {
                if (!zone.AllowCutting) continue;
                float stage = treeEnt.HasComponent<Growth>() ? treeEnt.GetComponent<Growth>().Stage : 1f;
                if (stage < SimRuntime.ChopMinGrowthStage)
                {
                    _sim.TryPostCutPlantJob(tile);
                }
                continue;
            }

            // Empty tile.
            if (zone.AllowSowing && _sim.IsSowable(tile))
            {
                _sim.TryPostSowJob(tile, zone.CropKind);
            }
        }
    }
}
