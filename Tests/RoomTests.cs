using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class RoomTests
{
    [Fact]
    public void RoomMap_FloodFillsConnectedNonBarrierTiles()
    {
        // 5x5 with a player-built vertical divider at x=2. Outer ring is
        // the magic border (RoomMap treats it as barrier automatically).
        // Layout:
        //   #####
        //   #.#.#
        //   #.#.#
        //   #.#.#
        //   #####
        const int w = 5, h = 5;
        var divider = new[] { new TilePos(2, 1), new TilePos(2, 2), new TilePos(2, 3) };

        var ids = new int[w * h];
        int count = RoomMap.Compute(w, h, divider, Array.Empty<TilePos>(), ids);

        // Both pockets touch the border-adjacent ring (x=1 / x=3), so
        // they're outdoor and counted as 0 rooms.
        Assert.Equal(0, count);
        Assert.Equal(0, ids[1 * w + 1]);
        Assert.Equal(0, ids[1 * w + 3]);
        Assert.Equal(0, ids[0]);
        Assert.Equal(0, ids[1 * w + 2]);
    }

    [Fact]
    public void RoomMap_EnclosedPocketIsAroom()
    {
        // 7x7 with a 3x3 player wall ring around (3,3) leaving the
        // center as a single enclosed tile. Outside is outdoor, center
        // pocket is one real room.
        const int w = 7, h = 7;
        var walls = new[]
        {
            new TilePos(2, 2), new TilePos(3, 2), new TilePos(4, 2),
            new TilePos(2, 3),                    new TilePos(4, 3),
            new TilePos(2, 4), new TilePos(3, 4), new TilePos(4, 4),
        };

        var ids = new int[w * h];
        int count = RoomMap.Compute(w, h, walls, Array.Empty<TilePos>(), ids);

        Assert.Equal(1, count);
        Assert.Equal(1, ids[3 * w + 3]);
        // Outside is outdoor.
        Assert.Equal(0, ids[1 * w + 1]);
    }

    [Fact]
    public void RoomMap_DoorCountsAsBarrier()
    {
        // Same 3x3 ring but with a door at the south edge. Door is a
        // barrier so the center is still enclosed (count = 1).
        const int w = 7, h = 7;
        var walls = new[]
        {
            new TilePos(2, 2), new TilePos(3, 2), new TilePos(4, 2),
            new TilePos(2, 3),                    new TilePos(4, 3),
            new TilePos(2, 4),                    new TilePos(4, 4),
        };
        var doors = new[] { new TilePos(3, 4) };

        var ids = new int[w * h];
        int count = RoomMap.Compute(w, h, walls, doors, ids);

        Assert.Equal(1, count);
        Assert.Equal(1, ids[3 * w + 3]);
        Assert.Equal(0, ids[4 * w + 3]);
    }

    [Fact]
    public void SimRuntime_BuildingWallsCreatesRooms()
    {
        // Fresh sim has no player walls → 0 rooms (procgen walls and the
        // magic border don't enclose rooms by themselves).
        var sim = new SimRuntime();
        long startVer = sim.RoomVersion;
        Assert.Equal(0, sim.RoomCount);

        // Wall in a tiny 3x3 box around a center tile to carve out a
        // second room. Pick a spot near map center that's already
        // walkable and tree-free.
        int cx = SimConstants.MapSize / 2;
        int cy = SimConstants.MapSize / 2;
        TilePos? interior = null;
        for (int r = 2; r < 16 && interior is null; r++)
        {
            for (int dy = -r; dy <= r && interior is null; dy++)
                for (int dx = -r; dx <= r && interior is null; dx++)
                {
                    var c = new TilePos(cx + dx, cy + dy);
                    bool ok = true;
                    for (int yy = -1; yy <= 1 && ok; yy++)
                        for (int xx = -1; xx <= 1 && ok; xx++)
                        {
                            var t = new TilePos(c.X + xx, c.Y + yy);
                            if (!sim.MapView.Walkable(t)) ok = false;
                            if (sim.TreeTiles.Contains(t)) ok = false;
                        }
                    if (ok) interior = c;
                }
        }
        Assert.NotNull(interior);

        // Build a wall ring leaving the center as the enclosed room.
        var ring = new[]
        {
            new TilePos(interior!.Value.X - 1, interior.Value.Y - 1),
            new TilePos(interior.Value.X,     interior.Value.Y - 1),
            new TilePos(interior.Value.X + 1, interior.Value.Y - 1),
            new TilePos(interior.Value.X - 1, interior.Value.Y),
            new TilePos(interior.Value.X + 1, interior.Value.Y),
            new TilePos(interior.Value.X - 1, interior.Value.Y + 1),
            new TilePos(interior.Value.X,     interior.Value.Y + 1),
            new TilePos(interior.Value.X + 1, interior.Value.Y + 1),
        };
        foreach (var t in ring)
        {
            sim.QueueCommand(new PlaceWallBlueprintCommand(t));
        }
        for (int i = 0; i < 4000; i++) sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.RoomCount >= 1);
        Assert.True(sim.RoomVersion > startVer);
    }
}
