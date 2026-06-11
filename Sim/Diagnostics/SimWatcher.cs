using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim.Diagnostics;

public enum SimAnomalyKind
{
    Stuck,
    BrainDead,
    Rescued,
    StaleBillTarget,
}

public readonly record struct SimAnomaly(long Tick, int EntityId, SimAnomalyKind Kind, string Detail);

// Periodic sim-side liveness check. Sampled every SampleEveryTicks from
// SimRuntime.Step on the sim thread. Detects two failure modes the game
// engine cannot see by itself:
//   * Stuck:     pawn intends to move (walking, awaiting path, or build
//                approach) but its tile coord hasn't changed for
//                StuckTicks consecutive samples.
//   * BrainDead: undrafted pawn with no job, no path, no pending request
//                for BrainDeadTicks — i.e. wander loop gave up on it.
//
// Each kind is reported once per pawn per "episode"; flag clears as soon
// as the pawn recovers. Game thread reads the published anomaly array
// via Volatile snapshot — no locks.
public sealed class SimWatcher
{
    public const int SampleEveryTicks = 30;   // ~0.5s @ 60Hz
    public const int StuckTicks = 180;        // ~3s of wanting-to-move without moving
    public const int BrainDeadTicks = 600;    // ~10s of nothing-to-do
    public const int MaxRecent = 64;

    private sealed class PawnState
    {
        public float LastX;
        public float LastY;
        public long LastMoveTick;
        public long IdleStartTick;
        public bool StuckReported;
        public bool BrainDeadReported;
    }

    // Below this float distance per sample we consider the pawn stationary.
    // Walk speed is ~3 tiles/s so a sane sample (0.5s) moves at least
    // ~1.5 units; 0.05 is well under any real progress.
    private const float MovementEpsilon = 0.05f;

    private readonly Dictionary<int, PawnState> _pawns = new();
    private readonly List<SimAnomaly> _recent = new();
    private SimAnomaly[] _publish = Array.Empty<SimAnomaly>();
    private int _stuckTotal;
    private int _brainDeadTotal;
    private int _rescuedTotal;

    public int StuckTotal => Volatile.Read(ref _stuckTotal);
    public int BrainDeadTotal => Volatile.Read(ref _brainDeadTotal);
    public int RescuedTotal => Volatile.Read(ref _rescuedTotal);
    public SimAnomaly[] Recent => Volatile.Read(ref _publish);

    public void RecordRescue(long tick, int entityId, TilePos from, TilePos to)
    {
        Report(tick, entityId, SimAnomalyKind.Rescued, $"wall {from.X},{from.Y} -> {to.X},{to.Y}");
        Interlocked.Increment(ref _rescuedTotal);
    }

    // One-off data-integrity warning: a bill's target stockpile no longer
    // exists, so its output destination was downgraded to DropAtWorkbench.
    public void RecordStaleBillTarget(long tick, int entityId, string detail)
        => Report(tick, entityId, SimAnomalyKind.StaleBillTarget, detail);

    private readonly HashSet<int> _seenScratch = new();
    private ArchetypeQuery<WorldPos, PathFollower, Wanderer>? _pawnsQ;

    public void Observe(long tick, EntityStore store, JobBoard jobs)
    {
        if (tick % SampleEveryTicks != 0) return;

        var seen = _seenScratch;
        seen.Clear();
        var query = _pawnsQ ??= store.Query<WorldPos, PathFollower, Wanderer>();
        query.ForEachEntity((ref WorldPos pos, ref PathFollower path, ref Wanderer _, Entity ent) =>
        {
            seen.Add(ent.Id);
            var tile = new TilePos((int)pos.X, (int)pos.Y);
            if (!_pawns.TryGetValue(ent.Id, out var st))
            {
                st = new PawnState { LastX = pos.X, LastY = pos.Y, LastMoveTick = tick };
                _pawns[ent.Id] = st;
                return;
            }

            float ddx = pos.X - st.LastX;
            float ddy = pos.Y - st.LastY;
            if (ddx * ddx + ddy * ddy > MovementEpsilon * MovementEpsilon)
            {
                st.LastX = pos.X;
                st.LastY = pos.Y;
                st.LastMoveTick = tick;
                st.StuckReported = false;
            }

            bool drafted = ent.HasComponent<Drafted>();
            bool hasBuild = ent.HasComponent<BuildTarget>();
            bool wantsMove = false;
            string intent = "idle";

            if (path.PendingPathId != 0)
            {
                wantsMove = true;
                intent = "awaiting-path";
            }
            else if (path.Waypoints is { Count: > 0 } wp && path.Index < wp.Count)
            {
                wantsMove = true;
                intent = "walking";
            }
            else if (hasBuild)
            {
                var bt = ent.GetComponent<BuildTarget>();
                var job = jobs.Get(bt.JobId);
                if (job is not null)
                {
                    bool adj = Math.Abs(job.Tile.X - tile.X) + Math.Abs(job.Tile.Y - tile.Y) == 1;
                    if (adj)
                    {
                        intent = "building";
                    }
                    else
                    {
                        wantsMove = true;
                        intent = "build-approach";
                    }
                }
            }
            else if (drafted)
            {
                intent = "drafted-hold";
            }

            if (wantsMove && (tick - st.LastMoveTick) >= StuckTicks)
            {
                if (!st.StuckReported)
                {
                    Report(tick, ent.Id, SimAnomalyKind.Stuck, $"{intent} @ {tile.X},{tile.Y}");
                    st.StuckReported = true;
                    Interlocked.Increment(ref _stuckTotal);
                }
            }

            bool fullyIdle = !drafted && !wantsMove && !hasBuild;
            if (fullyIdle)
            {
                if (st.IdleStartTick == 0) st.IdleStartTick = tick;
                if (!st.BrainDeadReported && (tick - st.IdleStartTick) >= BrainDeadTicks)
                {
                    Report(tick, ent.Id, SimAnomalyKind.BrainDead, $"idle @ {tile.X},{tile.Y}");
                    st.BrainDeadReported = true;
                    Interlocked.Increment(ref _brainDeadTotal);
                }
            }
            else
            {
                st.IdleStartTick = 0;
                st.BrainDeadReported = false;
            }
        });

        if (_pawns.Count > seen.Count)
        {
            var dead = new List<int>();
            foreach (var k in _pawns.Keys) if (!seen.Contains(k)) dead.Add(k);
            foreach (var k in dead) _pawns.Remove(k);
        }
    }

    private void Report(long tick, int entityId, SimAnomalyKind kind, string detail)
    {
        var ev = new SimAnomaly(tick, entityId, kind, detail);
        _recent.Add(ev);
        if (_recent.Count > MaxRecent) _recent.RemoveAt(0);
        Volatile.Write(ref _publish, _recent.ToArray());
    }
}
