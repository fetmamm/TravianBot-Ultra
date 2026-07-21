using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Desktop.Services.Orchestration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LoopControllerTests
{
    [Fact]
    public void StartLoop_CancelsThePreviousLoopToken()
    {
        using var controller = new LoopController();

        var first = controller.StartLoop("first");
        var second = controller.StartLoop("second");

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void AcquireSessionScopeToken_RearmsAfterCancellation()
    {
        using var controller = new LoopController();

        var first = controller.AcquireSessionScopeToken();
        controller.CancelSessionScope();
        var second = controller.AcquireSessionScopeToken();

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public async Task QueueAutoRunGate_RejectsASecondRunUntilTheFirstLeaseIsReleased()
    {
        using var controller = new LoopController();

        using var first = await controller.TryAcquireQueueAutoRunGateAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await controller.TryAcquireQueueAutoRunGateAsync(CancellationToken.None));

        first.Dispose();
        Assert.NotNull(await controller.TryAcquireQueueAutoRunGateAsync(CancellationToken.None));
    }
}
