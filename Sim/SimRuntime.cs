using System.Collections.Concurrent;
using Friflo.Engine.ECS;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim;

public sealed class SimRuntime
{
    public EntityStore Store { get; } = new();
    public TileMap Map { get; }
    public BlueprintRegistry Blueprints { get; } = new();
    public long Tick { get; private set; }
    public long MapVersion { get; private set; }

    private MapView _mapView;
    public MapView MapView => Volatile.Read(ref _mapView);

    public PathService PathService { get; }

    private readonly DummyController _dummies;
    private readonly BuildSystem _builds;
    private readonly ConcurrentQueue<ISimCommand> _commands = new();
    private readonly object _mapLock = new();

    public SimRuntime(int seed = 1337)
    {
        Map = TileMap.GenerateDefault(SimConstants.MapSize, SimConstants.MapSize, seed);
        _mapView = Map.Snapshot(MapVersion);
        PathService = new PathService(Map.Width, Map.Height, () => MapView);
        _dummies = new DummyController(PathService, Blueprints, () => MapView, seed + 1);
        _builds = new BuildSystem(this, Blueprints);

        SpawnDummy(SimConstants.MapSize / 2, SimConstants.MapSize / 2);
    }

    public void Step(float dt)
    {
        while (_commands.TryDequeue(out var cmd)) cmd.Apply(this);
        _dummies.Step(Store, dt);
        _builds.Step(Store, dt);
        Tick++;
    }

    public void QueueCommand(ISimCommand cmd) => _commands.Enqueue(cmd);

    public SimSnapshot BuildSnapshot()
    {
        var dq = Store.Query<WorldPos, Wanderer>();
        var dummies = new DummyState[dq.Count];
        int i = 0;
        dq.ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            dummies[i++] = new DummyState(p.X, p.Y);
        });

        var bps = new BlueprintState[Blueprints.Count];
        int j = 0;
        foreach (var kv in Blueprints.All)
        {
            var bp = kv.Value.GetComponent<Blueprint>();
            bps[j++] = new BlueprintState(kv.Key, bp.ProgressSec / BuildSystem.BuildTimeSec);
        }

        return new SimSnapshot(Tick, MapVersion, dummies, bps);
    }

    // Snapshot of the tile array taken under a lock so a parallel write
    // can't tear it. Game uses this to rebuild the wall overlay texture
    // when MapVersion changes.
    public byte[] CopyTilesForRender()
    {
        lock (_mapLock)
        {
            var src = Map.RawTiles;
            var copy = new byte[src.Length];
            for (int k = 0; k < src.Length; k++) copy[k] = (byte)src[k];
            return copy;
        }
    }

    public bool TryPlaceWallBlueprint(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.Get(tile) == TileType.Wall) return false;
        if (Blueprints.Has(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Blueprint { Tile = tile, ProgressSec = 0f });
        Blueprints.Add(tile, e);
        return true;
    }

    public void CompleteWallBlueprint(TilePos tile)
    {
        if (!Blueprints.TryGet(tile, out var entity)) return;
        Blueprints.Remove(tile);
        entity.DeleteEntity();

        MapView newView;
        lock (_mapLock)
        {
            Map.Set(tile, TileType.Wall);
            MapVersion++;
            newView = Map.Snapshot(MapVersion);
        }
        Volatile.Write(ref _mapView, newView);
    }

    private void SpawnDummy(int tileX, int tileY)
    {
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
