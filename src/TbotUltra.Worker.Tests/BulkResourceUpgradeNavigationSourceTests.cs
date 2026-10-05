using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BulkResourceUpgradeNavigationSourceTests
{
    [Fact]
    public void CandidateScan_ReusesOverviewConstructionSnapshotInsteadOfNavigatingPerField()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Resources",
            "TravianClient.Resources.Upgrade.cs"));
        var loopStart = source.IndexOf("foreach (var candidate in candidateRows)", StringComparison.Ordinal);
        var loopEnd = source.IndexOf("if (!attemptedAny)", loopStart, StringComparison.Ordinal);

        Assert.True(loopStart >= 0 && loopEnd > loopStart, "Could not locate the bulk resource candidate scan.");
        var candidateScan = source[loopStart..loopEnd];
        Assert.DoesNotContain("ReadHighestKnownQueuedResourceLevelAsync", candidateScan, StringComparison.Ordinal);
        Assert.Contains("ResourceConstructionQueueMatcher.HighestQueuedLevelForSlot", candidateScan, StringComparison.Ordinal);
        Assert.Contains("queuedLevelsBySlot.TryGetValue", candidateScan, StringComparison.Ordinal);
        Assert.Contains("queuedProjectionState.Record(slot, nextLevel", candidateScan, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticDorf1Redirect_AppliesPageLoadPacingBeforeNextCandidate()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Resources",
            "TravianClient.Resources.Upgrade.cs"));
        var methodStart = source.IndexOf("private async Task NavigateToResourceFieldsAfterUpgradeClickAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private async Task EnsureResourceFieldsPageAsync", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains("ApplyPacingDelayAsync", method, StringComparison.Ordinal);
        Assert.Contains("after resource upgrade redirect", method, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleAndBulkResourceUpgrades_RunFinalSafetyCheckBeforeAnyAction()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Resources",
            "TravianClient.Resources.Upgrade.cs"));

        Assert.Equal(3, source.Split("VerifyResourceUpgradePreClickSafetyAsync", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, source.Split("clickSafety.CandidateIndex", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, source.Split("pre-click safety stopped upgrade", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BuildingUpgradeFlows_RunFinalSafetyCheckAndHaveNoBroadClickFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Buildings",
            "TravianClient.Buildings.UpgradeFlow.cs"));

        Assert.Equal(4, source.Split("VerifyUpgradePreClickSafetyAsync", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("anyPattern", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback: any element matching the broader", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeActionabilityAnalysis_DoesNotApplyClickPacingBeforeReadOnlyChecks()
    {
        var analysisSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Buildings",
            "TravianClient.Buildings.UpgradeAnalysis.cs"));
        var methodStart = analysisSource.IndexOf("private async Task<UpgradeAttemptResult> AnalyzeUpgradeActionabilityAsync", StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "Could not locate the upgrade actionability analysis.");
        var analysisMethod = analysisSource[methodStart..];
        Assert.DoesNotContain("DelayBeforeClickAsync", analysisMethod, StringComparison.Ordinal);

        var clickSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Buildings",
            "TravianClient.Upgrade.cs"));
        var clickStart = clickSource.IndexOf("private async Task ClickDetectedUpgradeCandidateAsync", StringComparison.Ordinal);
        var clickEnd = clickSource.IndexOf("private async Task<int?> ReadUpgradeDurationSecondsOnCurrentPageAsync", clickStart, StringComparison.Ordinal);

        Assert.True(clickStart >= 0 && clickEnd > clickStart, "Could not locate the upgrade click methods.");
        var clickMethods = clickSource[clickStart..clickEnd];
        Assert.Contains("DelayBeforeClickAsync", clickMethods, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TbotUltra.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate TbotUltra.sln from the test output directory.");
    }
}
