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
// Death: losing a vital part or dropping consciousness to zero kills the
// colonist (SimRuntime.KillColonist drops a corpse + their gear).
public sealed class HealthSystem
{
    public const float UnconsciousThreshold = 0.30f;
    public const float PainShockThreshold = 0.80f;  // total pain that downs a colonist
    public const float HealPerSecHp = 0.03f;         // hit-points/sim-sec an untended wound recovers
    public const float BloodRegenPerSec = 0.0001f;
    // Tending: a tended wound sheds severity (1 + TendHealQualityBonus*quality)×
    // faster and its pain is cut by TendPainFactor*quality. Stabilized wounds
    // keep StabilizeBleedFraction of their bleed; tended wounds bleed none.
    public const float TendHealQualityBonus = 8f;    // quality 0.75 → ~7× heal rate
    public const float TendPainFactor = 0.6f;        // quality 0.75 → ~45% less pain
    public const float StabilizeBleedFraction = 0.25f; // 75% bleed stopped
    // Blood (0..1 units) that must pool before a puddle is dripped.
    public const float PuddlePerDrip = 0.04f;

    // Health doesn't need 60 Hz; ~1s cadence keeps bleeding responsive.
    public const long TickInterval = 60;

    private readonly SimRuntime _sim;
    private float _accumDt;
    private readonly List<Map.TilePos> _dripScratch = new();

    // Cached queries — Store.Query<>() allocates a query object per call.
    private ArchetypeQuery<Health, WorldPos>? _healthQ;
    private readonly List<int> _downedScratch = new();
    private readonly List<int> _deadScratch = new();

    // Wired by SimRuntime: drop/grow a blood puddle on a tile; drop a
    // freshly-downed colonist's gear; turn a dead colonist into a corpse.
    public Action<Map.TilePos>? SpawnBloodPuddle;
    public Action<int>? OnDowned;
    public Action<int>? OnDied;

    public HealthSystem(SimRuntime sim) { _sim = sim; }

    public void Step(EntityStore store, float dt)
    {
        _accumDt += dt;
        if (_sim.Tick % TickInterval != 0) return;
        float step = _accumDt;
        _accumDt = 0f;

        _dripScratch.Clear();
        _downedScratch.Clear();
        _deadScratch.Clear();
        (_healthQ ??= store.Query<Health, WorldPos>()).ForEachEntity((ref Health h, ref WorldPos pos, Entity e) =>
        {
            float bleed = TotalBleed(h);
            Advance(ref h, step);
            Recompute(ref h);
            // Consciousness floored to zero = death (bled out / lost brain,
            // heart, or both lungs). Pain shock only downs, never kills.
            if (h.Consciousness <= 0f) { _deadScratch.Add(e.Id); h.WasDowned = true; return; }
            // Down -> drop gear on the transition into unconsciousness.
            if (h.Unconscious && !h.WasDowned) _downedScratch.Add(e.Id);
            h.WasDowned = h.Unconscious;
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
        // Structural work happens outside the query.
        if (SpawnBloodPuddle is not null)
            foreach (var t in _dripScratch) SpawnBloodPuddle(t);
        if (OnDowned is not null)
            foreach (var id in _downedScratch) OnDowned(id);
        if (OnDied is not null)
            foreach (var id in _deadScratch) OnDied(id);
    }

    private static float TotalBleed(in Health h)
    {
        float bleed = 0f;
        if (h.Injuries is not null)
            foreach (var inj in h.Injuries)
                bleed += BleedOf(inj);
        return bleed;
    }

    // Bleed/regen blood and slowly heal non-permanent wounds (damage in hit
    // points) over `dt` sim-seconds. No tending yet, so wounds just clot +
    // shrink on their own; scars/missing parts never heal.
    public static void Advance(ref Health h, float dt)
    {
        float bleed = 0f;
        if (h.Injuries is not null)
            foreach (var inj in h.Injuries)
                bleed += BleedOf(inj);

        if (bleed > 0f) h.BloodLevel = Math.Max(0f, h.BloodLevel - bleed * dt);
        else h.BloodLevel = Math.Min(1f, h.BloodLevel + BloodRegenPerSec * dt);

        if (h.Injuries is not null)
        {
            for (int i = h.Injuries.Count - 1; i >= 0; i--)
            {
                var inj = h.Injuries[i];
                if (BodyTree.IsPermanent(inj.Kind)) continue;
                // Tended wounds shed severity far faster (scaled by quality).
                float rate = HealPerSecHp * (inj.Tended ? 1f + TendHealQualityBonus * inj.TendQuality : 1f);
                inj.Severity -= rate * dt; // Severity == damage in HP
                if (inj.Severity <= 0f) h.Injuries.RemoveAt(i);
                else h.Injuries[i] = inj;
            }
        }
    }

    // Per-injury bleed after treatment: tended = none, stabilized = a quarter,
    // else full.
    public static float BleedOf(in PartInjury inj)
    {
        if (inj.Tended) return 0f;
        float b = BodyTree.BleedRate(inj.Kind, inj.Severity);
        return inj.Stabilized ? b * StabilizeBleedFraction : b;
    }

    // Recompute cached capacities + Unconscious from injuries + blood.
    public static void Recompute(ref Health h)
    {
        // Destruction pass: any part whose accumulated (non-missing) damage
        // meets/exceeds its MaxHp is shot off — its wounds clear and it
        // becomes Missing. Descendants are handled by IsGone (ancestor check),
        // and death from losing a vital part (brain/heart/torso) falls out of
        // the consciousness chain below.
        if (h.Injuries is not null)
            DestroyOverdamagedParts(h.Injuries);

        var missing = _missingScratch;
        missing.Clear();
        float pain = 0f;
        if (h.Injuries is not null)
            foreach (var inj in h.Injuries)
            {
                if (inj.Kind == ConditionKind.Missing) missing.Add(inj.PartId);
                // Tending cuts a wound's pain (scaled by quality).
                float p = BodyTree.Pain(inj.Kind, inj.Severity);
                if (inj.Tended) p *= 1f - TendPainFactor * inj.TendQuality;
                pain += p;
            }
        pain = Math.Clamp(pain, 0f, 1f);
        h.Pain = pain;

        var num = _numScratch;
        System.Array.Clear(num, 0, num.Length);
        foreach (var part in BodyTree.All)
        {
            if (part.Provides.Length == 0) continue;
            float eff;
            if (BodyTree.IsGone(part.Id, missing)) eff = 0f;
            else
            {
                // Efficiency = remaining HP / max HP.
                float dmg = 0f;
                if (h.Injuries is not null)
                    foreach (var inj in h.Injuries)
                        if (inj.PartId == part.Id && inj.Kind != ConditionKind.Missing)
                            dmg += inj.Severity;
                eff = part.MaxHp > 0f ? Math.Clamp((part.MaxHp - dmg) / part.MaxHp, 0f, 1f) : 1f;
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
        // Pass out from low consciousness OR from pain shock — the latter
        // lets even non-bleeding injuries (e.g. lots of bruises) down a
        // colonist.
        h.Unconscious = consciousness < UnconsciousThreshold || pain >= PainShockThreshold;
    }

    // Mark any part whose summed (non-missing) damage >= its MaxHp as Missing,
    // clearing its other wounds. Mutates the injury list in place.
    private static void DestroyOverdamagedParts(List<PartInjury> injuries)
    {
        var dmg = _dmgScratch; dmg.Clear();
        var alreadyMissing = _missingScratch2; alreadyMissing.Clear();
        foreach (var inj in injuries)
        {
            if (inj.Kind == ConditionKind.Missing) { alreadyMissing.Add(inj.PartId); continue; }
            dmg[inj.PartId] = dmg.GetValueOrDefault(inj.PartId) + inj.Severity;
        }
        List<string>? destroy = null;
        foreach (var kv in dmg)
        {
            if (alreadyMissing.Contains(kv.Key)) continue;
            float max = BodyTree.MaxHp(kv.Key);
            if (max > 0f && kv.Value >= max) (destroy ??= new()).Add(kv.Key);
        }
        if (destroy is null) return;
        foreach (var part in destroy)
        {
            injuries.RemoveAll(i => i.PartId == part && i.Kind != ConditionKind.Missing);
            injuries.Add(new PartInjury { PartId = part, Kind = ConditionKind.Missing, Severity = 1f });
        }
    }

    [ThreadStatic] private static HashSet<string>? _missingScratchTls;
    [ThreadStatic] private static float[]? _numScratchTls;
    [ThreadStatic] private static Dictionary<string, float>? _dmgScratchTls;
    [ThreadStatic] private static HashSet<string>? _missingScratch2Tls;
    private static HashSet<string> _missingScratch => _missingScratchTls ??= new HashSet<string>();
    private static float[] _numScratch => _numScratchTls ??= new float[System.Enum.GetValues(typeof(HealthCapacity)).Length];
    private static Dictionary<string, float> _dmgScratch => _dmgScratchTls ??= new Dictionary<string, float>();
    private static HashSet<string> _missingScratch2 => _missingScratch2Tls ??= new HashSet<string>();
}
