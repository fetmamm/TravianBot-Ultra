using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmListDispatchConfirmationTests
{
    [Theory]
    [InlineData(false, false, false, "none")]
    [InlineData(true, false, true, "success")]
    [InlineData(false, true, true, "error")]
    [InlineData(true, true, true, "success+error")]
    public void Confirmation_TreatsEitherOfficialResponseMarkerAsSent(
        bool hasSuccess,
        bool hasError,
        bool expectedConfirmed,
        string expectedDescription)
    {
        var confirmation = new FarmListDispatchConfirmation(hasSuccess, hasError);

        Assert.Equal(expectedConfirmed, confirmation.IsConfirmed);
        Assert.Equal(expectedDescription, confirmation.Description);
    }

    [Fact]
    public void WaitForConfirmation_UsesOnlyOfficialResponseMarkers()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Farming",
            "TravianClient.FarmLists.cs"));
        var methodStart = source.IndexOf(
            "private async Task<FarmListDispatchConfirmation> WaitForFarmListDispatchConfirmationAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public async Task<FarmListLossDeactivationResult>", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains(".farmListStatus svg.success", method, StringComparison.Ordinal);
        Assert.Contains(".farmListStatus svg.error", method, StringComparison.Ordinal);
        Assert.DoesNotContain("beingRaided", method, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("startFarmList", method, StringComparison.Ordinal);
    }

    [Fact]
    public void SequentialSend_StopsBeforeAnotherClickWhenResponseIsMissing()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Farming",
            "TravianClient.FarmLists.cs"));
        var methodStart = source.IndexOf(
            "private async Task<FarmListSendBatchResult> SendFarmListsSequentiallyAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf("/// <summary>", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd].Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "within 15 seconds after Start; not marking it sent and stopping this send batch.\");\n                break;",
            method,
            StringComparison.Ordinal);
    }
}
