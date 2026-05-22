using StruggleGame.Sim.Map;
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
            sim.Jobs.Release(bt.JobId);
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
