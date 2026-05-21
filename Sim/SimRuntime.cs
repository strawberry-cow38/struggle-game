using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim;

public sealed class SimRuntime
{
    public EntityStore Store { get; } = new();
    public TileMap Map { get; }
    public long Tick { get; private set; }

    private readonly DummyController _dummies;

    public SimRuntime(int seed = 1337)
    {
        Map = TileMap.GenerateDefault(SimConstants.MapSize, SimConstants.MapSize, seed);
        _dummies = new DummyController(Map, seed + 1);

        SpawnDummy(SimConstants.MapSize / 2, SimConstants.MapSize / 2);
    }

    public void Step(float dt)
    {
        _dummies.Step(Store, dt);
        Tick++;
    }

    public SimSnapshot BuildSnapshot()
    {
        var query = Store.Query<WorldPos, Wanderer>();
        var buf = new DummyState[query.Count];
        int i = 0;
        query.ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            buf[i++] = new DummyState(p.X, p.Y);
        });
        return new SimSnapshot(Tick, buf);
    }

    private void SpawnDummy(int tileX, int tileY)
    {
        // Walk outward in a spiral until we find a walkable tile in case the
        // requested spawn happens to land on a wall cluster.
        for (int r = 0; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = tileX + dx;
                    int y = tileY + dy;
                    if (!Map.Walkable(x, y)) continue;

                    var e = Store.CreateEntity();
                    e.AddComponent(new WorldPos { X = x + 0.5f, Y = y + 0.5f });
                    e.AddComponent(new PathFollower());
                    e.AddComponent(new Wanderer());
                    return;
                }
            }
        }
        throw new InvalidOperationException("No walkable tile found for dummy spawn.");
    }
}
