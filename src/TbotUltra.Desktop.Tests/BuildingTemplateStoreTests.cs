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
            ],
        };

        store.Save([template]);
        var loaded = store.Load();

        Assert.Single(loaded);
        Assert.Equal("Starter", loaded[0].Name);
        Assert.Equal("Teutons", loaded[0].CreatedByTribe);
        Assert.Single(loaded[0].Rows);
        Assert.Equal(15, loaded[0].Rows[0].Gid);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmpty()
    {
        var root = TempRoot();
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "building_templates.json"), "{broken");
        var store = new BuildingTemplateStore(root);

        Assert.Empty(store.Load());
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbot-ultra-building-template-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
