using StruggleGame.Sim.Bodies;

namespace StruggleGame.Sim.Items;

// Tree node in the item taxonomy. A category can hold both
// subcategories AND items (e.g. "Resources" might hold the "Wood"
// subcategory AND a direct "Misc" item — current code doesn't, but
// the shape supports it). Subcategories nest arbitrarily deep.
public sealed class ItemCategory
{
    public string Id { get; }
    public string DisplayName { get; }
    public ItemCategory? Parent { get; }
    public IReadOnlyList<ItemCategory> Subcategories => _subs;
    public IReadOnlyList<ItemDef> Items => _items;

    private readonly List<ItemCategory> _subs = new();
    private readonly List<ItemDef> _items = new();

    internal ItemCategory(string id, string displayName, ItemCategory? parent)
    {
        Id = id;
        DisplayName = displayName;
        Parent = parent;
    }

    internal void AddSub(ItemCategory c) => _subs.Add(c);
    internal void AddItem(ItemDef i) => _items.Add(i);

    // Slash-joined path from the root, used as a stable filter key for
    // stockpile UI state (so a saved filter survives renaming a leaf).
    public string FullPath => Parent is null ? Id : $"{Parent.FullPath}/{Id}";
}

// Which fire mode a colonist's ranged weapon is currently set to.
public enum FireMode : byte { Single = 0, Burst = 1, Auto = 2 }

// Body region a ranged colonist aims for (sets the shot's aim height).
// Default 0 = Auto (center mass / whatever's showing).
public enum TargetArea : byte { Auto = 0, Torso = 1, Head = 2, Legs = 3 }

// How a pawn fires: Aimed = full aim-time + normal accuracy; Snapshot = no aim
// time but a big accuracy penalty (CQB); Auto = picks Snapshot for very close
// targets (within SnapshotRangeFraction of weapon range), Aimed otherwise.
public enum AimMode : byte { Aimed = 0, Snapshot = 1, Auto = 2 }

// Bitmask of the fire modes a weapon supports. The action bar only
// offers buttons for the flags present here.
[System.Flags]
public enum FireModeFlags : byte { None = 0, Single = 1, Burst = 2, Auto = 4 }

// Ranged-weapon stat block. Attached to an ItemDef that fires
// projectiles. Rounds fly a real ballistic arc (height + gravity) and
// resolve against height-aware cover (sandbags / crouch / lean).
public sealed class RangedSpec
{
    public float Range;                // max firing distance, tiles
    public string AmmoCategoryPath = "";  // accepts ammo whose AmmoSpec.CategoryPath matches
    public int MagazineSize;
    public FireModeFlags Modes;        // which fire modes the gun supports
    public int BurstShots;             // shots per pull in Burst mode
    public int PelletsPerShot = 1;     // pellets fired per shell (shotguns > 1)
    public float ProjectileSpeed;      // tiles per second
    // Aim dispersion: shots scatter within a cone of (SpreadDegrees + recoil)
    // half-angle; the radius at the target = tan(angle) * distance.
    public float SpreadDegrees;        // inherent weapon spread (steady)
    public float RecoilPerShot;        // degrees added to the cone each shot
    public float RecoilRecoverPerSec;  // degrees the cone settles per second
    public float MaxRecoilDegrees;     // recoil cap
    public long WarmupTicks;           // (legacy) aim time before a burst's first shot
    public long AimTicks;              // PER-TARGET aim: spot-to-first-shot delay,
                                       // reset on target change / lost LoS / reload
    public long ShotCooldownTicks;     // between shots inside a burst / auto
    public long CycleCooldownTicks;    // between bursts (and between single shots)
    public long ReloadTicks;           // mag refill time
}

// Ammo stat block. The loaded round decides the wound — AP vs HP is
// just different numbers here (ArmorPen reserved for future cover math).
public sealed class AmmoSpec
{
    public string CategoryPath = "";   // matches a weapon's RangedSpec.AmmoCategoryPath
    public ConditionKind InjuryKind;   // typically Gunshot
    public float Damage;               // hit-point damage per hit (RimWorld/CE scale)
    public float PenSharp;             // sharp armor penetration, mmRHA
    public float PenBlunt;             // blunt/concussive penetration, MPa — banked for the armor system
}

// Worn-armor stat block. Defends the listed body parts: a round with sharp
// penetration ≤ ArmorSharp is deflected (becomes a blunt bruise); above it,
// the round penetrates with reduced damage. ArmorBlunt soaks the concussion.
public sealed class ArmorSpec
{
    public float ArmorSharp;   // sharp defense, mmRHA
    public float ArmorBlunt;   // blunt defense, MPa
    public string[] Covers = System.Array.Empty<string>(); // protected body-part ids
}

// Leaf of the taxonomy. One per concrete item kind that can exist
// in the world (wood, stone, raw meat, …). Stockpile filters and
// haul jobs reference items by Def, not by string.
public sealed class ItemDef
{
    public string Id { get; }
    public string DisplayName { get; }
    public ItemCategory Category { get; }
    // Per-unit carry cost. A colonist's inventory caps both: weight is
    // mass-like (one heavy item; bulk is volume-like (one fluffy item).
    // Tuned in concert with SimConstants.MaxCarryWeight / MaxCarryBulk.
    public float Weight { get; }
    public float Bulk { get; }
    // True if a colonist can equip this into an equipment slot (and the
    // RMB "Equip" order shows up on a dropped pile of it). Equipping
    // moves one unit into the pawn's equipped slots, which never auto-drop.
    public bool Equippable { get; }
    // Melee attacks this item grants when equipped + used as a weapon. A
    // swing picks one at random. Empty = not a weapon (bare-fist bruise).
    public (ConditionKind Kind, float Severity)[] MeleeAttacks { get; }
    public bool IsWeapon => MeleeAttacks.Length > 0;
    // Whether a freshly-created stockpile accepts this item. Corpses are
    // off by default — the player opts in per stockpile.
    public bool DefaultStockpileAllowed { get; }
    // Max units in one ground/stockpile stack before it splits into another.
    public int MaxStack { get; }
    // Ranged-weapon stats when equipped, else null. Equipping a ranged
    // weapon attaches a RangedCombat component (mag + fire mode).
    public RangedSpec? Ranged { get; }
    public bool IsRangedWeapon => Ranged is not null;
    // Ammo stats when this item is a round of ammunition, else null.
    public AmmoSpec? Ammo { get; }
    public bool IsAmmo => Ammo is not null;
    // Worn-armor stats when this item is apparel, else null.
    public ArmorSpec? Armor { get; }
    public bool IsArmor => Armor is not null;

    internal ItemDef(string id, string displayName, ItemCategory category, float weight, float bulk, bool equippable, (ConditionKind, float)[]? meleeAttacks, bool defaultStockpileAllowed, RangedSpec? ranged, AmmoSpec? ammo, int maxStack, ArmorSpec? armor)
    {
        Id = id;
        DisplayName = displayName;
        Category = category;
        Weight = weight;
        Bulk = bulk;
        Equippable = equippable;
        MeleeAttacks = meleeAttacks ?? System.Array.Empty<(ConditionKind, float)>();
        DefaultStockpileAllowed = defaultStockpileAllowed;
        Ranged = ranged;
        Ammo = ammo;
        MaxStack = maxStack;
        Armor = armor;
    }

    public string FullPath => $"{Category.FullPath}/{Id}";
}

// Process-global registry. Static-init time seeds the built-in
// categories and items. Adding a new item later is one
// RegisterItem(...) call from anywhere before first lookup.
public static class ItemCatalog
{
    private static readonly Dictionary<string, ItemCategory> _categoriesByPath = new();
    private static readonly Dictionary<string, ItemDef> _itemsByPath = new();
    private static readonly List<ItemCategory> _roots = new();

    public static IReadOnlyList<ItemCategory> Roots => _roots;
    public static IReadOnlyDictionary<string, ItemCategory> CategoriesByPath => _categoriesByPath;
    public static IReadOnlyDictionary<string, ItemDef> ItemsByPath => _itemsByPath;

    // Built-in catalog handles. New ones can be added the same way
    // — register under an existing parent or as a new root.
    public static readonly ItemCategory Resources;
    public static readonly ItemCategory ResourcesWood;
    public static readonly ItemDef Wood;
    public static readonly ItemCategory ResourcesFood;
    public static readonly ItemDef Carrot;
    public static readonly ItemDef SimpleMeal;
    public static readonly ItemCategory Equipment;
    // Dummy equippable. Placeholder until real apparel/weapons exist —
    // it does nothing but sit in an equipped slot and draw on the pawn.
    public static readonly ItemDef WoodenTrinket;
    public static readonly ItemCategory Corpses;
    // A dead colonist's body as a haulable item. Carries a Corpse data
    // component for resurrection. Heavy; stockpiles reject it by default.
    public static readonly ItemDef Corpse;
    // First ranged weapon — ships with all three fire modes for testing.
    public static readonly ItemDef AssaultRifle;
    // Torso armor. Stops most rounds (deflect → bruise); AP punches through.
    public static readonly ItemDef KevlarVest;
    public static readonly ItemCategory Ammo;
    // Rifle ammo variants: AP penetrates (future), HP wounds harder, FMJ
    // balanced in between.
    public static readonly ItemDef RifleAmmoAp;
    public static readonly ItemDef RifleAmmoHp;
    public static readonly ItemDef RifleAmmoFmj;
    // MP5 9mm SMG — low damage, high RoF, short range.
    public static readonly ItemDef SubmachineGun;
    public static readonly ItemDef Ammo9mm;
    // M700 7.62x51 bolt rifle — high damage, long range, slow.
    public static readonly ItemDef BoltActionRifle;
    public static readonly ItemDef Ammo762x51;
    // AKM 7.62x39 — punchy assault rifle, more recoil + less precise than the M16.
    public static readonly ItemDef Akm;
    public static readonly ItemDef Ammo762x39;
    // AUG 5.56 bullpup — accurate, shares the M16's Rifle ammo.
    public static readonly ItemDef Aug;
    // M249 SAW — belt-fed 5.56 LMG: full-auto only, big belt, heavy.
    public static readonly ItemDef Lmg;
    // AA-12 — full-auto combat shotgun: 8-pellet 12-gauge shells, drum mag.
    public static readonly ItemDef AutoShotgun;
    public static readonly ItemDef Ammo12ga;
    // CP33 — .22 LR pistol: 33-round mag, low damage, fast, light sidearm.
    public static readonly ItemDef Pistol;
    public static readonly ItemDef Ammo22lr;
    // G3 — 7.62x51 battle rifle: select-fire, hard-hitting, heavy recoil.
    public static readonly ItemDef BattleRifle;
    // RPD — belt-fed 7.62x39 LMG: full-auto, big belt, heavy. Shares AKM ammo.
    public static readonly ItemDef LmgRpd;
    // Consumed per tend/stabilize job.
    public static readonly ItemDef Medicine;

    static ItemCatalog()
    {
        Resources = RegisterCategory("Resources", "Resources");
        ResourcesWood = RegisterCategory("Wood", "Wood", Resources);
        Wood = RegisterItem("Wood", "Wood", ResourcesWood, weight: 1f, bulk: 1f);
        ResourcesFood = RegisterCategory("Food", "Food", Resources);
        Carrot = RegisterItem("Carrot", "Carrot", ResourcesFood, weight: 0.05f, bulk: 0.05f);
        Medicine = RegisterItem("Medicine", "Medicine", Resources, weight: 0.2f, bulk: 0.15f, maxStack: 25);
        SimpleMeal = RegisterItem("SimpleMeal", "Simple Meal", ResourcesFood, weight: 0.4f, bulk: 0.4f);
        Equipment = RegisterCategory("Equipment", "Equipment");
        // Melee attacks now deal hit-point damage (RimWorld scale).
        WoodenTrinket = RegisterItem("WoodenTrinket", "Wooden Trinket", Equipment, weight: 2f, bulk: 1f, equippable: true,
            meleeAttacks: new (ConditionKind, float)[] { (ConditionKind.Cut, 6f), (ConditionKind.Stab, 5f) });
        Corpses = RegisterCategory("Corpses", "Corpses");
        Corpse = RegisterItem("Colonist", "Corpse", Corpses, weight: 40f, bulk: 40f, defaultStockpileAllowed: false);

        Ammo = RegisterCategory("Ammo", "Ammo");
        // CE 5.56x45mm NATO values: Damage / PenSharp(mmRHA) / PenBlunt(MPa).
        RifleAmmoAp = RegisterItem("RifleAmmoAP", "5.56x45mm NATO (AP)", Ammo, weight: 0.02f, bulk: 0.02f, maxStack: 500,
            ammo: new AmmoSpec { CategoryPath = "Rifle", InjuryKind = ConditionKind.Gunshot, Damage = 9f, PenSharp = 12f, PenBlunt = 34f });
        RifleAmmoHp = RegisterItem("RifleAmmoHP", "5.56x45mm NATO (HP)", Ammo, weight: 0.02f, bulk: 0.02f, maxStack: 500,
            ammo: new AmmoSpec { CategoryPath = "Rifle", InjuryKind = ConditionKind.Gunshot, Damage = 18f, PenSharp = 3f, PenBlunt = 34f });
        RifleAmmoFmj = RegisterItem("RifleAmmoFMJ", "5.56x45mm NATO (FMJ)", Ammo, weight: 0.02f, bulk: 0.02f, maxStack: 500,
            ammo: new AmmoSpec { CategoryPath = "Rifle", InjuryKind = ConditionKind.Gunshot, Damage = 14f, PenSharp = 6f, PenBlunt = 34f });
        // 9x19mm — pistol/SMG round: low damage, modest pen.
        Ammo9mm = RegisterItem("Ammo9mm", "9x19mm Parabellum", Ammo, weight: 0.012f, bulk: 0.012f, maxStack: 500,
            ammo: new AmmoSpec { CategoryPath = "9mm", InjuryKind = ConditionKind.Gunshot, Damage = 8f, PenSharp = 4f, PenBlunt = 24f });
        // 7.62x51mm NATO — full-power rifle round: high damage + penetration.
        Ammo762x51 = RegisterItem("Ammo762x51", "7.62x51mm NATO", Ammo, weight: 0.025f, bulk: 0.025f, maxStack: 500,
            ammo: new AmmoSpec { CategoryPath = "762x51", InjuryKind = ConditionKind.Gunshot, Damage = 32f, PenSharp = 16f, PenBlunt = 45f });
        AssaultRifle = RegisterItem("AssaultRifle", "M16A2", Equipment, weight: 4f, bulk: 3f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 50f,
                AmmoCategoryPath = "Rifle",
                MagazineSize = 30,
                Modes = FireModeFlags.Single | FireModeFlags.Burst, // M16A2: 3-rnd burst, no full auto
                BurstShots = 3,
                // Fast round — snappy, not floaty. Tracer length scales with
                // this so it reads as a continuous streak, not stepping dots.
                ProjectileSpeed = 150f,
                // Tight rifle: ~1.2° steady spread. Each shot kicks +1° (caps
                // at 7°) and settles 9°/s — so taps stay accurate while a
                // mag-dump walks the cone wide open.
                SpreadDegrees = 1.2f,
                RecoilPerShot = 1.0f,
                RecoilRecoverPerSec = 9.0f,
                MaxRecoilDegrees = 7.0f,
                WarmupTicks = 12,
                AimTicks = 54,   // 0.9s spot-to-first-shot (per target)
                // ~720 rpm cyclic = a shot every ~5 ticks at 60 Hz.
                ShotCooldownTicks = 5,
                CycleCooldownTicks = 24, // 0.4s between semi shots / bursts
                ReloadTicks = 120,
            });

        // MP5 — 9mm SMG: low damage, very high RoF, short range, snappy aim,
        // looser cone than the rifle. Full auto for spray-down CQB.
        SubmachineGun = RegisterItem("SubmachineGun", "MP5", Equipment, weight: 3f, bulk: 2f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 22f,
                AmmoCategoryPath = "9mm",
                MagazineSize = 30,
                Modes = FireModeFlags.Single | FireModeFlags.Burst | FireModeFlags.Auto,
                BurstShots = 3,
                ProjectileSpeed = 110f,
                SpreadDegrees = 2.4f,
                RecoilPerShot = 0.7f,
                RecoilRecoverPerSec = 11f,
                MaxRecoilDegrees = 6f,
                WarmupTicks = 8,
                AimTicks = 36,   // 0.6s — quick to bring up in CQB
                ShotCooldownTicks = 4,  // ~900 rpm cyclic
                CycleCooldownTicks = 14, // ~0.23s between bursts / taps
                ReloadTicks = 100,
            });

        // M700 — 7.62x51 bolt rifle: high damage, long range, pinpoint, but
        // slow (long aim + bolt cycle between shots). Single fire only.
        BoltActionRifle = RegisterItem("BoltActionRifle", "M700", Equipment, weight: 5f, bulk: 4f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 75f,
                AmmoCategoryPath = "762x51",
                MagazineSize = 5,
                Modes = FireModeFlags.Single,
                BurstShots = 1,
                ProjectileSpeed = 220f,
                SpreadDegrees = 0.4f,
                RecoilPerShot = 2.5f,
                RecoilRecoverPerSec = 6f,
                MaxRecoilDegrees = 5f,
                WarmupTicks = 18,
                AimTicks = 108,   // 1.8s — slow, deliberate
                ShotCooldownTicks = 90,
                CycleCooldownTicks = 90, // 1.5s bolt cycle between shots
                ReloadTicks = 180,
            });

        // 7.62x39mm Soviet — intermediate round: harder-hitting than 5.56,
        // less than the full-power 7.62x51.
        Ammo762x39 = RegisterItem("Ammo762x39", "7.62x39mm Soviet", Ammo, weight: 0.018f, bulk: 0.018f, maxStack: 500,
            ammo: new AmmoSpec { CategoryPath = "762x39", InjuryKind = ConditionKind.Gunshot, Damage = 20f, PenSharp = 10f, PenBlunt = 38f });

        // AKM — 7.62x39 assault rifle: more punch + recoil, looser cone than the
        // M16, a touch slower cyclic. Select-fire.
        Akm = RegisterItem("AKM", "AKM", Equipment, weight: 4.3f, bulk: 3f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 46f,
                AmmoCategoryPath = "762x39",
                MagazineSize = 30,
                Modes = FireModeFlags.Single | FireModeFlags.Auto, // AKM: semi + full auto, no burst
                BurstShots = 3,
                ProjectileSpeed = 140f,
                SpreadDegrees = 1.7f,
                RecoilPerShot = 1.5f,
                RecoilRecoverPerSec = 8.0f,
                MaxRecoilDegrees = 8.5f,
                WarmupTicks = 12,
                AimTicks = 54,
                ShotCooldownTicks = 6,   // ~600 rpm cyclic
                CycleCooldownTicks = 26,
                ReloadTicks = 120,
            });

        // AUG — 5.56 bullpup: accurate, shares the M16's Rifle ammo, snappy.
        Aug = RegisterItem("AUG", "AUG", Equipment, weight: 3.6f, bulk: 3f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 52f,
                AmmoCategoryPath = "Rifle",
                MagazineSize = 30,
                Modes = FireModeFlags.Single | FireModeFlags.Auto, // AUG: semi + full auto, no burst
                BurstShots = 3,
                ProjectileSpeed = 150f,
                SpreadDegrees = 1.0f,
                RecoilPerShot = 0.9f,
                RecoilRecoverPerSec = 9.5f,
                MaxRecoilDegrees = 6.5f,
                WarmupTicks = 10,
                AimTicks = 48,
                ShotCooldownTicks = 5,
                CycleCooldownTicks = 22,
                ReloadTicks = 115,
            });

        // M249 SAW — belt-fed 5.56 LMG: 100-round belt, full-auto only, heavy,
        // looser cone than a rifle but lower per-shot recoil (bipod-steady).
        Lmg = RegisterItem("M249", "M249 SAW", Equipment, weight: 8f, bulk: 5f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 48f,
                AmmoCategoryPath = "Rifle",
                MagazineSize = 100,
                Modes = FireModeFlags.Auto,
                BurstShots = 1,
                ProjectileSpeed = 150f,
                SpreadDegrees = 2.0f,
                RecoilPerShot = 0.55f,
                RecoilRecoverPerSec = 8f,
                MaxRecoilDegrees = 9f,
                WarmupTicks = 16,
                AimTicks = 60,
                ShotCooldownTicks = 5,   // ~720 rpm
                CycleCooldownTicks = 24,
                ReloadTicks = 220,       // slow belt swap
            });

        // 12-gauge buckshot — each shell sprays a cluster of low-pen pellets.
        Ammo12ga = RegisterItem("Ammo12ga", "12-gauge Buckshot", Ammo, weight: 0.05f, bulk: 0.05f, maxStack: 300,
            ammo: new AmmoSpec { CategoryPath = "12ga", InjuryKind = ConditionKind.Gunshot, Damage = 7f, PenSharp = 3f, PenBlunt = 28f });

        // AA-12 — full-auto combat shotgun: 8 pellets/shell, 20-round drum.
        // Brutal point-blank, useless at range, heavy kick.
        AutoShotgun = RegisterItem("AA12", "AA-12", Equipment, weight: 5.5f, bulk: 4f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 16f,
                AmmoCategoryPath = "12ga",
                MagazineSize = 20,
                Modes = FireModeFlags.Single | FireModeFlags.Auto,
                BurstShots = 1,
                PelletsPerShot = 8,
                ProjectileSpeed = 80f,
                SpreadDegrees = 5.0f,
                RecoilPerShot = 1.8f,
                RecoilRecoverPerSec = 7f,
                MaxRecoilDegrees = 10f,
                WarmupTicks = 12,
                AimTicks = 30,
                ShotCooldownTicks = 8,   // ~450 rpm full-auto
                CycleCooldownTicks = 20,
                ReloadTicks = 160,
            });

        // .22 LR — tiny rimfire round: low damage + pen, but light + cheap.
        Ammo22lr = RegisterItem("Ammo22lr", ".22 LR", Ammo, weight: 0.004f, bulk: 0.004f, maxStack: 800,
            ammo: new AmmoSpec { CategoryPath = "22lr", InjuryKind = ConditionKind.Gunshot, Damage = 5f, PenSharp = 2f, PenBlunt = 16f });

        // CP33 — .22 LR pistol: 33-round mag, low damage, fast cycle, light +
        // quick to bring up. Semi-auto sidearm (+ full-auto for a bullet hose).
        Pistol = RegisterItem("CP33", "CP33", Equipment, weight: 1.2f, bulk: 1f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 20f,
                AmmoCategoryPath = "22lr",
                MagazineSize = 33,
                Modes = FireModeFlags.Single, // semi-auto pistol
                BurstShots = 1,
                ProjectileSpeed = 100f,
                SpreadDegrees = 1.8f,
                RecoilPerShot = 0.45f,
                RecoilRecoverPerSec = 12f,
                MaxRecoilDegrees = 5f,
                WarmupTicks = 6,
                AimTicks = 30,
                ShotCooldownTicks = 5,
                CycleCooldownTicks = 9,
                ReloadTicks = 90,
            });

        // G3 — 7.62x51 battle rifle: select-fire (semi + full auto), full-power
        // round so it hits hard with heavy recoil; 20-round mag. Shares 762x51.
        BattleRifle = RegisterItem("G3", "G3", Equipment, weight: 4.5f, bulk: 3f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 55f,
                AmmoCategoryPath = "762x51",
                MagazineSize = 20,
                Modes = FireModeFlags.Single | FireModeFlags.Auto,
                BurstShots = 1,
                ProjectileSpeed = 180f,
                SpreadDegrees = 1.4f,
                RecoilPerShot = 1.9f,
                RecoilRecoverPerSec = 8f,
                MaxRecoilDegrees = 9.5f,
                WarmupTicks = 14,
                AimTicks = 56,
                ShotCooldownTicks = 6,
                CycleCooldownTicks = 26,
                ReloadTicks = 130,
            });

        // RPD — belt-fed 7.62x39 LMG: full-auto only, 100-round belt, heavy,
        // looser cone but low per-shot recoil. Shares the AKM's Soviet ammo.
        LmgRpd = RegisterItem("RPD", "RPD", Equipment, weight: 7.5f, bulk: 5f, equippable: true,
            ranged: new RangedSpec
            {
                Range = 46f,
                AmmoCategoryPath = "762x39",
                MagazineSize = 100,
                Modes = FireModeFlags.Auto,
                BurstShots = 1,
                ProjectileSpeed = 140f,
                SpreadDegrees = 2.0f,
                RecoilPerShot = 0.7f,
                RecoilRecoverPerSec = 8f,
                MaxRecoilDegrees = 9f,
                WarmupTicks = 16,
                AimTicks = 60,
                ShotCooldownTicks = 6,
                CycleCooldownTicks = 24,
                ReloadTicks = 220,
            });

        // Kevlar vest — torso only. Sharp 8 mmRHA deflects HP (3) + FMJ (6)
        // into bruises; AP (12) penetrates (reduced). Blunt 20 MPa soaks some
        // of the deflected concussion.
        KevlarVest = RegisterItem("KevlarVest", "Kevlar Vest", Equipment, weight: 6f, bulk: 4f, equippable: true,
            armor: new ArmorSpec { ArmorSharp = 8f, ArmorBlunt = 20f, Covers = new[] { "Torso" } });
    }

    public static ItemCategory RegisterCategory(string id, string displayName, ItemCategory? parent = null)
    {
        var cat = new ItemCategory(id, displayName, parent);
        if (!_categoriesByPath.TryAdd(cat.FullPath, cat))
        {
            throw new InvalidOperationException($"Category already registered at path '{cat.FullPath}'.");
        }
        if (parent is null) _roots.Add(cat);
        else parent.AddSub(cat);
        return cat;
    }

    public static ItemDef RegisterItem(string id, string displayName, ItemCategory category, float weight = 1f, float bulk = 1f, bool equippable = false, (ConditionKind, float)[]? meleeAttacks = null, bool defaultStockpileAllowed = true, RangedSpec? ranged = null, AmmoSpec? ammo = null, int maxStack = 75, ArmorSpec? armor = null)
    {
        var item = new ItemDef(id, displayName, category, weight, bulk, equippable, meleeAttacks, defaultStockpileAllowed, ranged, ammo, maxStack, armor);
        if (!_itemsByPath.TryAdd(item.FullPath, item))
        {
            throw new InvalidOperationException($"Item already registered at path '{item.FullPath}'.");
        }
        category.AddItem(item);
        return item;
    }

    // True if `item` lives anywhere underneath `category` (direct or
    // nested). Used by stockpile filters where the player checks an
    // entire subtree.
    public static bool IsUnder(ItemDef item, ItemCategory category)
    {
        var c = item.Category;
        while (c is not null)
        {
            if (ReferenceEquals(c, category)) return true;
            c = c.Parent;
        }
        return false;
    }
}
