namespace StruggleGame.Sim.Bodies;

// Body capacities — derived from body-part efficiencies, then drive
// colonist stats (move/work speed) and the conscious/unconscious gate.
public enum HealthCapacity : byte
{
    Consciousness = 0,
    Moving = 1,
    Manipulation = 2,
    Sight = 3,
    BloodPumping = 4,
    Breathing = 5,
    Hearing = 6,
}

// Kinds of condition a body part can have. Cut/Burn/Bruise evolve over
// time (small heal, large worsen untended); Scar + Missing are permanent.
public enum ConditionKind : byte
{
    Bruise = 0,
    Cut = 1,
    Burn = 2,
    Scar = 3,    // permanent, small fixed efficiency loss, no bleed
    Missing = 4, // part (and its descendants) gone
    Stab = 5,    // puncture — bleeds heavily, heals like a cut
    Gunshot = 6, // bullet wound — heavy damage + heavy bleed, heals like a cut
    Sickness = 7, // whole-body condition (illness, malnutrition, …) — no bleed
}

// Static definition of one body part in the hierarchy.
public sealed class BodyPartDef
{
    public string Id { get; }
    public string DisplayName { get; }
    public string? ParentId { get; }
    // Capacities this part contributes to, with a weight. A capacity's
    // value = sum(partEfficiency*weight) / sum(weight) across all parts
    // that provide it, so full health = 1.0.
    public (HealthCapacity Cap, float Weight)[] Provides { get; }
    // Internal/abstract parts (organs, brain, the Body root) can't be hit
    // by a melee punch — only outer parts are valid targets.
    public bool Internal { get; }
    // Max hit points (RimWorld-ish). Damage on the part subtracts from this;
    // efficiency = remaining/max; accumulated damage >= MaxHp destroys it.
    public float MaxHp { get; }

    public BodyPartDef(string id, string name, string? parent, (HealthCapacity, float)[] provides, bool internalPart, float maxHp)
    {
        Id = id; DisplayName = name; ParentId = parent; Provides = provides; Internal = internalPart; MaxHp = maxHp;
    }
}

// The human body tree + capacity bookkeeping. Static — every colonist
// shares the same layout; per-pawn state is just the injury list + blood.
public static class BodyTree
{
    private static readonly List<BodyPartDef> _all = new();
    private static readonly Dictionary<string, BodyPartDef> _byId = new();
    private static readonly Dictionary<string, List<string>> _children = new();
    private static readonly Dictionary<HealthCapacity, float> _capacityTotalWeight = new();

    public static IReadOnlyList<BodyPartDef> All => _all;
    public static BodyPartDef Get(string id) => _byId[id];
    public static bool TryGet(string id, out BodyPartDef def) => _byId.TryGetValue(id, out def!);
    public static IReadOnlyList<string> ChildrenOf(string id)
        => _children.TryGetValue(id, out var c) ? c : (IReadOnlyList<string>)System.Array.Empty<string>();

    static BodyTree()
    {
        void Add(string id, string name, string? parent, bool internalPart, float maxHp, (HealthCapacity, float)[] provides)
        {
            var def = new BodyPartDef(id, name, parent, provides, internalPart, maxHp);
            _all.Add(def); _byId[id] = def;
            if (parent is not null)
            {
                if (!_children.TryGetValue(parent, out var list)) { list = new(); _children[parent] = list; }
                list.Add(id);
            }
        }
        // Outer (punchable) part.
        void P(string id, string name, string? parent, float maxHp, params (HealthCapacity, float)[] provides)
            => Add(id, name, parent, false, maxHp, provides);
        // Internal/abstract part (can't be hit by melee).
        void PI(string id, string name, string? parent, float maxHp, params (HealthCapacity, float)[] provides)
            => Add(id, name, parent, true, maxHp, provides);

        // MaxHp values are RimWorld-ish.
        PI("Body", "Body", null, 100f);
        P("Torso", "Torso", "Body", 40f);
        PI("Heart", "Heart", "Torso", 15f, (HealthCapacity.BloodPumping, 1f));
        PI("LungL", "Left Lung", "Torso", 15f, (HealthCapacity.Breathing, 0.5f));
        PI("LungR", "Right Lung", "Torso", 15f, (HealthCapacity.Breathing, 0.5f));
        P("Neck", "Neck", "Body", 25f);
        P("Head", "Head", "Neck", 25f);
        PI("Brain", "Brain", "Head", 12f, (HealthCapacity.Consciousness, 1f));
        P("EyeL", "Left Eye", "Head", 10f, (HealthCapacity.Sight, 0.5f));
        P("EyeR", "Right Eye", "Head", 10f, (HealthCapacity.Sight, 0.5f));
        P("EarL", "Left Ear", "Head", 10f, (HealthCapacity.Hearing, 0.5f));
        P("EarR", "Right Ear", "Head", 10f, (HealthCapacity.Hearing, 0.5f));
        P("ArmL", "Left Arm", "Body", 30f, (HealthCapacity.Manipulation, 0.5f));
        P("HandL", "Left Hand", "ArmL", 20f, (HealthCapacity.Manipulation, 0.5f));
        P("ArmR", "Right Arm", "Body", 30f, (HealthCapacity.Manipulation, 0.5f));
        P("HandR", "Right Hand", "ArmR", 20f, (HealthCapacity.Manipulation, 0.5f));
        P("LegL", "Left Leg", "Body", 30f, (HealthCapacity.Moving, 0.5f));
        P("FootL", "Left Foot", "LegL", 20f, (HealthCapacity.Moving, 0.5f));
        P("LegR", "Right Leg", "Body", 30f, (HealthCapacity.Moving, 0.5f));
        P("FootR", "Right Foot", "LegR", 20f, (HealthCapacity.Moving, 0.5f));

        foreach (var def in _all)
        {
            foreach (var (cap, w) in def.Provides)
                _capacityTotalWeight[cap] = _capacityTotalWeight.GetValueOrDefault(cap) + w;
            if (!def.Internal) _punchable.Add(def.Id);
        }
    }

    // Outer parts a melee hit can land on (organs/brain/root excluded).
    private static readonly List<string> _punchable = new();
    public static IReadOnlyList<string> PunchableParts => _punchable;

    public static float TotalWeight(HealthCapacity cap) => _capacityTotalWeight.GetValueOrDefault(cap);

    // True if `id` or any ancestor is in the missing set. Tolerates virtual /
    // unknown ids (e.g. "WholeBody") that aren't real body parts.
    public static bool IsGone(string id, HashSet<string> missing)
    {
        string? cur = id;
        while (cur is not null)
        {
            if (missing.Contains(cur)) return true;
            cur = _byId.TryGetValue(cur, out var d) ? d.ParentId : null;
        }
        return false;
    }

    // Max hit points of a part (0 if unknown).
    public static float MaxHp(string partId) => _byId.TryGetValue(partId, out var d) ? d.MaxHp : 0f;

    // NOTE: injury "severity" now means DAMAGE in hit points (RimWorld-style),
    // not a 0..1 fraction. Part efficiency = remaining/max is computed in
    // HealthSystem; bleed + pain below scale per hit-point of damage.

    // Blood lost per sim-second per hit-point of damage from a condition.
    // (Tuned loosely; balance pass to follow.)
    public static float BleedRate(ConditionKind kind, float damage) => kind switch
    {
        ConditionKind.Cut => 0.00010f * damage,
        ConditionKind.Stab => 0.00014f * damage, // punctures bleed more
        ConditionKind.Gunshot => 0.00018f * damage, // bleeds hardest
        ConditionKind.Burn => 0.00005f * damage,
        ConditionKind.Bruise => 0f,
        _ => 0f,
    };

    public static bool IsPermanent(ConditionKind kind)
        => kind == ConditionKind.Scar || kind == ConditionKind.Missing;

    // Pain contributed by one condition per hit-point of damage (summed across
    // the body, clamped 0..1). Enough total pain causes pain-shock → down.
    public static float Pain(ConditionKind kind, float damage) => kind switch
    {
        ConditionKind.Cut => 0.013f * damage,
        ConditionKind.Stab => 0.014f * damage,
        ConditionKind.Gunshot => 0.016f * damage,
        ConditionKind.Burn => 0.018f * damage,
        ConditionKind.Bruise => 0.010f * damage,
        ConditionKind.Scar => 0.003f * damage,
        ConditionKind.Missing => 4f,   // a lost part hurts a lot, flat
        _ => 0f,
    };
}
