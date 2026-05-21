using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Per-tick: after movement, look at every Wanderer that's standing
// adjacent to its BuildTarget blueprint, advance progress, and complete
// the blueprint (turning the tile into a wall and bumping the map
// version) when ProgressSec >= BuildTimeSec.
public sealed class BuildSystem
{
    public const float BuildTimeSec = 1.5f;

    private readonly TileMap _map;
    private readonly BlueprintRegistry _registry;
    private readonly SimRuntime _sim;

    public BuildSystem(SimRuntime sim, TileMap map, BlueprintRegistry registry)
    {
        _sim = sim;
        _map = map;
        _registry = registry;
    }

    public void Step(EntityStore store, float dt)
    {
        var completed = new List<TilePos>();

        var builders = store.Query<WorldPos, BuildTarget, Wanderer>();
        builders.ForEachEntity((ref WorldPos pos, ref BuildTarget target, ref Wanderer _, Entity _) =>
        {
            if (!_registry.TryGet(target.Tile, out var bp)) return;

            int btx = target.Tile.X;
            int bty = target.Tile.Y;
            int ptx = (int)pos.X;
            int pty = (int)pos.Y;
            int dx = Math.Abs(ptx - btx);
            int dy = Math.Abs(pty - bty);
            // 4-connected adjacency only (no diagonal "reach").
            if (dx + dy != 1) return;

            ref var blueprint = ref bp.GetComponent<Blueprint>();
            blueprint.ProgressSec += dt;
            if (blueprint.ProgressSec >= BuildTimeSec)
            {
                completed.Add(target.Tile);
            }
        });

        foreach (var tile in completed)
        {
            _sim.CompleteWallBlueprint(tile);
        }
    }
}
