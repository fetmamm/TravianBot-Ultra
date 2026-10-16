using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class HeroRaidingManualUiSourceTests
{
    [Fact]
    public void ScanNow_RequiresAnExistingTravianLoginBeforeStarting()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "TbotUltra.Desktop", "MainWindow.HeroRaiding.cs"));
        var scanStart = source.IndexOf("internal Task OnHeroRaidingScanClicked", StringComparison.Ordinal);
        var exclusionStart = source.IndexOf("private void HeroRaidingExclusion_Changed", scanStart,
            StringComparison.Ordinal);
        Assert.True(scanStart >= 0 && exclusionStart > scanStart);
        var scanBody = source[scanStart..exclusionStart];

        var loginGuard = scanBody.IndexOf("RequireTravianLoginForHeroRaiding()", StringComparison.Ordinal);
        var operation = scanBody.IndexOf("RunManualOperationAsync", StringComparison.Ordinal);
        Assert.True(loginGuard >= 0 && operation > loginGuard);
        Assert.Contains("if (_isLoggedIn) return true;", source, StringComparison.Ordinal);
        Assert.Contains("AppDialog.Show", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateList_OffersOpeningTheExactMapTile()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "TbotUltra.Desktop", "Views", "HeroHubPanel.xaml"));
        var candidate = File.ReadAllText(Path.Combine(
            root, "src", "TbotUltra.Desktop", "ViewModels", "HeroRaidingCandidateItem.cs"));

        Assert.Contains("Content=\"Open in browser\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"HeroRaidingOpenInBrowserButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("/karte.php?x={X}&y={Y}", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualScan_UsesReadinessThatAllowsTheHeroToBeAway()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var runner = File.ReadAllText(Path.Combine(
            root, "src", "TbotUltra.Worker", "Services", "BotTaskRunner.HeroRaiding.cs"));

        Assert.Contains("ReadHeroRaidingScanReadinessAsync", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Scan again after the hero returns", runner, StringComparison.Ordinal);
    }
}
