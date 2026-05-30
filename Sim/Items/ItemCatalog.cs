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

    internal ItemDef(string id, string displayName, ItemCategory category, float weight, float bulk, bool equippable, (ConditionKind, float)[]? meleeAttacks, bool defaultStockpileAllowed)
    {
        Id = id;
        DisplayName = displayName;
        Category = category;
        Weight = weight;
        Bulk = bulk;
        Equippable = equippable;
        MeleeAttacks = meleeAttacks ?? System.Array.Empty<(ConditionKind, float)>();
        DefaultStockpileAllowed = defaultStockpileAllowed;
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

    static ItemCatalog()
    {
        Resources = RegisterCategory("Resources", "Resources");
        ResourcesWood = RegisterCategory("Wood", "Wood", Resources);
        Wood = RegisterItem("Wood", "Wood", ResourcesWood, weight: 1f, bulk: 1f);
        ResourcesFood = RegisterCategory("Food", "Food", Resources);
        Carrot = RegisterItem("Carrot", "Carrot", ResourcesFood, weight: 0.05f, bulk: 0.05f);
        SimpleMeal = RegisterItem("SimpleMeal", "Simple Meal", ResourcesFood, weight: 0.4f, bulk: 0.4f);
        Equipment = RegisterCategory("Equipment", "Equipment");
        WoodenTrinket = RegisterItem("WoodenTrinket", "Wooden Trinket", Equipment, weight: 2f, bulk: 1f, equippable: true,
            meleeAttacks: new (ConditionKind, float)[] { (ConditionKind.Cut, 0.25f), (ConditionKind.Stab, 0.22f) });
        Corpses = RegisterCategory("Corpses", "Corpses");
        Corpse = RegisterItem("Colonist", "Corpse", Corpses, weight: 40f, bulk: 40f, defaultStockpileAllowed: false);
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

    public static ItemDef RegisterItem(string id, string displayName, ItemCategory category, float weight = 1f, float bulk = 1f, bool equippable = false, (ConditionKind, float)[]? meleeAttacks = null, bool defaultStockpileAllowed = true)
    {
        var item = new ItemDef(id, displayName, category, weight, bulk, equippable, meleeAttacks, defaultStockpileAllowed);
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
