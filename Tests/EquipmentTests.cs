using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

// Equipment / persistent-inventory flow: a colonist ordered to equip a
// dropped equippable pile walks over, equips one unit into a generic
// slot, and the item can then be force-unequipped (→ general inventory)
// or dropped back on the ground.
public class EquipmentTests
{
    [Fact]
    public void EquipOrder_WalksPawnToPileAndEquipsOneUnit()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        var (pawnId, pawnTile) = FirstPawn(sim);
        var itemTile = NearbyWalkableNotEqual(sim, pawnTile, pawnTile);
        sim.SpawnItemPile(itemTile, ItemCatalog.WoodenTrinket.FullPath, 1);
        int itemId = FindPileEntity(sim, itemTile, ItemCatalog.WoodenTrinket.FullPath);

        sim.SetEquipOrder(pawnId, itemId);

        bool equipped = StepUntil(sim, 4000, () => EquippedCount(sim, pawnId) == 1);
        Assert.True(equipped, "pawn never equipped the trinket");

        // The pile is gone from the world (consumed into the slot).
        Assert.Equal(0, CountPiles(sim, ItemCatalog.WoodenTrinket.FullPath));
        // Equipped weight counts toward the pawn's carry load.
        Assert.True(EquippedSlotPath(sim, pawnId, 0) == ItemCatalog.WoodenTrinket.FullPath);
    }

    [Fact]
    public void ForceUnequip_MovesEquippedIntoGeneralInventory()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (pawnId, _) = FirstPawn(sim);
        GiveEquipped(sim, pawnId, ItemCatalog.WoodenTrinket.FullPath);

        sim.ForceUnequip(pawnId, 0);

        Assert.Equal(0, EquippedCount(sim, pawnId));
        Assert.Equal(1, HeldCount(sim, pawnId));
    }

    [Fact]
    public void DropEquipped_DropsPileAtPawnTile()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (pawnId, _) = FirstPawn(sim);
        GiveEquipped(sim, pawnId, ItemCatalog.WoodenTrinket.FullPath);

        sim.DropEquipped(pawnId, 0);

        Assert.Equal(0, EquippedCount(sim, pawnId));
        Assert.Equal(1, CountPiles(sim, ItemCatalog.WoodenTrinket.FullPath));
    }

    [Fact]
    public void DropHeldItem_DropsPileAtPawnTile()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (pawnId, _) = FirstPawn(sim);
        GiveEquipped(sim, pawnId, ItemCatalog.WoodenTrinket.FullPath);
        sim.ForceUnequip(pawnId, 0); // now in general inventory

        sim.DropHeldItem(pawnId, 0);

        Assert.Equal(0, HeldCount(sim, pawnId));
        Assert.Equal(1, CountPiles(sim, ItemCatalog.WoodenTrinket.FullPath));
    }

    [Fact]
    public void EquipFromInventory_MovesHeldEquippableIntoSlot()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (pawnId, _) = FirstPawn(sim);
        GiveEquipped(sim, pawnId, ItemCatalog.WoodenTrinket.FullPath);
        sim.ForceUnequip(pawnId, 0); // trinket now in general inventory

        Assert.Equal(1, HeldCount(sim, pawnId));
        sim.EquipFromInventory(pawnId, 0);

        Assert.Equal(0, HeldCount(sim, pawnId));
        Assert.Equal(1, EquippedCount(sim, pawnId));
    }

    [Fact]
    public void PickupOrder_FetchesRequestedCountIntoInventory()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (pawnId, pawnTile) = FirstPawn(sim);
        var itemTile = NearbyWalkableNotEqual(sim, pawnTile, pawnTile);
        sim.SpawnItemPile(itemTile, ItemCatalog.Carrot.FullPath, 10);
        int itemId = FindPileEntity(sim, itemTile, ItemCatalog.Carrot.FullPath);

        sim.SetPickupOrder(pawnId, itemId, 4);

        bool done = StepUntil(sim, 4000, () => HeldUnits(sim, pawnId, ItemCatalog.Carrot.FullPath) == 4);
        Assert.True(done, "pawn never picked up 4 carrots");
        // 6 left on the ground.
        Assert.Equal(6, CountUnits(sim, ItemCatalog.Carrot.FullPath));
    }

    [Fact]
    public void PickupAll_ClampsToCarryCapacity()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (pawnId, pawnTile) = FirstPawn(sim);
        var itemTile = NearbyWalkableNotEqual(sim, pawnTile, pawnTile);
        // Trinket weighs 2; cap is 75 → at most 37 fit. Spawn 50, ask for all.
        sim.SpawnItemPile(itemTile, ItemCatalog.WoodenTrinket.FullPath, 50);
        int itemId = FindPileEntity(sim, itemTile, ItemCatalog.WoodenTrinket.FullPath);

        sim.SetPickupOrder(pawnId, itemId, int.MaxValue);

        bool done = StepUntil(sim, 4000, () => HeldUnits(sim, pawnId, ItemCatalog.WoodenTrinket.FullPath) > 0);
        Assert.True(done, "pawn never picked up trinkets");
        int held = HeldUnits(sim, pawnId, ItemCatalog.WoodenTrinket.FullPath);
        Assert.Equal((int)(SimConstants.MaxCarryWeight / 2f), held); // clamped by weight
    }

    [Fact]
    public void NonEquippableItem_GetsNoEquipOrder()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (pawnId, pawnTile) = FirstPawn(sim);
        var itemTile = NearbyWalkableNotEqual(sim, pawnTile, pawnTile);
        sim.SpawnItemPile(itemTile, ItemCatalog.Carrot.FullPath, 1);
        int itemId = FindPileEntity(sim, itemTile, ItemCatalog.Carrot.FullPath);

        sim.SetEquipOrder(pawnId, itemId);

        Assert.True(sim.Store.TryGetEntityById(pawnId, out var pawnEnt));
        Assert.False(pawnEnt.HasComponent<EquipOrder>());
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static void GiveEquipped(SimRuntime sim, int pawnId, string path)
    {
        Assert.True(sim.Store.TryGetEntityById(pawnId, out var pawn));
        pawn.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>(),
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = path, Count = 1 },
            },
        });
    }

    private static (int Id, TilePos Tile) FirstPawn(SimRuntime sim)
    {
        int id = 0;
        TilePos tile = default;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity e) =>
        {
            if (id != 0) return;
            id = e.Id;
            tile = new TilePos((int)p.X, (int)p.Y);
        });
        Assert.True(id != 0, "no pawn in sim");
        return (id, tile);
    }

    private static int EquippedCount(SimRuntime sim, int pawnId)
    {
        Assert.True(sim.Store.TryGetEntityById(pawnId, out var e));
        if (!e.HasComponent<Inventory>()) return 0;
        var inv = e.GetComponent<Inventory>();
        return inv.Equipped?.Count ?? 0;
    }

    private static int HeldUnits(SimRuntime sim, int pawnId, string path)
    {
        Assert.True(sim.Store.TryGetEntityById(pawnId, out var e));
        if (!e.HasComponent<Inventory>()) return 0;
        var inv = e.GetComponent<Inventory>();
        int n = 0;
        if (inv.Items != null) foreach (var s in inv.Items) if (s.ItemPath == path) n += s.Count;
        return n;
    }

    private static int CountUnits(SimRuntime sim, string path)
    {
        int n = 0;
        sim.Store.Query<ItemPile>().ForEachEntity((ref ItemPile p, Entity _) =>
        {
            if (p.ItemPath == path) n += p.Count;
        });
        return n;
    }

    private static int HeldCount(SimRuntime sim, int pawnId)
    {
        Assert.True(sim.Store.TryGetEntityById(pawnId, out var e));
        if (!e.HasComponent<Inventory>()) return 0;
        var inv = e.GetComponent<Inventory>();
        return inv.Items?.Count ?? 0;
    }

    private static string EquippedSlotPath(SimRuntime sim, int pawnId, int idx)
    {
        Assert.True(sim.Store.TryGetEntityById(pawnId, out var pe));
        var inv = pe.GetComponent<Inventory>();
        return inv.Equipped![idx].ItemPath;
    }

    private static int FindPileEntity(SimRuntime sim, TilePos tile, string path)
    {
        int id = 0;
        sim.Store.Query<ItemPile>().ForEachEntity((ref ItemPile p, Entity e) =>
        {
            if (id != 0) return;
            if (p.Tile == tile && p.ItemPath == path) id = e.Id;
        });
        Assert.True(id != 0, "pile not found");
        return id;
    }

    private static int CountPiles(SimRuntime sim, string path)
    {
        int n = 0;
        sim.Store.Query<ItemPile>().ForEachEntity((ref ItemPile p, Entity _) =>
        {
            if (p.ItemPath == path && p.Count > 0) n++;
        });
        return n;
    }

    private static bool StepUntil(SimRuntime sim, int maxTicks, Func<bool> done)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            if (done()) return true;
            sim.Step(SimConstants.TickSeconds);
        }
        return done();
    }

    private static TilePos NearbyWalkableNotEqual(SimRuntime sim, TilePos near, TilePos avoid)
    {
        for (int r = 1; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var t = new TilePos(near.X + dx, near.Y + dy);
                    if (t == avoid) continue;
                    if (!sim.MapView.Walkable(t)) continue;
                    if (sim.TreeTiles.Contains(t)) continue;
                    return t;
                }
        }
        throw new Xunit.Sdk.XunitException("no walkable tile near anchor");
    }
}
