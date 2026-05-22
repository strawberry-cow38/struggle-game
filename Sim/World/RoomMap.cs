using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Flood-fills connected components of non-barrier tiles to assign room ids.
// Barriers = player-built walls + doors + the magic map border. Procgen
// walls are NOT barriers (they're just terrain) so an "empty colony" map
// reports zero rooms — only spaces the player encloses with their own
// walls and doors become rooms. Components that touch the map border are
// outdoor and get id 0 (not counted in the returned room count).
public static class RoomMap
{
    // Fills roomIds[w*h] with 0 for barrier / outdoor tiles and 1..n
    // for interior rooms grouped by connectivity. Returns n (room count).
    // playerWalls: tiles the player has built a wall on (procgen walls
    // excluded). doors: set of door tile keys.
    public static int Compute(
        int width,
        int height,
        IReadOnlyCollection<TilePos> playerWalls,
        IReadOnlyCollection<TilePos> doors,
        int[] roomIds)
    {
        int n = width * height;
        if (roomIds.Length < n) throw new ArgumentException("roomIds buffer too small");

        // -1 = unvisited interior, 0 = barrier (border / player wall / door).
        for (int i = 0; i < n; i++) roomIds[i] = -1;
        for (int x = 0; x < width; x++)
        {
            roomIds[x] = 0;
            roomIds[(height - 1) * width + x] = 0;
        }
        for (int y = 0; y < height; y++)
        {
            roomIds[y * width] = 0;
            roomIds[y * width + (width - 1)] = 0;
        }
        foreach (var w in playerWalls)
        {
            if ((uint)w.X < (uint)width && (uint)w.Y < (uint)height)
                roomIds[w.Y * width + w.X] = 0;
        }
        foreach (var d in doors)
        {
            if ((uint)d.X < (uint)width && (uint)d.Y < (uint)height)
                roomIds[d.Y * width + d.X] = 0;
        }

        // BFS each unvisited component. Track which components touch a
        // tile adjacent to the border (cx == 1 / cy == 1 / cx == w-2 /
        // cy == h-2). Those are outdoor and get remapped to id 0 later.
        int rawCount = 0;
        var queue = new Queue<int>();
        var touchesBorder = new List<bool> { false }; // index 0 unused
        for (int seedIdx = 0; seedIdx < n; seedIdx++)
        {
            if (roomIds[seedIdx] != -1) continue;
            rawCount++;
            int id = rawCount;
            touchesBorder.Add(false);
            roomIds[seedIdx] = id;
            queue.Enqueue(seedIdx);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % width;
                int cy = cur / width;
                if (cx == 1 || cy == 1 || cx == width - 2 || cy == height - 2)
                    touchesBorder[id] = true;
                if (cx > 0)
                {
                    int ni = cur - 1;
                    if (roomIds[ni] == -1) { roomIds[ni] = id; queue.Enqueue(ni); }
                }
                if (cx + 1 < width)
                {
                    int ni = cur + 1;
                    if (roomIds[ni] == -1) { roomIds[ni] = id; queue.Enqueue(ni); }
                }
                if (cy > 0)
                {
                    int ni = cur - width;
                    if (roomIds[ni] == -1) { roomIds[ni] = id; queue.Enqueue(ni); }
                }
                if (cy + 1 < height)
                {
                    int ni = cur + width;
                    if (roomIds[ni] == -1) { roomIds[ni] = id; queue.Enqueue(ni); }
                }
            }
        }

        // Renumber: outdoor (border-touching) components → 0, real rooms
        // → 1..realCount in encounter order.
        var remap = new int[rawCount + 1];
        int realCount = 0;
        for (int id = 1; id <= rawCount; id++)
        {
            if (touchesBorder[id]) remap[id] = 0;
            else { realCount++; remap[id] = realCount; }
        }
        for (int i = 0; i < n; i++)
        {
            int id = roomIds[i];
            if (id > 0) roomIds[i] = remap[id];
        }
        return realCount;
    }
}
