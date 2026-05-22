using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class DoorTests
{
    [Fact]
    public void PlaceDoor_RequiresFlankingWalls()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        // No walls yet → rejected.
        sim.QueueCommand(new PlaceDoorBlueprintCommand(tile));
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(0, CountDoorJobs(sim));

        // Build flanking walls (east + west).
        PlaceAndBuildWall(sim, new TilePos(tile.X - 1, tile.Y));
        PlaceAndBuildWall(sim, new TilePos(tile.X + 1, tile.Y));

        sim.QueueCommand(new PlaceDoorBlueprintCommand(tile));
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(1, CountDoorJobs(sim));
    }

    [Fact]
    public void DoorBuild_CompletesAndDoorIsClosedClosed()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);
        PlaceAndBuildWall(sim, new TilePos(tile.X - 1, tile.Y));
        PlaceAndBuildWall(sim, new TilePos(tile.X + 1, tile.Y));

        sim.QueueCommand(new PlaceDoorBlueprintCommand(tile));
        for (int i = 0; i < 1500; i++) sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.TryGetDoor(tile, out var doorEnt));
        var d = doorEnt.GetComponent<Door>();
        Assert.Equal(DoorState.Closed, d.State);
        Assert.Equal(DoorOrientation.Horizontal, d.Orientation);
    }

    [Fact]
    public void Door_OpensWhenWantsOpenFlagSet_ThenAutoClosesAfterIdle()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);
        PlaceAndBuildWall(sim, new TilePos(tile.X - 1, tile.Y));
        PlaceAndBuildWall(sim, new TilePos(tile.X + 1, tile.Y));

        sim.QueueCommand(new PlaceDoorBlueprintCommand(tile));
        for (int i = 0; i < 1500; i++) sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.TryGetDoor(tile, out var doorEnt));

        // Set WantsOpen and let the system tick the animation.
        ref var dref = ref doorEnt.GetComponent<Door>();
        dref.WantsOpen = true;

        int ticksToOpen = (int)((DoorSystem.OpenTimeSec / SimConstants.TickSeconds) + 5);
        for (int i = 0; i < ticksToOpen; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(DoorState.Open, doorEnt.GetComponent<Door>().State);

        // Now stop pinging WantsOpen; idle time accumulates.
        int ticksToAutoClose = (int)(((DoorSystem.AutoCloseSec + DoorSystem.OpenTimeSec) / SimConstants.TickSeconds) + 5);
        for (int i = 0; i < ticksToAutoClose; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(DoorState.Closed, doorEnt.GetComponent<Door>().State);
    }

    [Fact]
    public void WallPlacement_OnDoorTile_Rejected()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);
        PlaceAndBuildWall(sim, new TilePos(tile.X - 1, tile.Y));
        PlaceAndBuildWall(sim, new TilePos(tile.X + 1, tile.Y));
        sim.QueueCommand(new PlaceDoorBlueprintCommand(tile));
        for (int i = 0; i < 1500; i++) sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.TryGetDoor(tile, out _));

        bool placed = sim.TryPlaceWallBlueprint(tile);
        Assert.False(placed);
    }

    [Fact]
    public void FloorPlacement_UnderDoor_Accepted()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);
        PlaceAndBuildWall(sim, new TilePos(tile.X - 1, tile.Y));
        PlaceAndBuildWall(sim, new TilePos(tile.X + 1, tile.Y));
        sim.QueueCommand(new PlaceDoorBlueprintCommand(tile));
        for (int i = 0; i < 1500; i++) sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.TryGetDoor(tile, out _));

        // Door occupies the job board slot until the build completes,
        // but once it does the tile is free for a floor job.
        bool ok = sim.TryPlaceFloorBlueprint(tile);
        Assert.True(ok);
        for (int i = 0; i < 1500; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(FlooringType.Wood, sim.Map.GetFlooring(tile));
        Assert.True(sim.TryGetDoor(tile, out _));
    }

    [Fact]
    public void DoorTile_IsWalkable_InMapView()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);
        PlaceAndBuildWall(sim, new TilePos(tile.X - 1, tile.Y));
        PlaceAndBuildWall(sim, new TilePos(tile.X + 1, tile.Y));
        sim.QueueCommand(new PlaceDoorBlueprintCommand(tile));
        for (int i = 0; i < 1500; i++) sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.TryGetDoor(tile, out _));

        Assert.True(sim.MapView.Walkable(tile));
    }

    private static void PlaceAndBuildWall(SimRuntime sim, TilePos tile)
    {
        sim.QueueCommand(new PlaceWallBlueprintCommand(tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(WallType.Stone, sim.Map.GetWall(tile));
    }

    private static TilePos NearestBuildableTile(SimRuntime sim)
    {
        int c = SimConstants.MapSize / 2;
        for (int r = 1; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = c + dx, y = c + dy;
                    var t = new TilePos(x, y);
                    if (!sim.MapView.Walkable(t)) continue;
                    if (sim.TreeTiles.Contains(t)) continue;
                    // Need flanking tiles also buildable (no wall, no tree).
                    var l = new TilePos(x - 1, y);
                    var rr = new TilePos(x + 1, y);
                    if (!sim.MapView.Walkable(l) || !sim.MapView.Walkable(rr)) continue;
                    if (sim.TreeTiles.Contains(l) || sim.TreeTiles.Contains(rr)) continue;
                    bool occ = false;
                    sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
                    {
                        if ((int)p.X == x && (int)p.Y == y) occ = true;
                        if ((int)p.X == l.X && (int)p.Y == l.Y) occ = true;
                        if ((int)p.X == rr.X && (int)p.Y == rr.Y) occ = true;
                    });
                    if (occ) continue;
                    return t;
                }
        }
        throw new Xunit.Sdk.XunitException("no buildable tile with flanking room near center");
    }

    private static int CountDoorJobs(SimRuntime sim)
    {
        int n = 0;
        foreach (var j in sim.Jobs.All) if (j.Kind == JobKind.DoorBuild) n++;
        return n;
    }
}
