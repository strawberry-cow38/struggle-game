using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.Commands;

// Game→Sim commands. Game thread enqueues; Sim thread drains at the
// start of every tick. Keep commands tiny + value-typed so there's no
// shared mutable state.
public interface ISimCommand
{
    void Apply(SimRuntime sim);
}

public sealed class PlaceWallBlueprintCommand : ISimCommand
{
    public TilePos Tile { get; }
    public PlaceWallBlueprintCommand(TilePos tile) { Tile = tile; }
    public void Apply(SimRuntime sim) => sim.TryPlaceWallBlueprint(Tile);
}

// Cancel every job whose tile lies in the inclusive rect. Used by the
// drag-rect cancel designator. Currently affects WallBuild jobs; future
// kinds get whatever cancel semantics they need.
public sealed class CancelJobsInRectCommand : ISimCommand
{
    public TilePos A { get; }
    public TilePos B { get; }
    public CancelJobsInRectCommand(TilePos a, TilePos b) { A = a; B = b; }
    public void Apply(SimRuntime sim)
    {
        var ids = new List<Jobs.JobId>();
        foreach (var job in sim.Jobs.InRect(A.X, A.Y, B.X, B.Y))
        {
            ids.Add(job.Id);
        }
        foreach (var id in ids) sim.CancelJob(id);
    }
}
