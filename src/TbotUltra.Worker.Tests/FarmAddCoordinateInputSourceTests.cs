using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmAddCoordinateInputSourceTests
{
    [Fact]
    public void ReusedAddTargetForm_ReplacesAndVerifiesBothCoordinatesBeforeValidation()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Farming",
            "TravianClient.FarmAdd.cs"));

        var fillCall = source.IndexOf("FillAndVerifyFarmCoordinatesAsync", StringComparison.Ordinal);
        var validation = fillCall >= 0
            ? source.IndexOf("var validationTriggered", fillCall, StringComparison.Ordinal)
            : -1;

        Assert.True(fillCall >= 0 && validation > fillCall,
            "Coordinates must be replaced and verified before Travian target validation starts.");
        Assert.Contains("await TypeHumanlyAsync(xInput, expectedX", source, StringComparison.Ordinal);
        Assert.Contains("await TypeHumanlyAsync(yInput, expectedY", source, StringComparison.Ordinal);
        Assert.Contains("var actualX = await xInput.InputValueAsync", source, StringComparison.Ordinal);
        Assert.Contains("var actualY = await yInput.InputValueAsync", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(actualX, expectedX", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(actualY, expectedY", source, StringComparison.Ordinal);
        Assert.Contains("coordinate input mismatch", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidCoordinates_ReusesOpenFormWithoutInitialPacing()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Farming",
            "TravianClient.FarmAdd.cs"));

        Assert.Contains("var reuseAfterInvalidCoordinates = reuseOpenFormAfterInvalidCoordinates", source, StringComparison.Ordinal);
        Assert.Contains("reuseAfterInvalidCoordinates: reuseAfterInvalidCoordinates", source, StringComparison.Ordinal);
        Assert.Contains("if (!reuseAfterInvalidCoordinates)", source, StringComparison.Ordinal);

        var invalidOutcome = source.IndexOf("saveOutcome == AddRaidSaveOutcome.InvalidCoordinates", StringComparison.Ordinal);
        var occupiedOutcome = source.IndexOf("saveOutcome == AddRaidSaveOutcome.OccupiedOasisSkipped", invalidOutcome, StringComparison.Ordinal);
        var failedOutcome = source.IndexOf("Failed to save farm", occupiedOutcome, StringComparison.Ordinal);
        Assert.True(invalidOutcome >= 0 && occupiedOutcome > invalidOutcome && failedOutcome > occupiedOutcome);
        Assert.Contains("reuseOpenFormAfterInvalidCoordinates = true;", source[invalidOutcome..occupiedOutcome], StringComparison.Ordinal);
        Assert.DoesNotContain("reuseOpenFormAfterInvalidCoordinates = true;", source[occupiedOutcome..failedOutcome], StringComparison.Ordinal);

        var pacingBranch = source.IndexOf("if (!reuseAfterInvalidCoordinates)", StringComparison.Ordinal);
        var fillCoordinates = source.IndexOf("FillAndVerifyFarmCoordinatesAsync", pacingBranch, StringComparison.Ordinal);
        var initialSettle = source.IndexOf("Random.Shared.Next(200, 400)", pacingBranch, StringComparison.Ordinal);
        var clickPacing = source.IndexOf("DelayBeforeClickAsync(cancellationToken, \"add farm: enter coordinates\")", pacingBranch, StringComparison.Ordinal);

        Assert.True(pacingBranch >= 0 && initialSettle > pacingBranch && initialSettle < fillCoordinates,
            "The initial settle must run only inside the normal pacing branch.");
        Assert.True(clickPacing > pacingBranch && clickPacing < fillCoordinates,
            "Click pacing must run only inside the normal pacing branch.");
    }

    [Fact]
    public void LookupTimeout_RetriesSameCoordinateBeforeReportingFailure()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Farming",
            "TravianClient.FarmAdd.cs"));

        Assert.Contains("AddRaidSaveOutcome.LookupTimedOut", source, StringComparison.Ordinal);
        Assert.Contains("Retrying Add target lookup", source, StringComparison.Ordinal);

        var retryLoop = source.IndexOf("for (var lookupAttempt = 1; lookupAttempt <= AddTargetLookupMaxAttempts; lookupAttempt++)", StringComparison.Ordinal);
        var firstAttempt = source.IndexOf("saveOutcome = await TryFillAddRaidFormAndSaveAsync", retryLoop, StringComparison.Ordinal);
        var retryCheck = source.IndexOf(
            "saveOutcome is not (AddRaidSaveOutcome.LookupTimedOut or AddRaidSaveOutcome.IdentityUnavailable)",
            firstAttempt,
            StringComparison.Ordinal);
        var reopenForm = source.IndexOf("await OpenAddRaidFormAsync(lid, cancellationToken);", retryCheck, StringComparison.Ordinal);
        var failedOutcome = source.IndexOf("Failed to validate farm", reopenForm, StringComparison.Ordinal);

        Assert.True(retryLoop >= 0 && firstAttempt > retryLoop && retryCheck > firstAttempt);
        Assert.True(reopenForm > retryCheck);
        Assert.True(failedOutcome > reopenForm, "Failure must be reported only after the bounded retry.");
        Assert.Contains("Save was not attempted", source[failedOutcome..], StringComparison.Ordinal);
    }
}
