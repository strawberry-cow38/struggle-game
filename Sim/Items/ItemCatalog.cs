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

    internal ItemDef(string id, string displayName, ItemCategory category)
    {
        Id = id;
        DisplayName = displayName;
        Category = category;
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

    static ItemCatalog()
    {
        Resources = RegisterCategory("Resources", "Resources");
        ResourcesWood = RegisterCategory("Wood", "Wood", Resources);
        Wood = RegisterItem("Wood", "Wood", ResourcesWood);
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

    public static ItemDef RegisterItem(string id, string displayName, ItemCategory category)
    {
        var item = new ItemDef(id, displayName, category);
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
