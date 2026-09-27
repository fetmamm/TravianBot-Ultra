using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class InboxNavigationStyleSourceTests
{
    [Fact]
    public void UnreadMessages_UseInfoColorInsteadOfAlarmColor()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.xaml"));
        var triggerStart = xaml.IndexOf(
            "<DataTrigger Binding=\"{Binding InboxVm.HasUnreadMessages, ElementName=RootWindow}\" Value=\"True\">",
            StringComparison.Ordinal);
        var triggerEnd = xaml.IndexOf("</DataTrigger>", triggerStart, StringComparison.Ordinal);

        Assert.True(triggerStart >= 0 && triggerEnd > triggerStart, "Unread Messages navigation trigger was not found.");
        var trigger = xaml[triggerStart..triggerEnd];
        Assert.Contains("Background\" Value=\"{DynamicResource InfoBrush}", trigger, StringComparison.Ordinal);
        Assert.DoesNotContain("UnreadBrush", trigger, StringComparison.Ordinal);
        Assert.DoesNotContain("Danger", trigger, StringComparison.Ordinal);
    }
}
