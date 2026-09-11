using TbotUltra.Core.Farming;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class FarmListDispatchStateStoreTests
{
    [Fact]
    public void SaveAndLoad_PersistsStateByStableListId()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbot-farmlist-dispatch-tests", Guid.NewGuid().ToString("N"));
        var sentAt = new DateTimeOffset(2026, 8, 2, 12, 30, 0, TimeSpan.Zero);
        var key = FarmListDispatchStateStore.CreateKey("12345", "Renamed list");

        FarmListDispatchStateStore.Save(root, "alice", new Dictionary<string, FarmListDispatchState>
        {
            [key] = new(sentAt, Failed: false),
        });

        var loaded = FarmListDispatchStateStore.Load(root, "alice");

        Assert.Equal("lid:12345", key);
        Assert.Equal(sentAt, loaded[key].LastSentAtUtc);
        Assert.False(loaded[key].Failed);
    }

    [Fact]
    public void SaveAndLoad_PersistsIndependentIntervalAndDeadline()
    {
        var root = Path.Combine(Path.GetTempPath(), "tbot-farmlist-dispatch-tests", Guid.NewGuid().ToString("N"));
        var next = new DateTimeOffset(2026, 9, 11, 12, 10, 0, TimeSpan.Zero);

        FarmListDispatchStateStore.Save(root, "alice", new Dictionary<string, FarmListDispatchState>
        {
            ["lid:42"] = new(next.AddMinutes(-5), false, 5, 10, next),
        });

        var state = FarmListDispatchStateStore.Load(root, "alice")["lid:42"];

        Assert.Equal(5, state.IntervalMinMinutes);
        Assert.Equal(10, state.IntervalMaxMinutes);
        Assert.Equal(next, state.NextSendAtUtc);
    }

    [Fact]
    public void CreateKey_UsesNameOnlyWhenTheListIdIsUnavailable()
    {
        Assert.Equal("name:Raiders", FarmListDispatchStateStore.CreateKey(null, "Raiders"));
    }

    [Fact]
    public void IsSuccessfulDispatch_CompletedSendWithoutCooldown_IsSuccessful()
    {
        Assert.True(FarmListDispatchStateStore.IsSuccessfulDispatch(
            sendActionCompleted: true,
            remainingSeconds: 0));
    }

    [Fact]
    public void ShouldTrackDispatch_SendAllIncludesDisabledReadyLists()
    {
        Assert.True(FarmListDispatchStateStore.ShouldTrackDispatch(
            sendAllLists: true,
            isEnabled: false,
            isReady: true,
            isEmpty: false));
        Assert.False(FarmListDispatchStateStore.ShouldTrackDispatch(
            sendAllLists: false,
            isEnabled: false,
            isReady: true,
            isEmpty: false));
    }
}
