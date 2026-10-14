using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TownHallCelebrationSelectorTests
{
    [Fact]
    public void CelebrationsPath_ExplicitlySelectsCelebrationsTab()
    {
        var path = TravianClient.BuildTownHallCelebrationsPath(21);

        Assert.Equal("/build.php?id=21&t=1", path);
    }

    [Fact]
    public void StartLinkSelector_ExcludesGenericResearchAndHeroTransferLinks()
    {
        var selector = TravianClient.TownHallCelebrationStartLinkSelector;

        Assert.DoesNotContain("a.research", selector, StringComparison.Ordinal);
        Assert.DoesNotContain("resource.transfer", selector, StringComparison.Ordinal);
        Assert.Contains("a=1", selector, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCelebrationsTabLink_UsesVerifiedReadOnlyNavigation()
    {
        var source = ReadNavigationSource();
        var start = source.IndexOf("private async Task OpenSlotTabAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task EnsurePageForReadAsync", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("await OpenSlotFromOverviewAsync(slotId, cancellationToken)", method, StringComparison.Ordinal);
        Assert.Contains("await TryClickFirstVisibleEnabledAsync(", method, StringComparison.Ordinal);
        Assert.Contains("await GotoAsync(tabPath, cancellationToken)", method, StringComparison.Ordinal);
        Assert.Contains("await ReloadOrGotoAsync(tabPath, cancellationToken)", method, StringComparison.Ordinal);
        Assert.Contains("VillageIdentityReconciler.SameCoordinates", method, StringComparison.Ordinal);
        Assert.Contains("CurrentPageHasUsableTravianShellAsync", method, StringComparison.Ordinal);
        Assert.Contains("expectedContentSelector", method, StringComparison.Ordinal);
        Assert.True(method.IndexOf("await TryClickFirstVisibleEnabledAsync(", StringComparison.Ordinal)
            < method.IndexOf("await GotoAsync(tabPath, cancellationToken)", StringComparison.Ordinal));
    }

    private static string ReadNavigationSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "TbotUltra.Worker", "Services", "Automation", "Core", "TravianClient.Navigation.cs");
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new DirectoryNotFoundException("Could not locate the TravianClient navigation source.");
    }
}
