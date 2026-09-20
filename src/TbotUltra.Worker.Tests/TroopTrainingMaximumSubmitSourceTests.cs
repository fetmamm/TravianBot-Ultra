using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TroopTrainingMaximumSubmitSourceTests
{
    [Fact]
    public void MaximumMode_ClicksTravianAmountLinkBeforeTrainInsteadOfTyping()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Training",
            "TravianClient.TroopTraining.cs"));
        var submitStart = source.IndexOf("private async Task<bool> SubmitTroopTrainingFromCurrentPageAsync", StringComparison.Ordinal);
        var maxStart = source.IndexOf("if (useMaxShortcut)", submitStart, StringComparison.Ordinal);
        var nonMaxStart = source.IndexOf("else", maxStart, StringComparison.Ordinal);
        var trainClick = source.IndexOf("await submitButton.ClickAsync", nonMaxStart, StringComparison.Ordinal);
        Assert.True(submitStart >= 0 && maxStart > submitStart && nonMaxStart > maxStart && trainClick > nonMaxStart);

        var maximumBranch = source[maxStart..nonMaxStart];
        Assert.Contains("ClickTroopTrainingMaxAmountAsync", maximumBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("TypeHumanlyAsync", maximumBranch, StringComparison.Ordinal);
        Assert.True(source.IndexOf("ClickTroopTrainingMaxAmountAsync", maxStart, StringComparison.Ordinal) < trainClick);
        Assert.Contains("' details '", source, StringComparison.Ordinal);
        Assert.Contains(".cta a[href='#']", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Submit_RetriesWhenFastTrainingAutoRefreshReplacesTheForm()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Training",
            "TravianClient.TroopTraining.cs"));
        var submitStart = source.IndexOf("private async Task<bool> SubmitTroopTrainingFromCurrentPageAsync", StringComparison.Ordinal);
        var submitEnd = source.IndexOf("    // Resolves the training form's amount-input NAME", submitStart, StringComparison.Ordinal);

        Assert.True(submitStart >= 0 && submitEnd > submitStart);
        var submitBody = source[submitStart..submitEnd];

        Assert.Contains("const int submitAttempts = 3;", submitBody, StringComparison.Ordinal);
        Assert.Contains("for (var submitAttempt = 1; submitAttempt <= submitAttempts; submitAttempt++)", submitBody, StringComparison.Ordinal);
        Assert.Contains("await WaitForPageReadyAsync(cancellationToken);", submitBody, StringComparison.Ordinal);
        Assert.Contains("form auto-refreshed", submitBody, StringComparison.OrdinalIgnoreCase);
        var guard = submitBody.IndexOf("TroopTrainingExecutionSettings.VerifyBeforeSubmit();", StringComparison.Ordinal);
        var click = submitBody.IndexOf("await submitButton.ClickAsync();", StringComparison.Ordinal);
        var pacing = submitBody.LastIndexOf("await DelayBeforeClickAsync", StringComparison.Ordinal);
        Assert.True(pacing >= 0 && guard > pacing && click > guard,
            "Saved training settings must be checked after pacing and before the Train click.");
    }

    [Fact]
    public void ResultLog_IncludesTheActiveVillage()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Training",
            "TravianClient.TroopTraining.cs"));

        Assert.Contains(
            "[troops] village='{status.ActiveVillage}' {candidate.Request.BuildingName} result",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExhaustedFormPreparation_DefersForAtLeastSixtySeconds()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Training",
            "TravianClient.TroopTraining.cs"));

        Assert.Contains("var retrySeconds = Math.Max(60, fallbackCooldownSeconds);", source, StringComparison.Ordinal);
        Assert.Contains("form did not remain stable", source, StringComparison.Ordinal);
        Assert.Contains("queue_wait_seconds={retrySeconds}", source, StringComparison.Ordinal);
    }
}
