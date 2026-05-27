using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Stockpiles;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim.Commands;

// Game→Sim commands. Game thread enqueues; Sim thread drains at the
// start of every tick. Keep commands tiny + value-typed so there's no
// shared mutable state.
public interface ISimCommand
{
    void Apply(SimRuntime sim);
}

public sealed class PlaceWallBlueprintCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PlaceWallBlueprintCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPlaceWallBlueprint(Tile);
}

// Harness/debug shortcut: drop a finished wall onto the tile with no
// blueprint or build job. Used by visual harness scenarios so the
// scene reaches its target topology in a single tick.
public sealed class InstantPlaceWallCommand : ISimCommand
{
    public TilePos Tile { get; }
    public InstantPlaceWallCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.InstantPlaceWall(Tile);
}

public sealed class InstantPlaceDoorCommand : ISimCommand
{
    public TilePos Tile { get; }
    public InstantPlaceDoorCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.InstantPlaceDoor(Tile);
}

public sealed class InstantPlaceLampCommand : ISimCommand
{
    public TilePos Tile { get; }
    public LightColor Color { get; }
    public InstantPlaceLampCommand(TilePos tile) : this(tile, LightColor.White) { }
    public InstantPlaceLampCommand(TilePos tile, LightColor color) { Tile = tile; Color = color; }
    public void Apply(SimRuntime sim) => sim.InstantPlaceLamp(Tile, Color);
}

public sealed class InstantPaintRoofRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public InstantPaintRoofRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim) => sim.InstantPaintRoofRect(A, B);
}

public sealed class SetWorldTimeCommand : ISimCommand
{
    public double Seconds { get; }
    public SetWorldTimeCommand(double seconds) { Seconds = seconds; }
    public void Apply(SimRuntime sim) => sim.SetWorldTime(Seconds);
}

// Single-tile door designation. Orientation is derived inside
// TryPlaceDoorBlueprint from the flanking wall layout.
public sealed class PlaceDoorBlueprintCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PlaceDoorBlueprintCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPlaceDoorBlueprint(Tile);
}

// Single-tile lamp designation. Lamp doesn't block walking; placement
// rejects walls / doors / trees / existing lamps / other jobs. Color
// defaults to white; the info panel's picker can recolor a built lamp
// or the designator can pre-set it.
public sealed class PlaceLampBlueprintCommand : ISimCommand
{
    public TilePos Tile { get; }
    public LightColor Color { get; }
    public PlaceLampBlueprintCommand(TilePos tile) : this(tile, LightColor.White) { }
    public PlaceLampBlueprintCommand(TilePos tile, LightColor color) { Tile = tile; Color = color; }
    public void Apply(SimRuntime sim) => sim.TryPlaceLampBlueprint(Tile, Color);
}

// Lamp info panel → "Power" cheat toggle. Stubs the absent power
// network: lamps emit light iff PoweredOn is true.
public sealed class SetLampPoweredCommand : ISimCommand
{
    public TilePos Tile { get; }
    public bool On { get; }
    public SetLampPoweredCommand(TilePos tile, bool on) { Tile = tile; On = on; }
    public void Apply(SimRuntime sim) => sim.SetLampPowered(Tile, On);
}

// Lamp info panel → color picker. Recolors a built lamp; recompute
// re-runs so emission tint updates immediately.
public sealed class SetLampColorCommand : ISimCommand
{
    public TilePos Tile { get; }
    public LightColor Color { get; }
    public SetLampColorCommand(TilePos tile, LightColor color) { Tile = tile; Color = color; }
    public void Apply(SimRuntime sim) => sim.SetLampColor(Tile, Color);
}

// Lamp info panel → "Deconstruct" button. Single-tile sibling of the
// drag-rect decon.
public sealed class PostLampDeconCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PostLampDeconCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPostLampDeconstructJob(Tile);
}

// Drag-rect wood-floor designation. Posts one FloorBuild blueprint per
// tile in the rect that's eligible (no wall, no existing wood floor,
// no other job).
public sealed class FloorRectBlueprintCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public FloorRectBlueprintCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim)
    {
        int xmin = Math.Min(A.X, B.X), xmax = Math.Max(A.X, B.X);
        int ymin = Math.Min(A.Y, B.Y), ymax = Math.Max(A.Y, B.Y);
        for (int y = ymin; y <= ymax; y++)
            for (int x = xmin; x <= xmax; x++)
                sim.TryPlaceFloorBlueprint(new TilePos(x, y));
    }
}

// Cancel every job whose tile lies in the inclusive rect. Used by the
// drag-rect cancel designator. Currently affects WallBuild jobs; future
// kinds get whatever cancel semantics they need.
public sealed class CancelJobsInRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public CancelJobsInRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim)
    {
        var ids = new List<Jobs.JobId>();
        foreach (var job in sim.Jobs.InRect(A.X, A.Y, B.X, B.Y))
        {
            ids.Add(job.Id);
        }
        foreach (var id in ids) sim.CancelJob(id);
    }
}

// Toggle the Drafted marker on a colonist. Drafting also releases any
// build assignment, clears the current path, and discards in-flight
// path requests so the pawn is immediately under player control.
// Un-drafting drops the OrderQueue.
public sealed class ToggleDraftCommand : ISimCommand
{
    public int EntityId { get; }
    public ToggleDraftCommand(int entityId) { EntityId = entityId; }

    public void Apply(SimRuntime sim)
    {
        if (!sim.Store.TryGetEntityById(EntityId, out var ent)) return;

        if (ent.HasComponent<Drafted>())
        {
            ent.RemoveComponent<Drafted>();
            if (ent.HasComponent<OrderQueue>()) ent.RemoveComponent<OrderQueue>();
            if (ent.HasComponent<PathFollower>())
            {
                ref var pf = ref ent.GetComponent<PathFollower>();
                if (pf.PendingPathId != 0) sim.PathService.Discard(pf.PendingPathId);
                pf.PendingPathId = 0;
                pf.Waypoints = null;
                pf.Index = 0;
            }
            return;
        }

        ent.AddComponent(new Drafted());
        if (ent.HasComponent<BuildTarget>())
        {
            var bt = ent.GetComponent<BuildTarget>();
            // Mid-haul carriers drop every carried + reserved item at
            // their current tile so the wood doesn't vanish when the
            // draft toggles.
            if (ent.HasComponent<Carrying>())
            {
                TilePos here;
                if (ent.HasComponent<WorldPos>())
                {
                    var wp = ent.GetComponent<WorldPos>();
                    here = new TilePos((int)wp.X, (int)wp.Y);
                }
                else
                {
                    here = ent.GetComponent<Carrying>().DestTile;
                }
                var cb = sim.Store.GetCommandBuffer();
                sim.DeliverCarrying(ent, here, cb);
                cb.Playback();
            }
            else
            {
                sim.Jobs.Release(bt.JobId);
            }
            ent.RemoveComponent<BuildTarget>();
        }
        if (ent.HasComponent<PathFollower>())
        {
            ref var pf = ref ent.GetComponent<PathFollower>();
            if (pf.PendingPathId != 0) sim.PathService.Discard(pf.PendingPathId);
            pf.PendingPathId = 0;
            pf.Waypoints = null;
            pf.Index = 0;
        }
    }
}

// Append=false replaces the queue; append=true tails onto it. Only
// applies if the target entity is currently Drafted.
public sealed class IssueMoveOrderCommand : ISimCommand
{
    public int EntityId { get; }
    public TilePos Tile { get; }
    public bool Append { get; }
    public IssueMoveOrderCommand(int entityId, TilePos tile, bool append)
    {
        EntityId = entityId;
        Tile = tile;
        Append = append;
    }

    public void Apply(SimRuntime sim)
    {
        if (!sim.Store.TryGetEntityById(EntityId, out var ent)) return;
        if (!ent.HasComponent<Drafted>()) return;
        if (!sim.MapView.Walkable(Tile)) return;

        if (!ent.HasComponent<OrderQueue>())
        {
            ent.AddComponent(new OrderQueue { Tiles = new List<TilePos>() });
        }
        ref var oq = ref ent.GetComponent<OrderQueue>();
        oq.Tiles ??= new List<TilePos>();

        if (!Append)
        {
            oq.Tiles.Clear();
            if (ent.HasComponent<PathFollower>())
            {
                ref var pf = ref ent.GetComponent<PathFollower>();
                if (pf.PendingPathId != 0) sim.PathService.Discard(pf.PendingPathId);
                pf.PendingPathId = 0;
                pf.Waypoints = null;
                pf.Index = 0;
            }
        }
        oq.Tiles.Add(Tile);
    }
}

// Post a deconstruct job on every player-built wall AND door whose tile
// lies in the inclusive rect. Walls from procgen / map border are
// excluded; doors are always player-placed so all of them are eligible.
public sealed class DeconstructWallsInRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public DeconstructWallsInRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim)
    {
        int xmin = Math.Min(A.X, B.X), xmax = Math.Max(A.X, B.X);
        int ymin = Math.Min(A.Y, B.Y), ymax = Math.Max(A.Y, B.Y);
        // Snapshot the wall list so removals during iteration are safe.
        var walls = sim.PlayerWalls;
        var wallHits = new List<TilePos>();
        foreach (var tile in walls)
        {
            if (tile.X < xmin || tile.X > xmax) continue;
            if (tile.Y < ymin || tile.Y > ymax) continue;
            wallHits.Add(tile);
        }
        var doorHits = new List<TilePos>();
        foreach (var tile in sim.DoorTiles)
        {
            if (tile.X < xmin || tile.X > xmax) continue;
            if (tile.Y < ymin || tile.Y > ymax) continue;
            doorHits.Add(tile);
        }
        var lampHits = new List<TilePos>();
        foreach (var tile in sim.LampTiles)
        {
            if (tile.X < xmin || tile.X > xmax) continue;
            if (tile.Y < ymin || tile.Y > ymax) continue;
            lampHits.Add(tile);
        }
        foreach (var tile in wallHits) sim.TryPostDeconstructJob(tile);
        foreach (var tile in doorHits) sim.TryPostDoorDeconstructJob(tile);
        foreach (var tile in lampHits) sim.TryPostLampDeconstructJob(tile);
    }
}

// Post a FloorDeconstruct job on every wood-floor tile in the rect.
// Tiles hidden under a wall are skipped (wall must be decon'd first).
public sealed class DeconstructFloorsInRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public DeconstructFloorsInRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim)
    {
        int xmin = Math.Min(A.X, B.X), xmax = Math.Max(A.X, B.X);
        int ymin = Math.Min(A.Y, B.Y), ymax = Math.Max(A.Y, B.Y);
        for (int y = ymin; y <= ymax; y++)
            for (int x = xmin; x <= xmax; x++)
                sim.TryPostFloorDeconJob(new TilePos(x, y));
    }
}

// Post chop jobs on every mature tree (≥50% growth) whose tile lies in
// the inclusive rect. Immature trees fall through to CutPlants instead.
public sealed class ChopTreesInRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public ChopTreesInRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim)
    {
        int xmin = Math.Min(A.X, B.X), xmax = Math.Max(A.X, B.X);
        int ymin = Math.Min(A.Y, B.Y), ymax = Math.Max(A.Y, B.Y);
        foreach (var tile in sim.TreeTiles)
        {
            if (tile.X < xmin || tile.X > xmax) continue;
            if (tile.Y < ymin || tile.Y > ymax) continue;
            sim.TryPostChopJob(tile);
        }
    }
}

// Post CutPlants jobs across the rect. Targets immature trees (<50%)
// and crops at any growth stage.
public sealed class CutPlantsInRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public CutPlantsInRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim)
    {
        int xmin = Math.Min(A.X, B.X), xmax = Math.Max(A.X, B.X);
        int ymin = Math.Min(A.Y, B.Y), ymax = Math.Max(A.Y, B.Y);
        foreach (var tile in sim.TreeTiles)
        {
            if (tile.X < xmin || tile.X > xmax) continue;
            if (tile.Y < ymin || tile.Y > ymax) continue;
            sim.TryPostCutPlantJob(tile);
        }
        foreach (var tile in sim.CropTiles)
        {
            if (tile.X < xmin || tile.X > xmax) continue;
            if (tile.Y < ymin || tile.Y > ymax) continue;
            sim.TryPostCutPlantJob(tile);
        }
    }
}

// Post Harvest jobs on every crop in the rect that's at ≥75% growth.
public sealed class HarvestInRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public HarvestInRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim)
    {
        int xmin = Math.Min(A.X, B.X), xmax = Math.Max(A.X, B.X);
        int ymin = Math.Min(A.Y, B.Y), ymax = Math.Max(A.Y, B.Y);
        foreach (var tile in sim.CropTiles)
        {
            if (tile.X < xmin || tile.X > xmax) continue;
            if (tile.Y < ymin || tile.Y > ymax) continue;
            sim.TryPostHarvestJob(tile);
        }
    }
}

// Create a new stockpile zone from the inclusive rect [A..B]. Tiles
// already claimed by another zone are skipped — zones don't overlap.
public sealed class CreateStockpileRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public CreateStockpileRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim) => sim.CreateStockpileRect(A, B);
}

// Add the free tiles of [A..B] to an existing zone (compound shape).
public sealed class ExpandStockpileRectCommand : ISimCommand
{
    public int StockpileId { get; }
    public TilePos A { get; }
    public TilePos B { get; }
    public ExpandStockpileRectCommand(int id, TilePos a, TilePos b)
    { StockpileId = id; A = a; B = b; }
    public void Apply(SimRuntime sim) => sim.ExpandStockpileRect(StockpileId, A, B);
}

// Subtract the tiles of [A..B] from an existing zone (shrink).
public sealed class ShrinkStockpileRectCommand : ISimCommand
{
    public int StockpileId { get; }
    public TilePos A { get; }
    public TilePos B { get; }
    public ShrinkStockpileRectCommand(int id, TilePos a, TilePos b)
    { StockpileId = id; A = a; B = b; }
    public void Apply(SimRuntime sim) => sim.ShrinkStockpileRect(StockpileId, A, B);
}

public sealed class DeleteStockpileCommand : ISimCommand
{
    public int StockpileId { get; }
    public DeleteStockpileCommand(int id) { StockpileId = id; }
    public void Apply(SimRuntime sim) => sim.DeleteStockpile(StockpileId);
}

public sealed class RenameStockpileCommand : ISimCommand
{
    public int StockpileId { get; }
    public string Name { get; }
    public RenameStockpileCommand(int id, string name) { StockpileId = id; Name = name; }
    public void Apply(SimRuntime sim) => sim.RenameStockpile(StockpileId, Name);
}

public sealed class SetStockpilePriorityCommand : ISimCommand
{
    public int StockpileId { get; }
    public StockpilePriority Priority { get; }
    public SetStockpilePriorityCommand(int id, StockpilePriority p) { StockpileId = id; Priority = p; }
    public void Apply(SimRuntime sim) => sim.SetStockpilePriority(StockpileId, Priority);
}

public sealed class SetStockpileItemAllowedCommand : ISimCommand
{
    public int StockpileId { get; }
    public string ItemPath { get; }
    public bool Allowed { get; }
    public SetStockpileItemAllowedCommand(int id, string itemPath, bool allowed)
    { StockpileId = id; ItemPath = itemPath; Allowed = allowed; }
    public void Apply(SimRuntime sim) => sim.SetStockpileItemAllowed(StockpileId, ItemPath, Allowed);
}

public sealed class SetStockpileCategoryAllowedCommand : ISimCommand
{
    public int StockpileId { get; }
    public string CategoryPath { get; }
    public bool Allowed { get; }
    public SetStockpileCategoryAllowedCommand(int id, string categoryPath, bool allowed)
    { StockpileId = id; CategoryPath = categoryPath; Allowed = allowed; }
    public void Apply(SimRuntime sim) => sim.SetStockpileCategoryAllowed(StockpileId, CategoryPath, Allowed);
}

// === Grow zone commands ===
public sealed class CreateGrowZoneRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public CropKind CropKind { get; }
    public CreateGrowZoneRectCommand(TilePos a, TilePos b, CropKind kind)
    { A = a; B = b; CropKind = kind; }
    public void Apply(SimRuntime sim) => sim.CreateGrowZoneRect(A, B, CropKind);
}

public sealed class ExpandGrowZoneRectCommand : ISimCommand
{
    public int ZoneId { get; }
    public TilePos A { get; }
    public TilePos B { get; }
    public ExpandGrowZoneRectCommand(int id, TilePos a, TilePos b)
    { ZoneId = id; A = a; B = b; }
    public void Apply(SimRuntime sim) => sim.ExpandGrowZoneRect(ZoneId, A, B);
}

public sealed class ShrinkGrowZoneRectCommand : ISimCommand
{
    public int ZoneId { get; }
    public TilePos A { get; }
    public TilePos B { get; }
    public ShrinkGrowZoneRectCommand(int id, TilePos a, TilePos b)
    { ZoneId = id; A = a; B = b; }
    public void Apply(SimRuntime sim) => sim.ShrinkGrowZoneRect(ZoneId, A, B);
}

public sealed class DeleteGrowZoneCommand : ISimCommand
{
    public int ZoneId { get; }
    public DeleteGrowZoneCommand(int id) { ZoneId = id; }
    public void Apply(SimRuntime sim) => sim.DeleteGrowZone(ZoneId);
}

public sealed class RenameGrowZoneCommand : ISimCommand
{
    public int ZoneId { get; }
    public string Name { get; }
    public RenameGrowZoneCommand(int id, string name) { ZoneId = id; Name = name; }
    public void Apply(SimRuntime sim) => sim.RenameGrowZone(ZoneId, Name);
}

public sealed class SetGrowZoneCropKindCommand : ISimCommand
{
    public int ZoneId { get; }
    public CropKind CropKind { get; }
    public SetGrowZoneCropKindCommand(int id, CropKind kind) { ZoneId = id; CropKind = kind; }
    public void Apply(SimRuntime sim) => sim.SetGrowZoneCropKind(ZoneId, CropKind);
}

public sealed class SetGrowZoneAllowCuttingCommand : ISimCommand
{
    public int ZoneId { get; }
    public bool Allowed { get; }
    public SetGrowZoneAllowCuttingCommand(int id, bool allowed) { ZoneId = id; Allowed = allowed; }
    public void Apply(SimRuntime sim) => sim.SetGrowZoneAllowCutting(ZoneId, Allowed);
}

public sealed class SetGrowZoneAllowSowingCommand : ISimCommand
{
    public int ZoneId { get; }
    public bool Allowed { get; }
    public SetGrowZoneAllowSowingCommand(int id, bool allowed) { ZoneId = id; Allowed = allowed; }
    public void Apply(SimRuntime sim) => sim.SetGrowZoneAllowSowing(ZoneId, Allowed);
}

// Toggle the Forbidden marker on a world item stack. Cancels any
// in-flight haul referencing it so the carrier drops its cargo and
// the poster won't re-claim it until the player un-forbids.
public sealed class ForbidStackCommand : ISimCommand
{
    public int EntityId { get; }
    public bool Forbidden { get; }
    public ForbidStackCommand(int entityId, bool forbidden)
    {
        EntityId = entityId;
        Forbidden = forbidden;
    }
    public void Apply(SimRuntime sim) => sim.SetItemForbidden(EntityId, Forbidden);
}

// Post a single Deconstruct job on a player-built wall tile. Sourced
// from the wall info panel's button (not the rect designator).
public sealed class PostWallDeconCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PostWallDeconCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPostDeconstructJob(Tile);
}

// Post a single DoorDeconstruct job on a built door tile. Sourced from
// the door info panel's button.
public sealed class PostDoorDeconCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PostDoorDeconCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPostDoorDeconstructJob(Tile);
}

// Toggle Forbidden on the door at this tile. Forbidden = pathing
// treats it as a wall and the door refuses to open.
public sealed class SetDoorForbiddenCommand : ISimCommand
{
    public TilePos Tile { get; }
    public bool Forbidden { get; }
    public SetDoorForbiddenCommand(TilePos tile, bool forbidden)
    { Tile = tile; Forbidden = forbidden; }
    public void Apply(SimRuntime sim) => sim.SetDoorForbidden(Tile, Forbidden);
}

// Toggle Locked on the door. Stub flag — no enemy code reads it yet.
public sealed class SetDoorLockedCommand : ISimCommand
{
    public TilePos Tile { get; }
    public bool Locked { get; }
    public SetDoorLockedCommand(TilePos tile, bool locked)
    { Tile = tile; Locked = locked; }
    public void Apply(SimRuntime sim) => sim.SetDoorLocked(Tile, Locked);
}

// Set the door's per-priority traversal weight. Cycled via the door
// info panel; A* + the mover see the change on the next tick.
public sealed class SetDoorPriorityCommand : ISimCommand
{
    public TilePos Tile { get; }
    public DoorPriority Priority { get; }
    public SetDoorPriorityCommand(TilePos tile, DoorPriority priority)
    { Tile = tile; Priority = priority; }
    public void Apply(SimRuntime sim) => sim.SetDoorPriority(Tile, Priority);
}

// Toggle Forbidden on a queued build / decon / chop / haul job by tile.
// Sourced from the blueprint info panel's Forbid toggle.
public sealed class SetJobForbiddenCommand : ISimCommand
{
    public TilePos Tile { get; }
    public bool Forbidden { get; }
    public SetJobForbiddenCommand(TilePos tile, bool forbidden)
    { Tile = tile; Forbidden = forbidden; }
    public void Apply(SimRuntime sim) => sim.SetJobForbidden(Tile, Forbidden);
}

// Cancel a queued job by tile. Sourced from the blueprint info panel's
// Cancel button — single-tile sibling of CancelJobsInRectCommand.
public sealed class CancelJobAtTileCommand : ISimCommand
{
    public TilePos Tile { get; }
    public CancelJobAtTileCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.CancelJobAtTile(Tile);
}

// Pawn info panel: drop one inventory slot at the pawn's current
// tile. Force-drop bypasses the per-slot Forbidden flag — this is the
// explicit player escape hatch for items the AI has been told to keep.
public sealed class ForceDropInventorySlotCommand : ISimCommand
{
    public int CarrierEntityId { get; }
    public int SlotEntityId { get; }
    public ForceDropInventorySlotCommand(int carrierId, int slotId)
    {
        CarrierEntityId = carrierId;
        SlotEntityId = slotId;
    }
    public void Apply(SimRuntime sim) => sim.ForceDropInventorySlot(CarrierEntityId, SlotEntityId);
}

// Pawn info panel: toggle the Forbidden flag on one inventory slot.
// Forbidden slots are never auto-dropped on delivery / draft / abort
// and are never used as material for any job.
public sealed class SetInventorySlotForbiddenCommand : ISimCommand
{
    public int CarrierEntityId { get; }
    public int SlotEntityId { get; }
    public bool Forbidden { get; }
    public SetInventorySlotForbiddenCommand(int carrierId, int slotId, bool forbidden)
    {
        CarrierEntityId = carrierId;
        SlotEntityId = slotId;
        Forbidden = forbidden;
    }
    public void Apply(SimRuntime sim) => sim.SetInventorySlotForbidden(CarrierEntityId, SlotEntityId, Forbidden);
}

// === Roof commands ===
// Drag-rect "build roof" — sets every tile in [A..B] to roofed (unless
// the tile is marked no-roof). Instant for now; build jobs ship later.
public sealed class PaintRoofRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public PaintRoofRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim) => sim.PaintRoofRect(A, B);
}

// Drag-rect "remove roof". Strips the roof flag across the rect; the
// no-roof mark is untouched.
public sealed class RemoveRoofRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public RemoveRoofRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim) => sim.RemoveRoofRect(A, B);
}

// Drag-rect "no-roof" toggle. Mark=true sets the no-roof flag AND
// strips any roof in the rect. Mark=false clears the flag and lets
// auto-roof reclaim the area on the next room recompute.
public sealed class SetNoRoofRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public bool Mark { get; }
    public SetNoRoofRectCommand(TilePos a, TilePos b, bool mark)
    { A = a; B = b; Mark = mark; }
    public void Apply(SimRuntime sim) => sim.SetNoRoofRect(A, B, Mark);
}

// Debug bar action: spawn a fresh wanderer at a random walkable tile.
public sealed class SpawnDummyCommand : ISimCommand
{
    public void Apply(SimRuntime sim) => sim.SpawnRandomDummy();
}

// Debug bar action: shift world time by N in-sim seconds. Negative
// rewinds. Buttons hand in ±3600 for the ±1hr controls.
public sealed class AdvanceWorldTimeCommand : ISimCommand
{
    public double DeltaSec { get; }
    public AdvanceWorldTimeCommand(double deltaSec) { DeltaSec = deltaSec; }
    public void Apply(SimRuntime sim) => sim.AdvanceWorldTime(DeltaSec);
}

// Debug bar action: delete a wanderer by entity id (point-and-click).
public sealed class RemoveDummyCommand : ISimCommand
{
    public int EntityId { get; }
    public RemoveDummyCommand(int entityId) { EntityId = entityId; }
    public void Apply(SimRuntime sim) => sim.RemoveDummy(EntityId);
}
