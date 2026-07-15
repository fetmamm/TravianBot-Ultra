using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingTemplateStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var root = TempRoot();
        var store = new BuildingTemplateStore(root);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void SaveThenLoad_PreservesTemplates()
    {
        var root = TempRoot();
        var store = new BuildingTemplateStore(root);
        var template = new BuildingTemplate
        {
            Name = "Starter",
            CreatedByTribe = "Teutons",
            Rows =
            [
                new BuildingTemplateRow
                {
                    Kind = BuildingTemplateRowKind.Building,
                    Gid = 15,
                    BuildingName = "Main Building",
                    PreferredSlotId = 19,
                    TargetLevel = 3,
                },
                new BuildingTemplateRow
                {
                    Kind = BuildingTemplateRowKind.AllResources,
                    BuildingName = "All Woodcutters",
                    ResourceScope = "wood",
                    TargetLevel = 99,
                },
            ],
        };

        store.Save([template]);
        var loaded = store.Load();

        Assert.Single(loaded);
        Assert.Equal("Starter", loaded[0].Name);
        Assert.Equal("Teutons", loaded[0].CreatedByTribe);
        Assert.Equal(2, loaded[0].Rows.Count);
        Assert.Equal(15, loaded[0].Rows[0].Gid);
        Assert.Equal("wood", loaded[0].Rows[1].ResourceScope);
        Assert.Equal(20, loaded[0].Rows[1].TargetLevel);
    }

    [Fact]
    public void Load_CorruptJson_QuarantinesFileBeforeReturningEmpty()
    {
        var root = TempRoot();
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "building_templates.json"), "{broken");
        var store = new BuildingTemplateStore(root);

        Assert.Empty(store.Load());
        Assert.NotNull(store.LastLoadWarning);
        Assert.False(File.Exists(Path.Combine(config, "building_templates.json")));
        Assert.Single(Directory.GetFiles(config, "building_templates.json.corrupt-*"));
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbot-ultra-building-template-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
