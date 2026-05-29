using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Drains RecreationNeed while the pawn is not at a recreation source;
// refills it (at the kind-specific power rate) while AtRecreation is
// present. Also advances the per-pawn 12h preference roll timer and
// re-picks from the currently available recreation pool when it hits
// zero. Build / decon of the Ur board entity itself + seat reservation
// live in DummyController + SimRuntime — this system is pure
// accounting + the preference roll, so the math is testable in isolation.
public sealed class RecreationSystem
{
    // 16h of zero recreation = full → empty.
    public const double AwakeSecToEmpty = 16.0 * 3600.0;
    // Below this the pawn drops other plans and seeks recreation.
    public const float SeekThreshold = 0.15f;
    // Re-roll preference every 12 sim-hours.
    public const float PreferenceRollSec = 12f * 3600f;
    // Per-kind refill power (fraction of full need per sim-hour while
    // engaged). Spectating is the watched activity's power minus 10%;
    // DummyController fills this in based on the board kind it spectates.
    public const float UrPower = 0.85f;
    public const float SpectatingPenalty = 0.10f;
    // Up to 8 spectators per board. Players + spectators are picked from
    // tiles within this Chebyshev radius (line of sight not enforced for
    // now — clumping around an open board on open ground is fine).
    public const int SpectatorRadius = 3;
    public const int MaxSpectators = 8;
    public const int PlayerSeats = 2;

    public delegate IReadOnlyList<RecreationKind> AvailableKindsProvider();

    private readonly Random _rng;
    private readonly AvailableKindsProvider _availableKinds;

    public RecreationSystem(int seed, AvailableKindsProvider availableKinds)
    {
        _rng = new Random(seed);
        _availableKinds = availableKinds;
    }

    // Per-kind refill rate in "fraction per sim-second".
    public static float PowerPerSec(RecreationKind k) => k switch
    {
        RecreationKind.Ur          => UrPower / 3600f,
        RecreationKind.Spectating  => (UrPower - SpectatingPenalty) / 3600f,
        _                          => 0f,
    };

    public void Step(EntityStore store, float dt)
    {
        float drainPerSimSec = (float)(1.0 / AwakeSecToEmpty);
        float simDt = (float)(SimRuntime.SimSecondsPerRealSecond * dt);
        float drain = drainPerSimSec * simDt;

        // Resolve the live pool once per Step so the roll branch isn't
        // re-querying SimRuntime for every pawn.
        IReadOnlyList<RecreationKind> pool = _availableKinds();

        // TEMP 2026-05-29: recreation pinned to 1.0 until seek/sit/leave
        // bugs are fixed. Disables drain/gain so pawns never seek Ur boards.
        store.Query<RecreationNeed>().ForEachEntity((ref RecreationNeed need, Entity ent) =>
        {
            need.Level = 1f;
        });

        store.Query<RecreationPreference>().ForEachEntity((ref RecreationPreference pref, Entity ent) =>
        {
            pref.SecondsUntilRoll -= simDt;
            // Initial roll: byte 255 sentinel means "never rolled".
            bool needFirstRoll = (byte)pref.Kind == 255;
            if (!needFirstRoll && pref.SecondsUntilRoll > 0f) return;
            if (pool.Count == 0)
            {
                // Nothing to roll into — leave the existing preference
                // alone (or sentinel if never rolled). Re-check soon
                // rather than wait the full 12h.
                pref.SecondsUntilRoll = 3600f;
                return;
            }
            pref.Kind = pool[_rng.Next(pool.Count)];
            pref.SecondsUntilRoll = PreferenceRollSec;
        });
    }
}
