using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ProductionBonusBatchSourceTests
{
    [Fact]
    public void ActivationBatch_AttemptsEveryInitiallyActivatableResourceWithoutInternalCooldownStop()
    {
        var source = ReadProductionBonusSource();
        var methodStart = source.IndexOf("public async Task<string> ActivateProductionBonusVideosAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public async Task<string> ScanProductionBonusTimersAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);

        var method = source[methodStart..methodEnd];
        Assert.Contains("bypassExistingCooldown: resourceIndex > 0", method, StringComparison.Ordinal);
        Assert.DoesNotContain("stopping remaining video attempts", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationBatch_VerifiesEachResourceWithBoundedRetriesAndAlarmsAfterExhaustion()
    {
        var source = ReadProductionBonusSource();
        var method = ExtractMethod(
            source,
            "public async Task<string> ActivateProductionBonusVideosAsync",
            "public async Task<string> ScanProductionBonusTimersAsync");

        Assert.Contains("ProductionBonusVideoMaxAttemptsPerResource", method, StringComparison.Ordinal);
        Assert.Contains("FindUnconfirmedActivations", method, StringComparison.Ordinal);
        Assert.Contains("ALARM:", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionCheck_UsesTheCanonicalProductionBonusBoxParser()
    {
        var source = ReadProductionBonusSource();
        var method = ExtractMethod(
            source,
            "private async Task<bool> WaitForProductionBonusVideoCompletionAsync",
            "private static string ResourceBonusBoxClass");

        Assert.Contains("ReadProductionBonusBoxesRawAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain(".bonusDuration", method, StringComparison.Ordinal);
    }

    private static string ReadProductionBonusSource()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Features",
            "TravianClient.ProductionBonus.cs"));
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
