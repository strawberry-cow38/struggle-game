using Friflo.Engine.ECS;
using StruggleGame.Sim.Bodies;

namespace StruggleGame.Sim.World;

// Rare-tick health advancer. Per colonist:
//   • Bleeds blood from open conditions (or slowly regenerates when not
//     bleeding).
//   • Evolves conditions — small ones heal, large untended ones worsen;
//     scars/missing are permanent.
//   • Recomputes body capacities from part efficiencies + blood, and flips
//     the Unconscious flag below the consciousness threshold.
// No death yet: blood floors at 0 and the colonist just stays passed out.
public sealed class HealthSystem
{
    public const float UnconsciousThreshold = 0.30f;
    public const float WorsenThreshold = 0.60f;   // severity at/above which an untended wound worsens
    public const float HealPerSec = 0.0002f;       // severity/sim-sec for small wounds
    public const float WorsenPerSec = 0.0001f;
    public const float BloodRegenPerSec = 0.0001f;
    // Blood (0..1 units) that must pool before a puddle is dripped.
    public const float PuddlePerDrip = 0.04f;

    // Health doesn't need 60 Hz; ~1s cadence keeps bleeding responsive.
    public const long TickInterval = 60;

    private readonly SimRuntime _sim;
    private float _accumDt;
    private readonly List<Map.TilePos> _dripScratch = new();

    // Wired by SimRuntime: drop/grow a blood puddle on a tile.
    public Action<Map.TilePos>? SpawnBloodPuddle;

    public HealthSystem(SimRuntime sim) { _sim = sim; }

    public void Step(EntityStore store, float dt)
    {
        _accumDt += dt;
        if (_sim.Tick % TickInterval != 0) return;
        float step = _accumDt;
        _accumDt = 0f;

        _dripScratch.Clear();
        store.Query<Health, WorldPos>().ForEachEntity((ref Health h, ref WorldPos pos, Entity _) =>
        {
            float bleed = TotalBleed(h);
            Advance(ref h, step);
            Recompute(ref h);
            // Drip a puddle (at most one per tick) when enough blood pools.
            if (bleed > 0f)
            {
                h.BleedAccum += bleed * step;
                if (h.BleedAccum >= PuddlePerDrip)
                {
                    h.BleedAccum -= PuddlePerDrip;
                    _dripScratch.Add(new Map.TilePos((int)pos.X, (int)pos.Y));
                }
            }
        });
        // Spawn outside the query — structural change.
        if (SpawnBloodPuddle is not null)
            foreach (var t in _dripScratch) SpawnBloodPuddle(t);
    }

    private static float TotalBleed(in Health h)
    {
        float bleed = 0f;
        if (h.Injuries is not null)
            foreach (var inj in h.Injuries)
                bleed += BodyTree.BleedRate(inj.Kind, inj.Severity);
        return bleed;
    }

    // Bleed/regen blood and evolve non-permanent conditions over `dt`
    // sim-seconds.
    public static void Advance(ref Health h, float dt)
    {
        float bleed = 0f;
        if (h.Injuries is not null)
            foreach (var inj in h.Injuries)
                bleed += BodyTree.BleedRate(inj.Kind, inj.Severity);

        if (bleed > 0f) h.BloodLevel = Math.Max(0f, h.BloodLevel - bleed * dt);
        else h.BloodLevel = Math.Min(1f, h.BloodLevel + BloodRegenPerSec * dt);

        if (h.Injuries is not null)
        {
            for (int i = h.Injuries.Count - 1; i >= 0; i--)
            {
                var inj = h.Injuries[i];
                if (BodyTree.IsPermanent(inj.Kind)) continue;
                if (inj.Severity >= WorsenThreshold)
                    inj.Severity = Math.Min(1f, inj.Severity + WorsenPerSec * dt);
                else
                    inj.Severity -= HealPerSec * dt;
                if (inj.Severity <= 0f) h.Injuries.RemoveAt(i);
                else h.Injuries[i] = inj;
            }
        }
    }

    // Recompute cached capacities + Unconscious from injuries + blood.
    public static void Recompute(ref Health h)
    {
        var missing = _missingScratch;
        missing.Clear();
        if (h.Injuries is not null)
            foreach (var inj in h.Injuries)
                if (inj.Kind == ConditionKind.Missing) missing.Add(inj.PartId);

        var num = _numScratch;
        System.Array.Clear(num, 0, num.Length);
        foreach (var part in BodyTree.All)
        {
            if (part.Provides.Length == 0) continue;
            float eff;
            if (BodyTree.IsGone(part.Id, missing)) eff = 0f;
            else
            {
                float loss = 0f;
                if (h.Injuries is not null)
                    foreach (var inj in h.Injuries)
                        if (inj.PartId == part.Id && inj.Kind != ConditionKind.Missing)
                            loss += BodyTree.EfficiencyLoss(inj.Kind, inj.Severity);
                eff = Math.Clamp(1f - loss, 0f, 1f);
            }
            foreach (var (cap, w) in part.Provides)
                num[(int)cap] += eff * w;
        }

        float Cap(HealthCapacity c)
        {
            float total = BodyTree.TotalWeight(c);
            return total > 0f ? num[(int)c] / total : 1f;
        }

        float consciousnessRaw = Cap(HealthCapacity.Consciousness); // brain
        float bloodPumpRaw = Cap(HealthCapacity.BloodPumping);      // heart
        float breathingRaw = Cap(HealthCapacity.Breathing);
        float effectiveBloodPump = bloodPumpRaw * h.BloodLevel;
        float consciousness = Math.Clamp(consciousnessRaw * effectiveBloodPump * breathingRaw, 0f, 1f);

        h.Consciousness = consciousness;
        h.BloodPumping = effectiveBloodPump;
        h.Breathing = breathingRaw;
        h.Sight = Cap(HealthCapacity.Sight);
        // Moving + Manipulation are gated by consciousness — pass out and
        // you can't walk or work.
        h.Moving = Math.Clamp(Cap(HealthCapacity.Moving) * consciousness, 0f, 1f);
        h.Manipulation = Math.Clamp(Cap(HealthCapacity.Manipulation) * consciousness, 0f, 1f);
        h.Unconscious = consciousness < UnconsciousThreshold;
    }

    [ThreadStatic] private static HashSet<string>? _missingScratchTls;
    [ThreadStatic] private static float[]? _numScratchTls;
    private static HashSet<string> _missingScratch => _missingScratchTls ??= new HashSet<string>();
    private static float[] _numScratch => _numScratchTls ??= new float[System.Enum.GetValues(typeof(HealthCapacity)).Length];
}
