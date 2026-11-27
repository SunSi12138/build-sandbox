using Xunit;

namespace StreetEcoTool.Tests;

public sealed class CommunityMapStyleTests
{
    [Fact]
    public void StyleDirectoryAndLegacyInlineStylePersistWithoutSchemaChanges()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var layer = store.GetMapLayers()[0];
        var inline = """{"version":8,"sources":{"base":{"type":"vector"}},"layers":[]}""";
        var directory = Path.Combine(env.Root, "style");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "style.json"), inline);
        var saved = store.SaveMapLayer(layer with { Id = "offline-style", Folder = "vector", TileType = "mvt", IsDefault = false, StyleDirectory = directory, StyleJson = inline });
        Assert.Equal(directory, saved.StyleDirectory);
        var displayed = store.GetDisplayMapLayers().Single(item => item.Id == saved.Id);
        Assert.Equal("/api/map/style/offline-style/style.json", displayed.StyleUrl);
        Assert.Equal(inline, displayed.StyleJson);
        using var reopened = env.Open();
        var stored = reopened.GetMapLayers().Single(item => item.Id == saved.Id);
        Assert.Equal(directory, stored.StyleDirectory);
        Assert.Equal(inline, stored.StyleJson);
        var cleared = reopened.SaveMapLayer(stored with { StyleDirectory = "" });
        Assert.Equal("", cleared.StyleDirectory);
        Assert.Equal(inline, reopened.GetMapLayers().Single(item => item.Id == saved.Id).StyleJson);
        Assert.Equal("/api/map/style/offline-style/style.json", reopened.GetDisplayMapLayers().Single(item => item.Id == saved.Id).StyleUrl);
        V2Environment.AssertForeignKeys(reopened.DatabasePath);
    }
}
