using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BuildingTemplatesWindowSourceTests
{
    [Fact]
    public void QueueActions_UseEditedRowsWithoutSavingTheTemplateLibrary()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));
        var queueStart = code.IndexOf("private void QueueTemplateButton_Click", StringComparison.Ordinal);
        var closeStart = code.IndexOf("private void CloseButton_Click", queueStart, StringComparison.Ordinal);
        var queueActions = code[queueStart..closeStart];

        Assert.Contains("BuildTemplateRowsFromUi()", queueActions, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAllTemplates", queueActions, StringComparison.Ordinal);
        Assert.DoesNotContain("_store.Save", queueActions, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveButton_IsEnabledOnlyForUnsavedChanges()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml"));
        var saveStart = xaml.IndexOf("Content=\"Save\"", StringComparison.Ordinal);
        var saveEnd = xaml.IndexOf("Click=\"SaveButton_Click\"", saveStart, StringComparison.Ordinal);
        Assert.True(saveStart >= 0 && saveEnd > saveStart);
        Assert.Contains("IsEnabled=\"{Binding HasUnsavedChanges}\"", xaml[saveStart..saveEnd], StringComparison.Ordinal);
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));
        Assert.Equal(2, code.Split("SaveAllTemplates(", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void LoadingOverlay_CoversTheWholeWindowAndStaysUntilInitialPreviewCompletes()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));

        Assert.Contains("Grid.ColumnSpan=\"3\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"-14\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LoadingOverlay.Show", code, StringComparison.Ordinal);
        var initialPreview = code.IndexOf("RefreshPlanPreview();", code.IndexOf("SelectedTemplate = Templates[0]", StringComparison.Ordinal), StringComparison.Ordinal);
        var hideOverlay = code.IndexOf("LoadingOverlay.Hide();", StringComparison.Ordinal);
        Assert.True(initialPreview >= 0 && hideOverlay > initialPreview, "The initial preview must finish before the loading overlay is hidden.");
    }

    [Fact]
    public void RowEditing_IsDebouncedAndSupportsMovingToBothEnds()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml"));

        Assert.Contains("RequestPlanPreviewRefresh();", code, StringComparison.Ordinal);
        Assert.Contains("Interval = TimeSpan.FromMilliseconds(120)", code, StringComparison.Ordinal);
        Assert.Contains("DropDownOpened=\"BuildingOptions_DropDownOpened\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshBuildingOptionAvailability();", code, StringComparison.Ordinal);
        Assert.Contains("MoveSelectedRowTo(0)", code, StringComparison.Ordinal);
        Assert.Contains("MoveSelectedRowTo(Rows.Count - 1)", code, StringComparison.Ordinal);
        Assert.Contains("Content=\"Top\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Bottom\"", xaml, StringComparison.Ordinal);

        var up = xaml.IndexOf("Content=\"Up\"", StringComparison.Ordinal);
        var down = xaml.IndexOf("Content=\"Down\"", StringComparison.Ordinal);
        var top = xaml.IndexOf("Content=\"Top\"", StringComparison.Ordinal);
        var bottom = xaml.IndexOf("Content=\"Bottom\"", StringComparison.Ordinal);
        Assert.True(up < down && down < top && top < bottom);
        Assert.Contains("Margin=\"12,0,6,0\"", xaml[top..bottom], StringComparison.Ordinal);
        Assert.Contains("Header=\"Action\" Width=\"72\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedPrerequisites_RefreshAvailabilityBeforeSelectingRequestedBuilding()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));
        var handlerStart = code.IndexOf("private void BuildingOption_PreviewMouseLeftButtonDown", StringComparison.Ordinal);
        var handlerEnd = code.IndexOf("private void DeleteRowButton_Click", handlerStart, StringComparison.Ordinal);
        var handler = code[handlerStart..handlerEnd];

        var prerequisiteInsert = handler.IndexOf("Rows.Insert", StringComparison.Ordinal);
        var availabilityRefresh = handler.IndexOf("RefreshBuildingOptionAvailability(targetRow)", StringComparison.Ordinal);
        var requestedBuildingLookup = handler.IndexOf("var nowAvailable =", StringComparison.Ordinal);
        Assert.True(
            prerequisiteInsert >= 0
            && availabilityRefresh > prerequisiteInsert
            && requestedBuildingLookup > availabilityRefresh,
            "The requested building must be made selectable after its prerequisite rows are inserted.");
    }

    [Fact]
    public void BuildingOrLevelChange_OffersStorageRowsBeforeTheAffectedTemplateRow()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));

        Assert.Contains("e.PropertyName is nameof(BuildingTemplateRowView.Target) or nameof(BuildingTemplateRowView.TargetLevel)", code, StringComparison.Ordinal);
        Assert.Contains("OfferStoragePrerequisites", code, StringComparison.Ordinal);
        Assert.Contains("_pendingStorageCheckRows.Add(row)", code, StringComparison.Ordinal);
        Assert.Contains("PlanStoragePrerequisites", code, StringComparison.Ordinal);
        Assert.Contains("Add required storage", code, StringComparison.Ordinal);
        Assert.Contains("Rows.Insert(targetIndex++", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateAndRowDeleteActions_UseCompactIconOnlyButtons()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml"));

        Assert.DoesNotContain("<TextBlock Text=\"New\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<TextBlock Text=\"Duplicate\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<TextBlock Text=\"Delete\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"RemoveRowButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DeleteRowButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Create a new empty template\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Duplicate the selected template\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Delete the selected template\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveValidatesTemplateAndQueueValidationFailureIsShownExplicitly()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));

        var saveHandlerStart = code.IndexOf("private void SaveButton_Click", StringComparison.Ordinal);
        var queueHandlerStart = code.IndexOf("private void QueueTemplateButton_Click", StringComparison.Ordinal);
        var closeHandlerStart = code.IndexOf("private void CloseButton_Click", StringComparison.Ordinal);
        Assert.True(saveHandlerStart >= 0 && queueHandlerStart > saveHandlerStart && closeHandlerStart > queueHandlerStart);

        var saveHandler = code[saveHandlerStart..queueHandlerStart];
        var queueHandler = code[queueHandlerStart..closeHandlerStart];
        Assert.Contains("SaveAllTemplates(skipValidation: false)", saveHandler, StringComparison.Ordinal);
        Assert.Contains("AppDialog.Show(", queueHandler, StringComparison.Ordinal);
        Assert.Contains("\"Cannot queue template\"", queueHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveRunsFullPreflightAndRequiresASecondSaveAfterStorageRowsAreInserted()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));
        var saveHandlerStart = code.IndexOf("private void SaveButton_Click", StringComparison.Ordinal);
        var queueHandlerStart = code.IndexOf("private void QueueTemplateButton_Click", saveHandlerStart, StringComparison.Ordinal);
        var saveHandler = code[saveHandlerStart..queueHandlerStart];

        Assert.Contains("TryPrepareTemplateForSave()", saveHandler, StringComparison.Ordinal);
        Assert.Contains("SaveAllTemplates(skipValidation: false)", saveHandler, StringComparison.Ordinal);
        Assert.Contains("click Save again", code, StringComparison.Ordinal);
        Assert.Contains("PlanStoragePrerequisiteInsertions", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportAndExportUseCompactIconsAndAConflictPreview()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml"));
        var previewXaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplateImportWindow.xaml"));
        var previewCode = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplateImportWindow.xaml.cs"));
        var code = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml.cs"));

        Assert.Contains("TemplateImportIconGeometry", xaml, StringComparison.Ordinal);
        Assert.Contains("TemplateExportIconGeometry", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Import templates\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Export templates\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"8,0,6,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Selected template\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"All templates\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Import manifest", previewXaml, StringComparison.Ordinal);
        Assert.Contains("Import as copy", previewCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAllTemplates(skipValidation: true)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportMenu_UsesALocalThemeTemplateWithoutSystemCheckGutter()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml"));

        Assert.Contains("x:Key=\"ExportMenuItemStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"OverridesDefaultStyle\" Value=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource SurfaceBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource ExportMenuItemStyle}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RowDeleteIcon_PreservesItsTwentyFourPixelDrawingViewport()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "BuildingTemplatesWindow.xaml"));

        var rowDeleteStart = xaml.IndexOf("ToolTip=\"Delete row\"", StringComparison.Ordinal);
        var rowDeleteEnd = xaml.IndexOf("</Button>", rowDeleteStart, StringComparison.Ordinal);
        Assert.True(rowDeleteStart >= 0 && rowDeleteEnd > rowDeleteStart);
        var rowDeleteButton = xaml[rowDeleteStart..rowDeleteEnd];
        Assert.Contains("<Viewbox Width=\"14\" Height=\"14\"", rowDeleteButton, StringComparison.Ordinal);
        Assert.Contains("<Canvas Width=\"24\" Height=\"24\">", rowDeleteButton, StringComparison.Ordinal);
        Assert.Contains("TemplateRowDeleteIconStyle", rowDeleteButton, StringComparison.Ordinal);
    }
}
