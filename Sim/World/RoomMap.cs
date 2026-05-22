using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Flood-fills connected components of non-barrier tiles to assign room ids.
// Walls and doors are barriers; everything else is interior. Border walls
// in TileMap.GenerateDefault mean every non-barrier component is enclosed,
// so each component becomes a room (id 1+). Barrier tiles get id 0.
//
// Compute is called whenever walls or doors change (RebuildMapView, door
// build completion). The output buffer is owned by the caller and reused
// across calls; Compute resizes it if the map dimensions ever change.
public static class RoomMap
{
    // Fills roomIds[w*h] with 0 for barrier (wall/door) tiles and 1..n
    // for interior tiles, grouped by connectivity. Returns n (room count).
    // walls: flat byte[w*h], non-zero = wall. doors: set of door tile keys.
    public static int Compute(
        int width,
        int height,
        ReadOnlySpan<byte> walls,
        IReadOnlyCollection<TilePos> doors,
        int[] roomIds)
    {
        int n = width * height;
        if (roomIds.Length < n) throw new ArgumentException("roomIds buffer too small");

        // Mark barriers up front: -1 = unvisited interior, 0 = barrier.
        for (int i = 0; i < n; i++) roomIds[i] = walls[i] != 0 ? 0 : -1;
        foreach (var d in doors)
        {
            if ((uint)d.X < (uint)width && (uint)d.Y < (uint)height)
                roomIds[d.Y * width + d.X] = 0;
        }

        // 4-connected BFS from each unvisited interior tile.
        int roomCount = 0;
        var queue = new Queue<int>();
        for (int seedIdx = 0; seedIdx < n; seedIdx++)
        {
            if (roomIds[seedIdx] != -1) continue;
            roomCount++;
            int id = roomCount;
            roomIds[seedIdx] = id;
            queue.Enqueue(seedIdx);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % width;
                int cy = cur / width;
                // West
                if (cx > 0)
                {
                    int ni = cur - 1;
                    if (roomIds[ni] == -1) { roomIds[ni] = id; queue.Enqueue(ni); }
                }
                // East
                if (cx + 1 < width)
                {
                    int ni = cur + 1;
                    if (roomIds[ni] == -1) { roomIds[ni] = id; queue.Enqueue(ni); }
                }
                // North
                if (cy > 0)
                {
                    int ni = cur - width;
                    if (roomIds[ni] == -1) { roomIds[ni] = id; queue.Enqueue(ni); }
                }
                // South
                if (cy + 1 < height)
                {
                    int ni = cur + width;
                    if (roomIds[ni] == -1) { roomIds[ni] = id; queue.Enqueue(ni); }
                }
            }
        }
        return roomCount;
    }
}
