using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BonusVideoMuteSourceTests
{
    [Fact]
    public void MuteClicksOnlyTheVerifiedVisibleAudioIcon()
    {
        var method = ReadMethod("Hero", "TravianClient.AdventureDanger.cs", "MuteBonusVideoAsync");

        Assert.Contains(
            "frame.Locator(\".atg-gima-audio-button-enabled:not(.atg-gima-hidden)\")",
            method,
            StringComparison.Ordinal);
        Assert.Contains("IsSafeBonusVideoAudioControlAsync(", method, StringComparison.Ordinal);
        Assert.Contains("if (!_config.TurnOffVideoSound)", method, StringComparison.Ordinal);
        Assert.DoesNotContain(".atg-gima-audio-button:has(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Force = true", method, StringComparison.Ordinal);
    }

    [Fact]
    public void MuteSupportsDirectAutoplayVideoWithoutRemovingLegacyAudioButtonSupport()
    {
        var method = ReadMethod("Hero", "TravianClient.AdventureDanger.cs", "MuteBonusVideoAsync");

        var directVideoMute = method.IndexOf("document.querySelectorAll('video')", StringComparison.Ordinal);
        var legacyAudioButton = method.IndexOf(
            ".atg-gima-audio-button-enabled:not(.atg-gima-hidden)",
            StringComparison.Ordinal);

        Assert.True(directVideoMute >= 0, "Direct autoplay media must support safe property-based muting.");
        Assert.Contains("video.muted = true", method, StringComparison.Ordinal);
        Assert.True(
            legacyAudioButton > directVideoMute,
            "The direct-video variant should be handled before retaining the legacy audio-button fallback.");
    }

    [Fact]
    public void PlaybackStart_NeverClicksTheUnverifiedVideoAreaCenter()
    {
        var method = ReadMethod(
            "Hero",
            "TravianClient.AdventureDanger.cs",
            "StartBonusVideoPlayerAsync",
            "DateTimeOffset?");

        Assert.Contains("IsSafeBonusVideoPlayControlAsync(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_page.Mouse.ClickAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("video-area center fallback", method, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackStart_AcceptsVerifiedAutoplayBeforeReportingMissingPlayControl()
    {
        var method = ReadMethod(
            "Hero",
            "TravianClient.AdventureDanger.cs",
            "StartBonusVideoPlayerAsync",
            "DateTimeOffset?");

        var autoplayCheck = method.IndexOf("IsBonusVideoPlaybackActiveAsync(", StringComparison.Ordinal);
        var missingPlayFailure = method.IndexOf(
            "no safe visible play control appeared",
            StringComparison.Ordinal);

        Assert.True(autoplayCheck >= 0, "The player start flow must recognize provider autoplay.");
        Assert.True(
            missingPlayFailure > autoplayCheck,
            "Verified autoplay must be checked before a missing play control closes the video browser.");
        Assert.Contains("MuteBonusVideoAsync(", method[autoplayCheck..missingPlayFailure], StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackStart_PollsForAutoplayThroughoutTheMinimumObservationWindow()
    {
        var method = ReadMethod(
            "Hero",
            "TravianClient.AdventureDanger.cs",
            "StartBonusVideoPlayerAsync",
            "DateTimeOffset?");

        Assert.Contains("MayGiveUpWaitingForPlaybackStart", method, StringComparison.Ordinal);
        Assert.Contains("IsBonusVideoPlaybackActiveAsync", method, StringComparison.Ordinal);
        Assert.Contains("for autoplay or a safe play control", method, StringComparison.Ordinal);
    }

    private static string ReadMethod(
        string area,
        string fileName,
        string methodName,
        string returnType = "bool")
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            area,
            fileName));

        var methodStart = source.IndexOf($"private async Task<{returnType}> {methodName}(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Could not find {methodName}.");

        var nextMethod = source.IndexOf("\n    private ", methodStart + methodName.Length, StringComparison.Ordinal);
        return nextMethod > methodStart
            ? source[methodStart..nextMethod]
            : source[methodStart..];
    }
}
