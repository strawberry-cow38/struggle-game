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
        // 5x5 with border walls and a vertical divider at x=2.
        // Layout:
        //   #####
        //   #.#.#
        //   #.#.#
        //   #.#.#
        //   #####
        const int w = 5, h = 5;
        var walls = new byte[w * h];
        for (int x = 0; x < w; x++) { walls[x] = 1; walls[(h - 1) * w + x] = 1; }
        for (int y = 0; y < h; y++) { walls[y * w] = 1; walls[y * w + (w - 1)] = 1; }
        for (int y = 1; y < h - 1; y++) walls[y * w + 2] = 1;

        var ids = new int[w * h];
        int count = RoomMap.Compute(w, h, walls, Array.Empty<TilePos>(), ids);

        Assert.Equal(2, count);
        // Left and right interior tiles should each be one room.
        Assert.Equal(ids[1 * w + 1], ids[3 * w + 1]);
        Assert.Equal(ids[1 * w + 3], ids[3 * w + 3]);
        Assert.NotEqual(ids[1 * w + 1], ids[1 * w + 3]);
        // Border + divider tiles are 0.
        Assert.Equal(0, ids[0]);
        Assert.Equal(0, ids[1 * w + 2]);
    }

    [Fact]
    public void RoomMap_DoorCountsAsBarrier()
    {
        // Same layout but the divider has a door at the middle row.
        const int w = 5, h = 5;
        var walls = new byte[w * h];
        for (int x = 0; x < w; x++) { walls[x] = 1; walls[(h - 1) * w + x] = 1; }
        for (int y = 0; y < h; y++) { walls[y * w] = 1; walls[y * w + (w - 1)] = 1; }
        for (int y = 1; y < h - 1; y++) walls[y * w + 2] = 1;
        // Punch a hole in the wall layer where the door sits, then mark
        // the tile as a door so RoomMap still treats it as a barrier.
        walls[2 * w + 2] = 0;
        var doors = new[] { new TilePos(2, 2) };

        var ids = new int[w * h];
        int count = RoomMap.Compute(w, h, walls, doors, ids);

        Assert.Equal(2, count);
        Assert.NotEqual(ids[1 * w + 1], ids[1 * w + 3]);
        Assert.Equal(0, ids[2 * w + 2]);
    }

    [Fact]
    public void SimRuntime_BuildingWallsCreatesRooms()
    {
        // Procgen border walls already enclose one giant outdoor "room".
        var sim = new SimRuntime();
        long startVer = sim.RoomVersion;
        int startRooms = sim.RoomCount;
        Assert.True(startRooms >= 1);

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

        Assert.True(sim.RoomCount > startRooms);
        Assert.True(sim.RoomVersion > startVer);
    }
}
