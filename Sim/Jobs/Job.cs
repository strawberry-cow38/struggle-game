using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.Jobs;

public readonly record struct JobId(long Value)
{
    public static readonly JobId None = new(0);
    public bool IsNone => Value == 0;
}

// Sim-side job record. Lives on JobBoard; references its backing entity
// for kind-specific component state (e.g. Blueprint progress for
// WallBuild). Score is a tier-agnostic priority hint; higher wins. The
// dirty-flag refresh path will live on JobBoard.Version, not on the Job.
public sealed class Job
{
    public JobId Id { get; }
    public JobKind Kind { get; }
    public TilePos Tile { get; }
    public Entity Entity { get; }
    public JobState State { get; internal set; }
    public Entity Claimant { get; internal set; }
    public float Score { get; internal set; }
    // Forbidden = workers won't claim this job and any current claim is
    // released. Used by the blueprint info panel's Forbid toggle so the
    // player can park a blueprint without cancelling it.
    public bool Forbidden { get; internal set; }

    internal Job(JobId id, JobKind kind, TilePos tile, Entity entity)
    {
        Id = id;
        Kind = kind;
        Tile = tile;
        Entity = entity;
        State = JobState.Open;
        Claimant = default;
        Score = 0f;
        Forbidden = false;
    }
}
