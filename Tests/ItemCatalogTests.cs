using StruggleGame.Sim.Items;
using Xunit;

namespace StruggleGame.Tests;

public class ItemCatalogTests
{
    [Fact]
    public void Builtin_Resources_Wood_Wood_AreRegistered()
    {
        Assert.Contains(ItemCatalog.Resources, ItemCatalog.Roots);
        Assert.Contains(ItemCatalog.ResourcesWood, ItemCatalog.Resources.Subcategories);
        Assert.Contains(ItemCatalog.Wood, ItemCatalog.ResourcesWood.Items);
    }

    [Fact]
    public void Wood_FullPath_IsSlashJoined()
    {
        Assert.Equal("Resources/Wood/Wood", ItemCatalog.Wood.FullPath);
        Assert.Equal("Resources/Wood", ItemCatalog.ResourcesWood.FullPath);
        Assert.Equal("Resources", ItemCatalog.Resources.FullPath);
    }

    [Fact]
    public void IsUnder_RecognizesParentChainAndExactCategory()
    {
        Assert.True(ItemCatalog.IsUnder(ItemCatalog.Wood, ItemCatalog.ResourcesWood));
        Assert.True(ItemCatalog.IsUnder(ItemCatalog.Wood, ItemCatalog.Resources));
    }

    [Fact]
    public void RegisterCategory_RejectsDuplicatePath()
    {
        // Nested category with a unique top-level name is fine. The
        // duplicate guard fires only on a full-path collision.
        var top = ItemCatalog.RegisterCategory($"_test_top_{Guid.NewGuid():N}", "Test Top");
        Assert.Throws<InvalidOperationException>(() =>
            ItemCatalog.RegisterCategory(top.Id, "Test Top Dup"));
    }

    [Fact]
    public void ItemsByPath_LookupByFullPathRoundTrips()
    {
        Assert.Same(ItemCatalog.Wood, ItemCatalog.ItemsByPath["Resources/Wood/Wood"]);
    }

    [Fact]
    public void Wood_HasNonZeroWeightAndBulk()
    {
        Assert.True(ItemCatalog.Wood.Weight > 0f);
        Assert.True(ItemCatalog.Wood.Bulk > 0f);
    }
}
