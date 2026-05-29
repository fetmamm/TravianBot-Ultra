using System.Collections.Generic;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ResourcesViewModelTests
{
    [Fact]
    public void ResolveQueuedResourceTarget_ReturnsQueuedTarget_WhenAboveCurrentLevel()
    {
        var vm = new ResourcesViewModel();
        var queued = new Dictionary<int, int> { [3] = 7 };

        var target = vm.ResolveQueuedResourceTarget(slotId: 3, currentLevel: 5, queued);

        Assert.Equal(7, target);
    }

    [Fact]
    public void ResolveQueuedResourceTarget_PrefersRememberedTarget_WhenHigherThanQueued()
    {
        var vm = new ResourcesViewModel();
        vm.RememberPendingTarget(3, 9);
        var queued = new Dictionary<int, int> { [3] = 7 };

        var target = vm.ResolveQueuedResourceTarget(slotId: 3, currentLevel: 5, queued);

        Assert.Equal(9, target);
    }

    [Fact]
    public void ResolveQueuedResourceTarget_NoQueuedTarget_ForgetsSlotAndReturnsNull()
    {
        var vm = new ResourcesViewModel();
        vm.RememberPendingTarget(3, 9);

        var target = vm.ResolveQueuedResourceTarget(slotId: 3, currentLevel: 5, new Dictionary<int, int>());

        Assert.Null(target);
        Assert.False(vm.TryGetPendingTarget(3, out _));
    }

    [Fact]
    public void ResolveQueuedResourceTarget_TargetReached_ForgetsSlotAndReturnsNull()
    {
        var vm = new ResourcesViewModel();
        var queued = new Dictionary<int, int> { [3] = 5 };

        var target = vm.ResolveQueuedResourceTarget(slotId: 3, currentLevel: 5, queued);

        Assert.Null(target);
        Assert.False(vm.TryGetPendingTarget(3, out _));
    }

    [Fact]
    public void ClearPendingTargets_RemovesAllRememberedTargets()
    {
        var vm = new ResourcesViewModel();
        vm.RememberPendingTarget(1, 4);
        vm.RememberPendingTarget(2, 6);

        vm.ClearPendingTargets();

        Assert.False(vm.TryGetPendingTarget(1, out _));
        Assert.False(vm.TryGetPendingTarget(2, out _));
    }
}
