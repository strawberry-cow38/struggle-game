using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Stockpiles;
using StruggleGame.Sim.Work;
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

// Debug: drop a hostile at a tile (spirals to nearest walkable).
public sealed class SpawnEnemyCommand : ISimCommand
{
    public TilePos Tile { get; }
    public SpawnEnemyCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.SpawnEnemy(Tile.X, Tile.Y);
}

// Debug: drop a hostile just inside a random map edge (raid entry).
public sealed class SpawnEnemyAtEdgeCommand : ISimCommand
{
    public void Apply(SimRuntime sim) => sim.SpawnEnemyAtEdge();
}

// Debug: drop a raider at a random map edge running the demo raid mission
// (advance to centre → hold → exfil), to watch the goal-queue lifecycle on
// the overhead label.
public sealed class SpawnRaiderCommand : ISimCommand
{
    public void Apply(SimRuntime sim) => sim.SpawnEnemyAtEdge(SimRuntime.RaiderMission());
}

// Spawn a raid: a group of `Count` raiders at one map edge + a notification.
public sealed class TriggerRaidCommand : ISimCommand
{
    public int Count { get; }
    public TriggerRaidCommand(int count) { Count = count; }
    public void Apply(SimRuntime sim) => sim.SpawnRaid(Count);
}

// Toggle global "fire at will" — when off, drafted colonists only fire at
// player-forced (RMB) targets, no auto-acquire/peek.
public sealed class SetFireAtWillCommand : ISimCommand
{
    public bool On { get; }
    public SetFireAtWillCommand(bool on) { On = on; }
    public void Apply(SimRuntime sim) => sim.SetFireAtWill(On);
}

// Clear a player notification once the UI has shown + dismissed it.
public sealed class DismissNotificationCommand : ISimCommand
{
    public int Id { get; }
    public DismissNotificationCommand(int id) { Id = id; }
    public void Apply(SimRuntime sim) => sim.DismissNotification(Id);
}

public sealed class InstantPlaceLampCommand : ISimCommand
{
    public TilePos Tile { get; }
    public LightColor Color { get; }
    public InstantPlaceLampCommand(TilePos tile) : this(tile, LightColor.White) { }
    public InstantPlaceLampCommand(TilePos tile, LightColor color) { Tile = tile; Color = color; }
    public void Apply(SimRuntime sim) => sim.InstantPlaceLamp(Tile, Color);
}

public sealed class InstantPlaceUrBoardCommand : ISimCommand
{
    public TilePos Tile { get; }
    public InstantPlaceUrBoardCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.InstantPlaceUrBoard(Tile);
}

public sealed class InstantPlaceSandbagCommand : ISimCommand
{
    public TilePos Tile { get; }
    public InstantPlaceSandbagCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.InstantPlaceSandbag(Tile);
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
            if (ent.HasComponent<MeleeTarget>()) ent.RemoveComponent<MeleeTarget>();
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

        // Can't draft a downed colonist — they're unconscious.
        if (ent.HasComponent<Health>() && ent.GetComponent<Health>().Unconscious) return;

        ent.AddComponent(new Drafted());
        // Drafting a sleeper wakes them: release the in-flight bed
        // reservation, drop the Sleeping marker so SleepSystem stops
        // refilling. The pawn is now under player control.
        if (ent.HasComponent<Sleeping>())
        {
            var s = ent.GetComponent<Sleeping>();
            if (s.BedEntityId != 0) sim.ReleaseBedReservation(s.BedEntityId, ent.Id);
            ent.RemoveComponent<Sleeping>();
        }
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
        if (sim.MapView.HasFurniture(Tile)) return;

        // A move order cancels any melee attack or treatment order.
        if (ent.HasComponent<MeleeTarget>()) ent.RemoveComponent<MeleeTarget>();
        if (ent.HasComponent<TreatmentTarget>()) ent.RemoveComponent<TreatmentTarget>();
        // ...and any ranged fire order (keeps the weapon + mag).
        if (ent.HasComponent<RangedCombat>())
        {
            ref var rc = ref ent.GetComponent<RangedCombat>();
            rc.TargetEntityId = 0; rc.BurstRemaining = 0;
            // A fresh move order aborts an in-progress reload so the pawn can
            // reposition now. The mag only fills on reload COMPLETION, so the
            // dropped mag stays empty — no free instant reload from interrupting.
            rc.Reloading = false;
        }

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
//
// Multi-tile buildings (stove, bed, ur board) decon as a whole if ANY
// footprint tile overlaps the rect — you don't have to box the origin.
// The stove's standing/interact tile is NOT a footprint tile, so brushing
// only that tile does not trigger a decon.
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
        // Multi-tile buildings: hit the whole thing if any footprint tile
        // overlaps the rect. Collect keys first so TryPost*'s map mutation
        // can't disturb iteration.
        bool InRect(TilePos t) =>
            t.X >= xmin && t.X <= xmax && t.Y >= ymin && t.Y <= ymax;

        var stoveHits = new List<TilePos>();
        foreach (var (origin, stoveEnt) in sim.StoveMap)
        {
            var s = stoveEnt.GetComponent<Stove>();
            foreach (var t in StoveOrientations.BodyTiles(s.Origin, s.Orientation))
            {
                if (InRect(t)) { stoveHits.Add(origin); break; }
            }
        }
        var bedHits = new List<TilePos>();
        foreach (var (origin, bedEnt) in sim.BedMap)
        {
            var b = bedEnt.GetComponent<Bed>();
            if (InRect(b.Origin) || InRect(BedOrientations.Foot(b.Origin, b.Orientation)))
                bedHits.Add(origin);
        }
        var urBoardHits = new List<TilePos>();
        foreach (var tile in sim.UrBoardMap.Keys)
        {
            if (InRect(tile)) urBoardHits.Add(tile);
        }
        var sandbagHits = new List<TilePos>();
        foreach (var tile in sim.SandbagMap.Keys)
        {
            if (InRect(tile)) sandbagHits.Add(tile);
        }

        foreach (var tile in wallHits) sim.TryPostDeconstructJob(tile);
        foreach (var tile in doorHits) sim.TryPostDoorDeconstructJob(tile);
        foreach (var tile in lampHits) sim.TryPostLampDeconstructJob(tile);
        foreach (var tile in stoveHits) sim.TryPostStoveDeconstructJob(tile);
        foreach (var tile in bedHits) sim.TryPostBedDeconstructJob(tile);
        foreach (var tile in urBoardHits) sim.TryPostUrBoardDeconstructJob(tile);
        foreach (var tile in sandbagHits) sim.TryPostSandbagDeconstructJob(tile);
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

// Pin the blueprint at Tile to the colonist with PawnEntityId. The
// blueprint's build job (or any haul targeting it) is then claimable
// only by that pawn, jumping ahead of normal work-tab priorities.
// Sourced from the RMB "Prioritize for X" menu on a blueprint tile.
// Apply resolves the blueprint entity via Jobs.GetByTile(tile) — works
// for wall / floor / door / bed / lamp blueprints since each posts a
// build job whose Entity is the blueprint itself.
public sealed class PrioritizeBlueprintForPawnCommand : ISimCommand
{
    public TilePos Tile { get; }
    public int PawnEntityId { get; }
    public PrioritizeBlueprintForPawnCommand(TilePos tile, int pawnEntityId)
    { Tile = tile; PawnEntityId = pawnEntityId; }
    public void Apply(SimRuntime sim)
    {
        var job = sim.Jobs.GetByTile(Tile);
        if (job is null) return;
        int bpId = sim.GetJobBlueprintId(job);
        if (bpId == 0) return;
        sim.PrioritizeBlueprintForPawn(bpId, PawnEntityId);
    }
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

// === Equipment commands ===

// RMB "Equip" on a dropped equippable pile: order PawnEntityId to fetch
// the pile (ItemEntityId) and equip one unit of it.
public sealed class EquipItemCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public int ItemEntityId { get; }
    public EquipItemCommand(int pawnId, int itemEntityId)
    {
        PawnEntityId = pawnId;
        ItemEntityId = itemEntityId;
    }
    public void Apply(SimRuntime sim) => sim.SetEquipOrder(PawnEntityId, ItemEntityId);
}

// RMB "Pick up" / "Pick up X" on a dropped pile: order the colonist to
// fetch up to Count units into general inventory. Count == int.MaxValue
// means "pick up all" (capacity permitting).
public sealed class PickUpItemCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public int ItemEntityId { get; }
    public int Count { get; }
    public PickUpItemCommand(int pawnId, int itemEntityId, int count)
    {
        PawnEntityId = pawnId;
        ItemEntityId = itemEntityId;
        Count = count;
    }
    public void Apply(SimRuntime sim) => sim.SetPickupOrder(PawnEntityId, ItemEntityId, Count);
}

// RMB "Melee attack X" on a drafted pawn: order it to punch the target.
public sealed class MeleeAttackCommand : ISimCommand
{
    public int AttackerEntityId { get; }
    public int TargetEntityId { get; }
    public MeleeAttackCommand(int attackerId, int targetId)
    {
        AttackerEntityId = attackerId;
        TargetEntityId = targetId;
    }
    public void Apply(SimRuntime sim) => sim.SetMeleeTarget(AttackerEntityId, TargetEntityId);
}

// RMB "Fire at X" / force-target button on a drafted ranged pawn: order it
// to shoot the target while line of sight holds.
public sealed class SetFireTargetCommand : ISimCommand
{
    public int ShooterEntityId { get; }
    public int TargetEntityId { get; }
    public SetFireTargetCommand(int shooterId, int targetId)
    {
        ShooterEntityId = shooterId;
        TargetEntityId = targetId;
    }
    public void Apply(SimRuntime sim) => sim.SetFireTarget(ShooterEntityId, TargetEntityId);
}

// Draft action bar: switch the selected pawn's ranged weapon fire mode.
public sealed class SetFireModeCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public StruggleGame.Sim.Items.FireMode Mode { get; }
    public SetFireModeCommand(int pawnId, StruggleGame.Sim.Items.FireMode mode)
    {
        PawnEntityId = pawnId;
        Mode = mode;
    }
    public void Apply(SimRuntime sim) => sim.SetFireMode(PawnEntityId, Mode);
}

// Draft action bar: set the body region a ranged pawn aims for.
public sealed class SetTargetAreaCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public StruggleGame.Sim.Items.TargetArea Area { get; }
    public SetTargetAreaCommand(int pawnId, StruggleGame.Sim.Items.TargetArea area)
    {
        PawnEntityId = pawnId;
        Area = area;
    }
    public void Apply(SimRuntime sim) => sim.SetTargetArea(PawnEntityId, Area);
}

// RMB "Tend" / "Stabilize": order a drafted doctor (with medicine) to treat a
// patient over time.
public sealed class TreatPawnCommand : ISimCommand
{
    public int DoctorEntityId { get; }
    public int PatientEntityId { get; }
    public bool Stabilize { get; }
    public TreatPawnCommand(int doctorId, int patientId, bool stabilize)
    {
        DoctorEntityId = doctorId;
        PatientEntityId = patientId;
        Stabilize = stabilize;
    }
    public void Apply(SimRuntime sim) => sim.SetTreatmentTarget(DoctorEntityId, PatientEntityId, Stabilize);
}

public sealed class SetAimModeCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public StruggleGame.Sim.Items.AimMode Mode { get; }
    public SetAimModeCommand(int pawnId, StruggleGame.Sim.Items.AimMode mode)
    {
        PawnEntityId = pawnId;
        Mode = mode;
    }
    public void Apply(SimRuntime sim) => sim.SetAimMode(PawnEntityId, Mode);
}

// Draft action bar: manually reload the selected pawn's ranged weapon.
public sealed class ReloadWeaponCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public ReloadWeaponCommand(int pawnId) { PawnEntityId = pawnId; }
    public void Apply(SimRuntime sim) => sim.ManualReload(PawnEntityId);
}

// Reload-button RMB menu: lock auto-reload to an ammo type + force-reload now.
public sealed class SetReloadAmmoCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public string AmmoPath { get; }
    public SetReloadAmmoCommand(int pawnId, string ammoPath) { PawnEntityId = pawnId; AmmoPath = ammoPath; }
    public void Apply(SimRuntime sim) => sim.SetPreferredAmmoAndReload(PawnEntityId, AmmoPath);
}

// Reload-button RMB menu: empty the magazine back into inventory.
public sealed class UnloadMagazineCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public UnloadMagazineCommand(int pawnId) { PawnEntityId = pawnId; }
    public void Apply(SimRuntime sim) => sim.UnloadMagazine(PawnEntityId);
}

// Debug "Add Injury": apply a condition to a colonist's body part.
public sealed class ApplyInjuryCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public string PartId { get; }
    public StruggleGame.Sim.Bodies.ConditionKind Kind { get; }
    public float Severity { get; }
    public ApplyInjuryCommand(int pawnId, string partId, StruggleGame.Sim.Bodies.ConditionKind kind, float severity)
    {
        PawnEntityId = pawnId;
        PartId = partId;
        Kind = kind;
        Severity = severity;
    }
    public void Apply(SimRuntime sim) => sim.ApplyInjury(PawnEntityId, PartId, Kind, Severity);
}

// Debug: stamp a fixed mix of wounds (bleeding / tended / stabilized) onto a
// pawn so the health-tab status icons can be demoed/screenshotted.
public sealed class DebugHealthDemoCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public DebugHealthDemoCommand(int pawnId) { PawnEntityId = pawnId; }
    public void Apply(SimRuntime sim) => sim.DebugHealthDemo(PawnEntityId);
}

// RMB "Prioritize Haul" on a dropped pile outside a stockpile: pin its
// stockpile haul to the chosen colonist.
public sealed class PrioritizeHaulForPawnCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public int ItemEntityId { get; }
    public PrioritizeHaulForPawnCommand(int pawnId, int itemEntityId)
    {
        PawnEntityId = pawnId;
        ItemEntityId = itemEntityId;
    }
    public void Apply(SimRuntime sim) => sim.PrioritizeHaulForPawn(ItemEntityId, PawnEntityId);
}

// Pawn info panel: move equipped slot [EquipIndex] into general inventory.
public sealed class ForceUnequipCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public int EquipIndex { get; }
    public ForceUnequipCommand(int pawnId, int equipIndex)
    {
        PawnEntityId = pawnId;
        EquipIndex = equipIndex;
    }
    public void Apply(SimRuntime sim) => sim.ForceUnequip(PawnEntityId, EquipIndex);
}

// Pawn info panel: drop equipped slot [EquipIndex] on the ground.
public sealed class DropEquippedCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public int EquipIndex { get; }
    public DropEquippedCommand(int pawnId, int equipIndex)
    {
        PawnEntityId = pawnId;
        EquipIndex = equipIndex;
    }
    public void Apply(SimRuntime sim) => sim.DropEquipped(PawnEntityId, EquipIndex);
}

// Pawn info panel: equip one unit of general-inventory stack [HeldIndex]
// that's already on the pawn (no walking).
public sealed class EquipFromInventoryCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public int HeldIndex { get; }
    public EquipFromInventoryCommand(int pawnId, int heldIndex)
    {
        PawnEntityId = pawnId;
        HeldIndex = heldIndex;
    }
    public void Apply(SimRuntime sim) => sim.EquipFromInventory(PawnEntityId, HeldIndex);
}

// Pocket Sand: stash equipped weapons + equip ItemPath (empty = go unarmed).
public sealed class SwapToWeaponCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public string ItemPath { get; }
    public SwapToWeaponCommand(int pawnId, string itemPath)
    {
        PawnEntityId = pawnId;
        ItemPath = itemPath;
    }
    public void Apply(SimRuntime sim) => sim.SwapToWeapon(PawnEntityId, ItemPath);
}

// Debug/demo: hand a pawn a rifle + SMG + melee weapon for the Pocket Sand card.
public sealed class DebugGiveSidearmsCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public DebugGiveSidearmsCommand(int pawnId) { PawnEntityId = pawnId; }
    public void Apply(SimRuntime sim) => sim.DebugGiveSidearms(PawnEntityId);
}

// Pawn info panel: drop general-inventory stack [HeldIndex] on the ground.
public sealed class DropHeldItemCommand : ISimCommand
{
    public int PawnEntityId { get; }
    public int HeldIndex { get; }
    public DropHeldItemCommand(int pawnId, int heldIndex)
    {
        PawnEntityId = pawnId;
        HeldIndex = heldIndex;
    }
    public void Apply(SimRuntime sim) => sim.DropHeldItem(PawnEntityId, HeldIndex);
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

public sealed class SpawnDummyAtCommand : ISimCommand
{
    public TilePos Tile { get; }
    public SpawnDummyAtCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.SpawnDummyAt(Tile.X, Tile.Y);
}

// Harness "gunfight" demo: armed+drafted shooter opens fire on a target.
public sealed class SetupGunfightCommand : ISimCommand
{
    public TilePos ShooterTile { get; }
    public TilePos TargetTile { get; }
    public SetupGunfightCommand(TilePos shooterTile, TilePos targetTile) { ShooterTile = shooterTile; TargetTile = targetTile; }
    public void Apply(SimRuntime sim) => sim.SetupGunfight(ShooterTile, TargetTile);
}

public sealed class SetupEnemyDemoCommand : ISimCommand
{
    public TilePos DefenderTile { get; }
    public TilePos EnemyTile { get; }
    public SetupEnemyDemoCommand(TilePos defenderTile, TilePos enemyTile) { DefenderTile = defenderTile; EnemyTile = enemyTile; }
    public void Apply(SimRuntime sim) => sim.SetupEnemyDemo(DefenderTile, EnemyTile);
}

public sealed class SetAllRecreationLevelCommand : ISimCommand
{
    public float Level { get; }
    public SetAllRecreationLevelCommand(float level) { Level = level; }
    public void Apply(SimRuntime sim) => sim.SetAllRecreationLevel(Level);
}

// Debug bar action: shift world time by N in-sim seconds. Negative
// rewinds. Buttons hand in ±3600 for the ±1hr controls.
public sealed class AdvanceWorldTimeCommand : ISimCommand
{
    public double DeltaSec { get; }
    public AdvanceWorldTimeCommand(double deltaSec) { DeltaSec = deltaSec; }
    public void Apply(SimRuntime sim) => sim.AdvanceWorldTime(DeltaSec);
}

// Work tab: set pawn's per-WorkType priority. priority=0 disables, 1..8
// otherwise (1 = highest urgency, 8 = lowest). Only consulted when sim
// is in priority mode (CheckmarkMode == false). Setting priority to 0
// on the active WorkType also cancels any in-flight job of that type.
public sealed class SetWorkPriorityCommand : ISimCommand
{
    public int EntityId { get; }
    public WorkType Type { get; }
    public byte Priority { get; }
    public SetWorkPriorityCommand(int entityId, WorkType type, byte priority)
    { EntityId = entityId; Type = type; Priority = priority; }
    public void Apply(SimRuntime sim) => sim.SetWorkPriority(EntityId, Type, Priority);
}

// Work tab: set pawn's per-WorkType checkmark (allowed/disallowed).
// Only consulted when sim is in checkmark mode (CheckmarkMode == true).
// Flipping a WorkType off also cancels any in-flight job of that type.
public sealed class SetWorkCheckmarkCommand : ISimCommand
{
    public int EntityId { get; }
    public WorkType Type { get; }
    public bool Allowed { get; }
    public SetWorkCheckmarkCommand(int entityId, WorkType type, bool allowed)
    { EntityId = entityId; Type = type; Allowed = allowed; }
    public void Apply(SimRuntime sim) => sim.SetWorkCheckmark(EntityId, Type, Allowed);
}

// Work tab: flip sim-global checkmark/priority mode. Stored on
// SimRuntime; DummyController consults it when deciding which of the
// two parallel WorkPriorities arrays to read.
public sealed class SetCheckmarkModeCommand : ISimCommand
{
    public bool CheckmarkMode { get; }
    public SetCheckmarkModeCommand(bool checkmarkMode) { CheckmarkMode = checkmarkMode; }
    public void Apply(SimRuntime sim) => sim.SetCheckmarkMode(CheckmarkMode);
}

// Schedule tab: paint pawn's schedule for hours [HourStart..HourEnd]
// with Category. Inclusive on both ends; range may wrap past 23 → 0.
public sealed class PaintScheduleCommand : ISimCommand
{
    public int EntityId { get; }
    public int HourStart { get; }
    public int HourEnd { get; }
    public ScheduleCategory Category { get; }
    public PaintScheduleCommand(int entityId, int hourStart, int hourEnd, ScheduleCategory category)
    { EntityId = entityId; HourStart = hourStart; HourEnd = hourEnd; Category = category; }
    public void Apply(SimRuntime sim) => sim.PaintSchedule(EntityId, HourStart, HourEnd, Category);
}

// Post a bed-construction blueprint at Origin with Orientation. Both
// footprint tiles must be free of walls, doors, trees, lamps, beds,
// and other jobs. Silently no-ops on conflict.
public sealed class PlaceBedCommand : ISimCommand
{
    public TilePos Origin { get; }
    public BedOrientation Orientation { get; }
    public PlaceBedCommand(TilePos origin, BedOrientation orientation)
    { Origin = origin; Orientation = orientation; }
    public void Apply(SimRuntime sim) => sim.TryPlaceBedBlueprint(Origin, Orientation);
}

// Post an Ur-board blueprint at Tile. 1x1 footprint; the tile must be
// free of walls / doors / trees / other furniture / jobs. Silently
// no-ops on conflict. Build cost = SimRuntime.UrBoardWoodCost.
public sealed class PlaceUrBoardCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PlaceUrBoardCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPlaceUrBoardBlueprint(Tile);
}

// Post an UrBoardDeconstruct job on a built Ur board. Sourced from
// the board info panel's Deconstruct button.
public sealed class PostUrBoardDeconCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PostUrBoardDeconCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPostUrBoardDeconstructJob(Tile);
}

// Place a sandbag blueprint at the tile. 1x1 low cover, walkable-but-slow.
// Build cost = SimRuntime.SandbagWoodCost.
public sealed class PlaceSandbagCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PlaceSandbagCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPlaceSandbagBlueprint(Tile);
}

public sealed class PostSandbagDeconCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PostSandbagDeconCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPostSandbagDeconstructJob(Tile);
}

// Post a BedDeconstruct job on a built bed identified by its Origin
// tile. Sourced from the bed info panel's Deconstruct button or the
// X-key shortcut on selected beds.
public sealed class PostBedDeconCommand : ISimCommand
{
    public TilePos Origin { get; }
    public PostBedDeconCommand(TilePos origin) { Origin = origin; }
    public void Apply(SimRuntime sim) => sim.TryPostBedDeconstructJob(Origin);
}

// Bed info panel: set or clear the bed's assigned colonist. PawnEntityId
// == 0 → unassign. Setting a new pawn auto-evicts whoever currently owns
// the bed AND any other bed the new pawn was previously assigned to —
// one bed per colonist per map.
public sealed class AssignBedToColonistCommand : ISimCommand
{
    public TilePos BedOrigin { get; }
    public int PawnEntityId { get; }
    public AssignBedToColonistCommand(TilePos bedOrigin, int pawnEntityId)
    { BedOrigin = bedOrigin; PawnEntityId = pawnEntityId; }
    public void Apply(SimRuntime sim)
    {
        if (!sim.BedMap.TryGetValue(BedOrigin, out var bed)) return;
        if (PawnEntityId == 0)
        {
            if (bed.HasComponent<BedAssignee>())
            {
                int oldPawn = bed.GetComponent<BedAssignee>().PawnEntityId;
                if (sim.Store.TryGetEntityById(oldPawn, out var pawn))
                {
                    sim.UnassignPawnBed(pawn);
                }
                else
                {
                    bed.RemoveComponent<BedAssignee>();
                }
            }
            return;
        }
        sim.AssignBedToPawn(bed.Id, PawnEntityId);
    }
}

// Debug bar action: delete a wanderer by entity id (point-and-click).
public sealed class RemoveDummyCommand : ISimCommand
{
    public int EntityId { get; }
    public RemoveDummyCommand(int entityId) { EntityId = entityId; }
    public void Apply(SimRuntime sim) => sim.RemoveDummy(EntityId);
}

// Debug bar toggle: when true, blueprints skip BlueprintCost gating and
// build for free. When false, build systems wait for materials to be
// deposited before advancing progress.
public sealed class SetGodModeFreeBuildCommand : ISimCommand
{
    public bool Enabled { get; }
    public SetGodModeFreeBuildCommand(bool enabled) { Enabled = enabled; }
    public void Apply(SimRuntime sim) => sim.SetGodModeFreeBuild(Enabled);
}

// ─── Stove / workbench / bills ───────────────────────────────────────
public sealed class PlaceStoveBlueprintCommand : ISimCommand
{
    public TilePos Origin { get; }
    public StoveOrientation Orientation { get; }
    public PlaceStoveBlueprintCommand(TilePos origin, StoveOrientation orientation)
    { Origin = origin; Orientation = orientation; }
    public void Apply(SimRuntime sim) => sim.TryPlaceStoveBlueprint(Origin, Orientation);
}

public sealed class DeconstructStoveCommand : ISimCommand
{
    public TilePos Origin { get; }
    public DeconstructStoveCommand(TilePos origin) { Origin = origin; }
    public void Apply(SimRuntime sim) => sim.TryPostStoveDeconstructJob(Origin);
}

public sealed class InstantPlaceStoveCommand : ISimCommand
{
    public TilePos Origin { get; }
    public StoveOrientation Orientation { get; }
    public InstantPlaceStoveCommand(TilePos origin, StoveOrientation orientation)
    { Origin = origin; Orientation = orientation; }
    public void Apply(SimRuntime sim) => sim.InstantPlaceStove(Origin, Orientation);
}

public sealed class AddBillCommand : ISimCommand
{
    public int StoveEntityId { get; }
    public RecipeId Recipe { get; }
    public BillRepeatMode RepeatMode { get; }
    public int TargetCount { get; }
    public int RemainingCount { get; }
    public BillOutputDest OutputDest { get; }
    public int StockpileEntityId { get; }
    public AddBillCommand(int stoveId, RecipeId recipe, BillRepeatMode mode, int target, int remaining, BillOutputDest dest, int stockpileId)
    {
        StoveEntityId = stoveId; Recipe = recipe; RepeatMode = mode;
        TargetCount = target; RemainingCount = remaining;
        OutputDest = dest; StockpileEntityId = stockpileId;
    }
    public void Apply(SimRuntime sim)
    {
        if (!sim.Store.TryGetEntityById(StoveEntityId, out var stoveEnt)) return;
        if (!stoveEnt.HasComponent<BillsBoard>()) return;
        ref var board = ref stoveEnt.GetComponent<BillsBoard>();
        board.Bills ??= new List<Bill>();
        board.Bills.Add(new Bill
        {
            Recipe = Recipe,
            RepeatMode = RepeatMode,
            TargetCount = TargetCount,
            RemainingCount = RemainingCount,
            OutputDest = OutputDest,
            StockpileEntityId = StockpileEntityId,
        });
    }
}

public sealed class RemoveBillCommand : ISimCommand
{
    public int StoveEntityId { get; }
    public int BillIndex { get; }
    public RemoveBillCommand(int stoveId, int idx) { StoveEntityId = stoveId; BillIndex = idx; }
    public void Apply(SimRuntime sim)
    {
        if (!sim.Store.TryGetEntityById(StoveEntityId, out var stoveEnt)) return;
        if (!stoveEnt.HasComponent<BillsBoard>()) return;
        ref var board = ref stoveEnt.GetComponent<BillsBoard>();
        if (board.Bills is null) return;
        if (BillIndex < 0 || BillIndex >= board.Bills.Count) return;
        board.Bills.RemoveAt(BillIndex);
        // If active bill was removed, reset progress.
        if (stoveEnt.HasComponent<Stove>())
        {
            ref var stove = ref stoveEnt.GetComponent<Stove>();
            if (stove.CurrentBillIndex == BillIndex)
            {
                stove.CurrentBillIndex = -1;
                stove.CookProgressTicks = 0f;
                stove.ActiveCookEntityId = 0;
            }
            else if (stove.CurrentBillIndex > BillIndex)
            {
                stove.CurrentBillIndex--;
            }
        }
    }
}

public sealed class UpdateBillCommand : ISimCommand
{
    public int StoveEntityId { get; }
    public int BillIndex { get; }
    public BillRepeatMode RepeatMode { get; }
    public int TargetCount { get; }
    public int RemainingCount { get; }
    public BillOutputDest OutputDest { get; }
    public int StockpileEntityId { get; }
    public UpdateBillCommand(int stoveId, int idx, BillRepeatMode mode, int target, int remaining, BillOutputDest dest, int stockpileId)
    {
        StoveEntityId = stoveId; BillIndex = idx; RepeatMode = mode;
        TargetCount = target; RemainingCount = remaining;
        OutputDest = dest; StockpileEntityId = stockpileId;
    }
    public void Apply(SimRuntime sim)
    {
        if (!sim.Store.TryGetEntityById(StoveEntityId, out var stoveEnt)) return;
        if (!stoveEnt.HasComponent<BillsBoard>()) return;
        ref var board = ref stoveEnt.GetComponent<BillsBoard>();
        if (board.Bills is null) return;
        if (BillIndex < 0 || BillIndex >= board.Bills.Count) return;
        var b = board.Bills[BillIndex];
        b.RepeatMode = RepeatMode;
        b.TargetCount = TargetCount;
        b.RemainingCount = RemainingCount;
        b.OutputDest = OutputDest;
        b.StockpileEntityId = StockpileEntityId;
        board.Bills[BillIndex] = b;
    }
}

// Harness helper: drop an item pile onto a tile (carrots, meals, etc).
public sealed class SpawnItemPileCommand : ISimCommand
{
    public TilePos Tile { get; }
    public string ItemPath { get; }
    public int Count { get; }
    public SpawnItemPileCommand(TilePos tile, string itemPath, int count)
    { Tile = tile; ItemPath = itemPath; Count = count; }
    public void Apply(SimRuntime sim) => sim.SpawnItemPile(Tile, ItemPath, Count);
}

// Harness helper: add a bill to the first stove found (for demo scenarios
// where we don't want to plumb stove entity ids through the schedule).
public sealed class AddBillToFirstStoveCommand : ISimCommand
{
    public RecipeId Recipe { get; }
    public BillRepeatMode RepeatMode { get; }
    public int TargetCount { get; }
    public int RemainingCount { get; }
    public AddBillToFirstStoveCommand(RecipeId recipe, BillRepeatMode mode, int target, int remaining)
    { Recipe = recipe; RepeatMode = mode; TargetCount = target; RemainingCount = remaining; }
    public void Apply(SimRuntime sim)
    {
        foreach (var kv in sim.StoveMap)
        {
            var stoveEnt = kv.Value;
            if (!stoveEnt.HasComponent<BillsBoard>()) continue;
            ref var board = ref stoveEnt.GetComponent<BillsBoard>();
            board.Bills ??= new List<Bill>();
            board.Bills.Add(new Bill
            {
                Recipe = Recipe,
                RepeatMode = RepeatMode,
                TargetCount = TargetCount,
                RemainingCount = RemainingCount,
                OutputDest = BillOutputDest.DropAtWorkbench,
                StockpileEntityId = 0,
            });
            return;
        }
    }
}

public sealed class ReorderBillCommand : ISimCommand
{
    public int StoveEntityId { get; }
    public int FromIndex { get; }
    public int ToIndex { get; }
    public ReorderBillCommand(int stoveId, int from, int to)
    { StoveEntityId = stoveId; FromIndex = from; ToIndex = to; }
    public void Apply(SimRuntime sim)
    {
        if (!sim.Store.TryGetEntityById(StoveEntityId, out var stoveEnt)) return;
        if (!stoveEnt.HasComponent<BillsBoard>()) return;
        ref var board = ref stoveEnt.GetComponent<BillsBoard>();
        if (board.Bills is null) return;
        if (FromIndex < 0 || FromIndex >= board.Bills.Count) return;
        if (ToIndex < 0 || ToIndex >= board.Bills.Count) return;
        var b = board.Bills[FromIndex];
        board.Bills.RemoveAt(FromIndex);
        board.Bills.Insert(ToIndex, b);
    }
}

// Debug-bar action: drop an item pile at the given tile. Item is
// identified by its catalog FullPath. Count >= 1 required.
public sealed class DebugSpawnItemCommand : ISimCommand
{
    public TilePos Tile { get; }
    public string ItemPath { get; }
    public int Count { get; }
    public DebugSpawnItemCommand(TilePos tile, string itemPath, int count)
    {
        Tile = tile;
        ItemPath = itemPath;
        Count = count;
    }
    public void Apply(SimRuntime sim) => sim.SpawnItemPile(Tile, ItemPath, Count);
}
