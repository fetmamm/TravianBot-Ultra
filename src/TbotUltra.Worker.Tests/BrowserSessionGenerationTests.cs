using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserSessionGenerationTests
{
    [Fact]
    public void ThrowIfStale_AcceptsCurrentGeneration()
    {
        var generation = new BrowserSessionGeneration();
        var captured = generation.Capture();

        generation.ThrowIfStale(captured);
    }

    [Fact]
    public void ThrowIfStale_RejectsBrowserCreatedAfterShutdownStarted()
    {
        var generation = new BrowserSessionGeneration();
        var capturedBeforeOpenPage = generation.Capture();

        generation.Invalidate();

        Assert.Throws<OperationCanceledException>(() => generation.ThrowIfStale(capturedBeforeOpenPage));
    }

    [Fact]
    public void Invalidate_IsMonotonicAcrossConcurrentShutdowns()
    {
        var generation = new BrowserSessionGeneration();

        Parallel.For(0, 100, _ => generation.Invalidate());

        Assert.Equal(100, generation.Capture());
    }
}
