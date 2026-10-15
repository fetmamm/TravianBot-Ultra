using Microsoft.Playwright;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidDispatchVerificationTests
{
    [Fact]
    public void FinalConfirm_UsesHeroWidgetInsteadOfRallyPointRows()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRootLocator.FindProjectRoot(),
            "src", "TbotUltra.Worker", "Services", "Automation", "Hero", "TravianClient.Hero.Raiding.cs"));
        var start = source.IndexOf("internal async Task<bool> SendHeroRaidAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("private sealed record HeroRaidingEquipmentRead", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var sendFlow = source[start..end];
        Assert.Contains("VerifyHeroRaidDispatchAsync", sendFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("IsHeroRaidOutgoingVisibleAsync", sendFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Paths.RallyPointTroops", sendFlow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigationDuringWidgetRead_RecoversWithoutAnotherConfirm()
    {
        var reads = 0;
        var delays = 0;
        var verified = await TravianClient.VerifyHeroRaidDispatchAsync(
            _ =>
            {
                reads++;
                if (reads == 1)
                    throw new PlaywrightException("Execution context was destroyed, most likely because of a navigation");
                return Task.FromResult(HeroRaidDispatchState.Running);
            },
            (_, _) => { delays++; return Task.CompletedTask; },
            _ => { }, CancellationToken.None);

        Assert.True(verified);
        Assert.Equal(2, reads);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task HeroStillHome_DoesNotPretendRaidWasSent()
    {
        var reads = 0;
        var verified = await TravianClient.VerifyHeroRaidDispatchAsync(
            _ => { reads++; return Task.FromResult(HeroRaidDispatchState.Home); },
            (_, _) => Task.CompletedTask, _ => { }, CancellationToken.None);

        Assert.False(verified);
        Assert.Equal(12, reads);
    }

    [Fact]
    public void RaidForm_PacesFocusAndTypesCoordinatesAndHero()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRootLocator.FindProjectRoot(),
            "src", "TbotUltra.Worker", "Services", "Automation", "Hero", "TravianClient.Hero.Raiding.cs"));
        var start = source.IndexOf("internal async Task PrepareHeroRaidingSendFormAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("internal async Task<bool> SendHeroRaidAsync(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var form = source[start..end];

        Assert.Contains("DelayBeforeClickAsync(cancellationToken, \"hero raiding: focus X coordinate\");\n        await TypeHumanlyAsync(xInput", form, StringComparison.Ordinal);
        Assert.Contains("DelayBeforeClickAsync(cancellationToken, \"hero raiding: focus Y coordinate\");\n        await TypeHumanlyAsync(yInput", form, StringComparison.Ordinal);
        Assert.Contains("DelayBeforeClickAsync(cancellationToken, \"hero raiding: focus hero count\");\n        await TypeHumanlyAsync(heroInput", form, StringComparison.Ordinal);
    }
}
