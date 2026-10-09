using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ConstructionAffordabilityFlowTests
{
    [Fact]
    public void SingleResourceUpgrade_RunsCatalogPreflightBeforeBuildPageAnalysis()
    {
        var source = ReadAutomationSource("Resources", "TravianClient.Resources.Upgrade.cs");
        var method = Slice(source, "public async Task<string> UpgradeResourceToLevelAsync", "public async Task<string> UpgradeAllResourcesToLevelAsync");

        AssertOrdered(method, "EvaluateLiveConstructionAffordabilityAsync", "AnalyzeUpgradeActionabilityAsync");
    }

    [Fact]
    public void BuildingUpgradeFlows_RunCatalogPreflightBeforeOpeningBuildPage()
    {
        var source = ReadAutomationSource("Buildings", "TravianClient.Buildings.UpgradeFlow.cs");
        var toLevel = Slice(source, "public async Task<string> UpgradeBuildingToLevelAsync", "private async Task<UpgradeResourceWaitSnapshot>");
        var toMax = Slice(source, "public async Task<string> UpgradeBuildingToMaxAsync", "private async Task<UpgradeProgressResult> WaitForBuildingLevelAdvanceAsync");

        AssertOrdered(toLevel, "EvaluateBuildingUpgradeAffordabilityAsync", "EnsureCurrentBuildPageForActionAsync");
        AssertOrdered(toMax, "EvaluateBuildingUpgradeAffordabilityAsync", "EnsureCurrentBuildPageForActionAsync");
    }

    [Fact]
    public void ConstructBuilding_RunsCatalogPreflightBeforeOpeningSlotPage()
    {
        var source = ReadAutomationSource("Buildings", "TravianClient.Buildings.ConstructFlow.cs");
        var method = Slice(source, "public async Task<string> ConstructBuildingAsync", "private async Task<");

        AssertOrdered(method, "EvaluateLiveConstructionAffordabilityAsync", "await OpenConstructSlotPageAsync(slotId, categoryIndex, cancellationToken)");
    }

    [Fact]
    public void BulkResourceUpgrade_UsesSharedPlannerForBlockedCatalogCandidates()
    {
        var source = ReadAutomationSource("Resources", "TravianClient.Resources.Upgrade.cs");
        var method = Slice(source, "public async Task<string> UpgradeAllResourcesToLevelAsync", "private async Task<UpgradeProgressResult>");

        Assert.Contains("EvaluateConstructionAffordabilityAsync", method, StringComparison.Ordinal);
        Assert.Contains("decision.ShouldOpenBuildPage", method, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var blockedCandidate in localPlan.BlockedByResources)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void HeroRevalidation_ReservesOnlyFromTheExactBuildPageDialog()
    {
        var affordabilitySource = ReadAutomationSource("Construction", "TravianClient.ConstructionAffordability.cs");
        var heroTransferSource = ReadAutomationSource("Hero", "TravianClient.HeroResourceTransfer.cs");

        Assert.DoesNotContain("TryReserveConstructionHeroInventoryProbe", affordabilitySource, StringComparison.Ordinal);
        Assert.Contains("TryReserveConstructionHeroInventoryProbe", heroTransferSource, StringComparison.Ordinal);
        AssertOrdered(heroTransferSource, "if (!transferAvailable)", "TryReserveConstructionHeroInventoryProbe");
        Assert.DoesNotContain("ReadHeroInventoryResourcesAsync", affordabilitySource, StringComparison.Ordinal);
        Assert.Contains("lock (HeroInventoryCacheSync)", heroTransferSource, StringComparison.Ordinal);
        Assert.Contains("ConstructionProbe = new HeroConstructionProbeState(observations, reservedUntil)", heroTransferSource, StringComparison.Ordinal);
    }

    [Fact]
    public void HeroRevalidation_InsufficientNonEmptyCacheCanReadDialogAndFailedReadBacksOff()
    {
        var source = ReadAutomationSource("Hero", "TravianClient.HeroResourceTransfer.cs");

        Assert.Contains("&& HeroInventoryProbePolicy.ShouldRevalidateConstruction(cachedSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("DeferUnreadableConstructionHeroInventoryProbe", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HeroRevalidation_ResourceWaitCannotOutlastSharedProbeDeadline()
    {
        var source = ReadAutomationSource("Buildings", "TravianClient.Buildings.UpgradeFlow.cs");
        var method = Slice(source, "private async Task<UpgradeResourceWaitSnapshot> ReadUpgradeResourceWaitSnapshotAsync", "private async Task<IReadOnlyDictionary<string, double?>> ReadCachedProductionByHourForActiveVillageAsync");

        Assert.Contains("HonorConstructionHeroRevalidationDeadline(heroLimitSnapshot)", method, StringComparison.Ordinal);
        Assert.Contains("HonorConstructionHeroRevalidationDeadline(snapshot)", method, StringComparison.Ordinal);
        Assert.Contains("HonorConstructionHeroRevalidationDeadline(liveSnapshot)", method, StringComparison.Ordinal);
        Assert.Contains("snapshot with { WaitSeconds = probeWaitSeconds", method, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Expected to find '{first}'.");
        Assert.True(secondIndex > firstIndex, $"Expected '{first}' before '{second}'.");
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find '{startMarker}'.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected to find '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }

    private static string ReadAutomationSource(string area, string fileName)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            area,
            fileName));
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
