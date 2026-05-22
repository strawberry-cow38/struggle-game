namespace StruggleGame.Sim.Map;

// Chunk geometry shared by TileMap (mutable storage) and MapView
// (immutable snapshot). One chunk holds ChunkSize x ChunkSize tiles
// stored row-major in a single byte[]. Maps wider than a multiple of
// ChunkSize get partial edge chunks — chunk byte[] is still full size,
// the out-of-bounds bytes are simply unused.
//
// Picked 32 because it's small enough that a single-tile mutation
// only invalidates 1024 bytes (vs. the entire map flat array) but big
// enough that a 256x256 map has just 64 chunks per layer — cheap to
// iterate and clone.
public static class MapChunks
{
    public const int ChunkSize = 32;
    public const int ChunkArea = ChunkSize * ChunkSize;

    public static int ChunksAcross(int width) => (width + ChunkSize - 1) / ChunkSize;
    public static int ChunksDown(int height) => (height + ChunkSize - 1) / ChunkSize;

    public static int ChunkIndex(int x, int y, int chunksAcross)
        => (y / ChunkSize) * chunksAcross + (x / ChunkSize);

    public static int LocalIndex(int x, int y)
        => ((y & (ChunkSize - 1)) * ChunkSize) + (x & (ChunkSize - 1));
}
