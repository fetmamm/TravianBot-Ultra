using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class InboxViewModelTests
{
    [Fact]
    public void ActionsEnabled_DefaultsToTrue()
    {
        var vm = new InboxViewModel();

        Assert.True(vm.ActionsEnabled);
    }

    [Fact]
    public void UnreadMessages_UpdatesDerivedDisplayState()
    {
        var vm = new InboxViewModel { UnreadMessages = 3, UnreadReports = 1 };

        Assert.Equal("Unread: 3", vm.MessageUnreadText);
        Assert.Equal("Unread: 1", vm.ReportsUnreadText);
        Assert.Equal("Messages 3 | Reports 1", vm.NavTooltip);
        Assert.True(vm.HasUnreadMessages);
    }

    [Fact]
    public void HasUnreadMessages_FalseWhenNoUnread()
    {
        var vm = new InboxViewModel { UnreadMessages = 0 };

        Assert.False(vm.HasUnreadMessages);
    }
}
