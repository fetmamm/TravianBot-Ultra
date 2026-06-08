using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BackgroundTaskTrackerTests
{
    [Fact]
    public async Task StopAsync_CancelsAndWaitsForRunningTask()
    {
        using var tracker = new BackgroundTaskTracker();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(tracker.Run(async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                canceled.SetResult();
            }
        }));

        await started.Task;
        Assert.True(await tracker.StopAsync(TimeSpan.FromSeconds(2)));
        await canceled.Task;
    }

    [Fact]
    public async Task StopAsync_PreventsNewTasks()
    {
        using var tracker = new BackgroundTaskTracker();

        Assert.True(await tracker.StopAsync(TimeSpan.FromSeconds(1)));
        Assert.False(tracker.Run(_ => Task.CompletedTask));
        Assert.False(tracker.Track(Task.CompletedTask));
    }
}
