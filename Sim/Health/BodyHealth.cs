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

    public BodyPartDef(string id, string name, string? parent, (HealthCapacity, float)[] provides, bool internalPart)
    {
        Id = id; DisplayName = name; ParentId = parent; Provides = provides; Internal = internalPart;
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
        void Add(string id, string name, string? parent, bool internalPart, (HealthCapacity, float)[] provides)
        {
            var def = new BodyPartDef(id, name, parent, provides, internalPart);
            _all.Add(def); _byId[id] = def;
            if (parent is not null)
            {
                if (!_children.TryGetValue(parent, out var list)) { list = new(); _children[parent] = list; }
                list.Add(id);
            }
        }
        // Outer (punchable) part.
        void P(string id, string name, string? parent, params (HealthCapacity, float)[] provides)
            => Add(id, name, parent, false, provides);
        // Internal/abstract part (can't be hit by melee).
        void PI(string id, string name, string? parent, params (HealthCapacity, float)[] provides)
            => Add(id, name, parent, true, provides);

        PI("Body", "Body", null);
        P("Torso", "Torso", "Body");
        PI("Heart", "Heart", "Torso", (HealthCapacity.BloodPumping, 1f));
        PI("LungL", "Left Lung", "Torso", (HealthCapacity.Breathing, 0.5f));
        PI("LungR", "Right Lung", "Torso", (HealthCapacity.Breathing, 0.5f));
        P("Neck", "Neck", "Body");
        P("Head", "Head", "Neck");
        PI("Brain", "Brain", "Head", (HealthCapacity.Consciousness, 1f));
        P("EyeL", "Left Eye", "Head", (HealthCapacity.Sight, 0.5f));
        P("EyeR", "Right Eye", "Head", (HealthCapacity.Sight, 0.5f));
        P("EarL", "Left Ear", "Head", (HealthCapacity.Hearing, 0.5f));
        P("EarR", "Right Ear", "Head", (HealthCapacity.Hearing, 0.5f));
        P("ArmL", "Left Arm", "Body", (HealthCapacity.Manipulation, 0.5f));
        P("HandL", "Left Hand", "ArmL", (HealthCapacity.Manipulation, 0.5f));
        P("ArmR", "Right Arm", "Body", (HealthCapacity.Manipulation, 0.5f));
        P("HandR", "Right Hand", "ArmR", (HealthCapacity.Manipulation, 0.5f));
        P("LegL", "Left Leg", "Body", (HealthCapacity.Moving, 0.5f));
        P("FootL", "Left Foot", "LegL", (HealthCapacity.Moving, 0.5f));
        P("LegR", "Right Leg", "Body", (HealthCapacity.Moving, 0.5f));
        P("FootR", "Right Foot", "LegR", (HealthCapacity.Moving, 0.5f));

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

    // True if `id` or any ancestor is in the missing set.
    public static bool IsGone(string id, HashSet<string> missing)
    {
        string? cur = id;
        while (cur is not null)
        {
            if (missing.Contains(cur)) return true;
            cur = _byId[cur].ParentId;
        }
        return false;
    }

    // Per-unit-severity efficiency loss + bleed rate for a condition kind.
    public static float EfficiencyLoss(ConditionKind kind, float severity) => kind switch
    {
        ConditionKind.Bruise => 0.10f * severity,
        ConditionKind.Cut => 0.20f * severity,
        ConditionKind.Stab => 0.15f * severity, // less than a cut...
        ConditionKind.Burn => 0.25f * severity,
        ConditionKind.Scar => 0.10f,           // fixed, severity-independent
        ConditionKind.Missing => 1f,
        _ => 0f,
    };

    // Blood lost per sim-second from one condition at a given severity.
    public static float BleedRate(ConditionKind kind, float severity) => kind switch
    {
        ConditionKind.Cut => 0.020f * severity,
        ConditionKind.Stab => 0.030f * severity, // ...but bleeds more
        ConditionKind.Burn => 0.008f * severity,
        ConditionKind.Bruise => 0f,
        _ => 0f,
    };

    public static bool IsPermanent(ConditionKind kind)
        => kind == ConditionKind.Scar || kind == ConditionKind.Missing;

    // Pain contributed by one condition (summed across the body, clamped
    // to 0..1). Enough total pain causes pain-shock → unconscious.
    public static float Pain(ConditionKind kind, float severity) => kind switch
    {
        ConditionKind.Cut => 0.35f * severity,
        ConditionKind.Stab => 0.30f * severity,
        ConditionKind.Burn => 0.45f * severity,
        ConditionKind.Bruise => 0.15f * severity,
        ConditionKind.Scar => 0.05f,
        ConditionKind.Missing => 0.25f,
        _ => 0f,
    };
}
