using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services.Logging;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TerminalViewModelTests
{
    private static TerminalEntryRow Row(LogCategory category, bool verbose) =>
        new() { Text = "line", Category = category, IsVerbose = verbose };

    [Fact]
    public void ShouldShow_DefaultCleanModeHidesVerboseRows()
    {
        var vm = new TerminalViewModel();

        Assert.True(vm.CleanMode);
        Assert.False(vm.ShouldShow(Row(LogCategory.All, verbose: true)));
        Assert.True(vm.ShouldShow(Row(LogCategory.All, verbose: false)));
    }

    [Fact]
    public void ShouldShow_CleanModeOffShowsVerboseRows()
    {
        var vm = new TerminalViewModel { CleanMode = false };

        Assert.True(vm.ShouldShow(Row(LogCategory.All, verbose: true)));
    }

    [Fact]
    public void ShouldShow_CategoryFilterMatchesOnlySelectedCategory()
    {
        var vm = new TerminalViewModel { CleanMode = false, FilterCategory = LogCategory.Errors };

        Assert.True(vm.ShouldShow(Row(LogCategory.Errors, verbose: false)));
        Assert.False(vm.ShouldShow(Row(LogCategory.Farming, verbose: false)));
    }

    [Fact]
    public void ShouldShow_PacingViewShowsVerboseRowsEvenInCleanMode()
    {
        var vm = new TerminalViewModel { CleanMode = true, FilterCategory = LogCategory.Pacing };

        Assert.True(vm.ShouldShow(Row(LogCategory.Pacing, verbose: true)));
        Assert.False(vm.ShouldShow(Row(LogCategory.Farming, verbose: true)));
    }

    [Fact]
    public void ShouldShow_NonPacingViewKeepsCleanFilteringForVerboseRows()
    {
        var vm = new TerminalViewModel { CleanMode = true, FilterCategory = LogCategory.Errors };

        Assert.False(vm.ShouldShow(Row(LogCategory.Errors, verbose: true)));
        Assert.True(vm.ShouldShow(Row(LogCategory.Errors, verbose: false)));
    }
}
