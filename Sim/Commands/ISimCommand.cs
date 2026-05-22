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

// Single-tile door designation. Orientation is derived inside
// TryPlaceDoorBlueprint from the flanking wall layout.
public sealed class PlaceDoorBlueprintCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PlaceDoorBlueprintCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPlaceDoorBlueprint(Tile);
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
                var c = ent.GetComponent<Carrying>();
                TilePos here;
                if (ent.HasComponent<WorldPos>())
                {
                    var wp = ent.GetComponent<WorldPos>();
                    here = new TilePos((int)wp.X, (int)wp.Y);
                }
                else
                {
                    here = c.DestTile;
                }
                var cb = sim.Store.GetCommandBuffer();
                sim.DeliverCarrying(c, here, cb);
                cb.Playback();
                ent.RemoveComponent<Carrying>();
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

// Post a deconstruct job on every player-built wall whose tile lies in
// the inclusive rect. Walls from procgen / map border are excluded.
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
        var hits = new List<TilePos>();
        foreach (var tile in walls)
        {
            if (tile.X < xmin || tile.X > xmax) continue;
            if (tile.Y < ymin || tile.Y > ymax) continue;
            hits.Add(tile);
        }
        foreach (var tile in hits) sim.TryPostDeconstructJob(tile);
    }
}

// Post chop jobs on every tree whose tile lies in the inclusive rect.
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

// Debug bar action: spawn a fresh wanderer at a random walkable tile.
public sealed class SpawnDummyCommand : ISimCommand
{
    public void Apply(SimRuntime sim) => sim.SpawnRandomDummy();
}

// Debug bar action: delete a wanderer by entity id (point-and-click).
public sealed class RemoveDummyCommand : ISimCommand
{
    public int EntityId { get; }
    public RemoveDummyCommand(int entityId) { EntityId = entityId; }
    public void Apply(SimRuntime sim) => sim.RemoveDummy(EntityId);
}
