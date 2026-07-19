using TbotUltra.Desktop.Services.Logging;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SerialFileAppendWriterTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tbot-log-writer-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FlushAsync_PersistsQueuedBatchesInOrder()
    {
        var path = Path.Combine(_directory, "session.log");
        await using var writer = new SerialFileAppendWriter(path);

        writer.Append(["one", "two"]);
        writer.Append(["three"]);
        await writer.FlushAsync();

        Assert.Equal(["one", "two", "three"], File.ReadAllLines(path));
    }

    [Fact]
    public async Task AppendSessionLines_KeepsAlarmsInAlarmSection()
    {
        var path = Path.Combine(_directory, "session.log");
        File.WriteAllLines(path,
        [
            "=== Tbot Ultra Session Log ===",
            "",
            "=== ALARMS ===",
            "",
            "=== LOGS ===",
            "",
        ]);
        await using var writer = new SerialFileAppendWriter(path);

        writer.AppendSessionLines(["log one"], []);
        writer.AppendSessionLines(["log two"], ["alarm one"]);
        writer.AppendSessionLines([], ["alarm two"]);
        await writer.FlushAsync();

        Assert.Equal(
        [
            "=== Tbot Ultra Session Log ===",
            "",
            "=== ALARMS ===",
            "alarm one",
            "alarm two",
            "",
            "=== LOGS ===",
            "",
            "log one",
            "log two",
        ],
        File.ReadAllLines(path));
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
}
