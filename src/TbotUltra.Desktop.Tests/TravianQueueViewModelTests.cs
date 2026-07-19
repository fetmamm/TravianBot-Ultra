using System.Collections.Generic;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TravianQueueViewModelTests
{
    private static TravianBuildQueueRow Row(string name) => new() { Name = name };

    [Fact]
    public void ApplyBuildQueueRows_UpdatesSharedRowsInPlace()
    {
        var vm = new TravianQueueViewModel();
        vm.ApplyBuildQueueRows(new List<TravianBuildQueueRow> { Row("Old A"), Row("Old B") });
        var firstInstance = vm.BuildQueueRows[0];

        vm.ApplyBuildQueueRows(new List<TravianBuildQueueRow> { Row("New A"), Row("New B") });

        Assert.Same(firstInstance, vm.BuildQueueRows[0]);
        Assert.Equal("New A", vm.BuildQueueRows[0].Name);
        Assert.Equal("New B", vm.BuildQueueRows[1].Name);
    }

    [Fact]
    public void ApplyBuildQueueRows_TrimsExtraTailRows()
    {
        var vm = new TravianQueueViewModel();
        vm.ApplyBuildQueueRows(new List<TravianBuildQueueRow> { Row("A"), Row("B"), Row("C") });

        vm.ApplyBuildQueueRows(new List<TravianBuildQueueRow> { Row("A") });

        Assert.Single(vm.BuildQueueRows);
    }

    [Fact]
    public void ApplyBuildQueueRows_AppendsNewRowsBeyondShared()
    {
        var vm = new TravianQueueViewModel();
        vm.ApplyBuildQueueRows(new List<TravianBuildQueueRow> { Row("A") });

        vm.ApplyBuildQueueRows(new List<TravianBuildQueueRow> { Row("A"), Row("B"), Row("C") });

        Assert.Equal(3, vm.BuildQueueRows.Count);
        Assert.Equal("C", vm.BuildQueueRows[2].Name);
    }

    [Fact]
    public void ApplyBuildQueueRows_EmptySnapshotClearsRows()
    {
        var vm = new TravianQueueViewModel();
        vm.ApplyBuildQueueRows(new List<TravianBuildQueueRow> { Row("A") });

        vm.ApplyBuildQueueRows(new List<TravianBuildQueueRow>());

        Assert.Empty(vm.BuildQueueRows);
    }
}
