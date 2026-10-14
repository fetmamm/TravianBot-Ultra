using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TravcoToolsControlSourceTests
{
    [Fact]
    public void MapOasisSummary_ReportsElapsedTimeAndEveryOasisType()
    {
        var scan = new MapOasisScanResult(
            [
                new MapOasisEntry(1, 1, false, "Clay", "Clay", "", "", ""),
                new MapOasisEntry(2, 2, true, "Clay", "Clay", "", "Player", ""),
                new MapOasisEntry(3, 3, false, "Iron", "Iron", "", "", ""),
                new MapOasisEntry(4, 4, false, "Crop", "Crop", "", "", ""),
            ],
            12,
            12);

        var summary = TravcoToolsControl.BuildMapOasisScanSummary(
            new MapOasisScanRequest(0, 0, MapOasisScanScope.Radius, 40, MapOasisScanSpeed.Normal),
            scan,
            TimeSpan.FromHours(27) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3),
            "Map Oasis");

        Assert.Contains("Time: 27:02:03", summary, StringComparison.Ordinal);
        Assert.Contains("Map areas scanned: 12/12", summary, StringComparison.Ordinal);
        Assert.Contains("Oases found: 4 (3 free, 1 occupied)", summary, StringComparison.Ordinal);
        Assert.Contains("Clay: 2", summary, StringComparison.Ordinal);
        Assert.Contains("Iron: 1", summary, StringComparison.Ordinal);
        Assert.Contains("Crop: 1", summary, StringComparison.Ordinal);
        Assert.Contains("Saved list 'Map Oasis'", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MapOasisSummary_ExplainsPartialScanWithNoMatches()
    {
        var summary = TravcoToolsControl.BuildMapOasisScanSummary(
            new MapOasisScanRequest(0, 0, MapOasisScanScope.WholeMap, 0, MapOasisScanSpeed.Normal),
            new MapOasisScanResult([], 2, 10, IsPartialResult: true),
            TimeSpan.FromSeconds(9),
            savedListName: null);

        Assert.Contains("Scan stopped early", summary, StringComparison.Ordinal);
        Assert.Contains("Time: 00:00:09", summary, StringComparison.Ordinal);
        Assert.Contains("Oases found: 0", summary, StringComparison.Ordinal);
        Assert.Contains("By type:\nNone", summary, StringComparison.Ordinal);
        Assert.Contains("No list was created", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MapSqlScan_DoesNotStartPersistentTravcoSession()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Farming.Travco.cs"));

        Assert.Contains("AddAllVillagesRequested = RunAllVillagesImportAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddAllVillagesRequested = async (request, progress, cancellationToken)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CloseTravcoAction_ClosesImmediatelyWithoutConfirmationDialog()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Desktop",
            "TravcoToolsControl.xaml.cs"));
        var methodStart = source.IndexOf("private async Task CloseTravcoSessionAsync()", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private void SetBusy", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.Contains("await CloseRequested();", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AppDialog.ShowCustom", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Keep open", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveSession_ExposesGlobalCloseActionAcrossUiNavigation()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Farming.Travco.cs"));

        var globalAction = xaml.IndexOf("x:Name=\"TravcoSessionAttentionBorder\"", StringComparison.Ordinal);
        var navigation = xaml.IndexOf("x:Name=\"DashboardNavButton\"", StringComparison.Ordinal);

        Assert.True(globalAction >= 0 && globalAction < navigation);
        Assert.Contains("Style=\"{StaticResource TravcoAttentionRingStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("GlobalCloseTravcoTabButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("_travcoSessionActive ? System.Windows.Visibility.Visible", source, StringComparison.Ordinal);
        Assert.Contains("RequestCloseSessionAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAllVillagesPopup_ShowsSavedSkipOwnVillagesBelowIgnoreAlliance()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Desktop",
            "TravcoToolsControl.xaml.cs"));
        var ignoredAlliance = source.IndexOf("content.Children.Add(ignoredAlliances);", StringComparison.Ordinal);
        var skipOwn = source.IndexOf("content.Children.Add(skipOwnVillages);", StringComparison.Ordinal);

        Assert.Contains("Content = \"Skip own villages\"", source, StringComparison.Ordinal);
        Assert.Contains("IsChecked = saved.SkipOwnVillages", source, StringComparison.Ordinal);
        Assert.True(ignoredAlliance >= 0 && skipOwn > ignoredAlliance);
    }
}
