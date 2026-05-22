using StruggleGame.Sim.Map;
using Xunit;

namespace StruggleGame.Tests;

public class ChunkedSnapshotTests
{
    [Fact]
    public void Snapshot_ReusesChunks_WhenNothingChanged()
    {
        // 64x64 = 2x2 chunks per layer.
        var map = new TileMap(64, 64);
        var first = map.Snapshot(1);
        var second = map.Snapshot(2, first);

        // No mutations between snapshots: every chunk should be the
        // same byte[] reference, proving the snapshot path skipped the
        // clone entirely.
        for (int i = 0; i < first.WallChunks.Length; i++)
        {
            Assert.Same(first.WallChunks[i], second.WallChunks[i]);
            Assert.Same(first.TerrainChunks[i], second.TerrainChunks[i]);
            Assert.Same(first.FlooringChunks[i], second.FlooringChunks[i]);
            Assert.Same(first.RoofChunks[i], second.RoofChunks[i]);
        }
    }

    [Fact]
    public void Snapshot_ClonesOnlyDirtyChunks()
    {
        // 96x96 = 3x3 chunks per layer. Mutate a single tile in the
        // (1,1) center chunk and confirm the other 8 chunks are reused
        // by reference while the dirty one gets a fresh byte[].
        var map = new TileMap(96, 96);
        var first = map.Snapshot(1);

        map.SetWall(40, 40, WallType.Stone); // chunk (1, 1) for ChunkSize=32

        var second = map.Snapshot(2, first);

        int dirtyChunkIndex = MapChunks.ChunkIndex(40, 40, map.ChunksAcross);

        for (int i = 0; i < first.WallChunks.Length; i++)
        {
            if (i == dirtyChunkIndex)
            {
                Assert.NotSame(first.WallChunks[i], second.WallChunks[i]);
            }
            else
            {
                Assert.Same(first.WallChunks[i], second.WallChunks[i]);
            }
        }
    }

    [Fact]
    public void MapView_GetWall_ReflectsMutations()
    {
        var map = new TileMap(64, 64);
        map.SetWall(10, 20, WallType.Stone);
        var view = map.Snapshot(1);

        Assert.Equal(WallType.Stone, view.GetWall(10, 20));
        Assert.Equal(WallType.None, view.GetWall(11, 20));
    }

    [Fact]
    public void AssembleFlat_RoundTripsWallLayer()
    {
        var map = new TileMap(40, 40);
        map.SetWall(5, 5, WallType.Stone);
        map.SetWall(20, 30, WallType.Stone);
        var view = map.Snapshot(1);

        var flat = view.AssembleFlat(MapLayer.Wall);
        Assert.Equal(40 * 40, flat.Length);
        Assert.NotEqual(0, flat[5 * 40 + 5]);
        Assert.NotEqual(0, flat[30 * 40 + 20]);
        Assert.Equal(0, flat[5 * 40 + 6]);
    }

    [Fact]
    public void MapView_WallsList_Populated()
    {
        var map = new TileMap(32, 32);
        map.SetWall(1, 1, WallType.Stone);
        map.SetWall(2, 2, WallType.Stone);
        map.SetWall(3, 3, WallType.Stone);
        var view = map.Snapshot(1);
        Assert.Equal(3, view.Walls.Count);
    }
}
