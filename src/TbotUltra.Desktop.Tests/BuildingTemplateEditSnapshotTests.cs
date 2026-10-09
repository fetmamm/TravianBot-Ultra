using TbotUltra.Desktop;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingTemplateEditSnapshotTests
{
    [Fact]
    public void TracksSelectedRowsAndNameButNotSaveTimestamps()
    {
        var template = new BuildingTemplate
        {
            Name = "Farm start",
            Rows = [new BuildingTemplateRow { Gid = 10, BuildingName = "Warehouse", TargetLevel = 2 }],
        };
        var baseline = BuildingTemplateEditSnapshot.Capture([template], template, template.Rows);

        template.UpdatedAtUtc = template.UpdatedAtUtc.AddMinutes(1);
        Assert.Equal(baseline, BuildingTemplateEditSnapshot.Capture([template], template, template.Rows));

        var temporaryRows = new[] { new BuildingTemplateRow { Gid = 10, BuildingName = "Warehouse", TargetLevel = 3 } };
        Assert.NotEqual(baseline, BuildingTemplateEditSnapshot.Capture([template], template, temporaryRows));
        Assert.Equal(2, template.Rows[0].TargetLevel);

        template.Name = "Farm start edited";
        Assert.NotEqual(baseline, BuildingTemplateEditSnapshot.Capture([template], template, template.Rows));
    }

    [Fact]
    public void LoadingARowPreservesItsIdentitySoSwitchingTemplatesDoesNotAppearDirty()
    {
        var original = new BuildingTemplateRow { Gid = 10, BuildingName = "Warehouse", TargetLevel = 2 };
        var option = new BuildingTemplateTargetOption(10, "Warehouse", "Infrastructure", null, null);
        var view = BuildingTemplateRowView.From(original, [option], []);

        Assert.Equal(original.Id, view.ToTemplateRow().Id);
    }
}
