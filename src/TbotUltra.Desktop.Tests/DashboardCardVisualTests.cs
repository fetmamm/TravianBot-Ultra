using System.Xml.Linq;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class DashboardCardVisualTests
{
    [Fact]
    public void AutomationCards_DoNotUseSelectableListRowVisuals()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TbotUltra.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        var xaml = XDocument.Load(Path.Combine(root, "src", "TbotUltra.Desktop", "Views", "DashboardPanel.xaml"));
        var list = xaml.Descendants().Single(element =>
            element.Name.LocalName == "ListBox"
            && element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "AutomationLoopListBox"));
        var itemStyle = list.Descendants().Single(element => element.Name.LocalName == "ListBox.ItemContainerStyle");
        var setters = itemStyle.Descendants().Where(element => element.Name.LocalName == "Setter").ToList();

        Assert.Contains(setters, setter => (string?)setter.Attribute("Property") == "FocusVisualStyle"
            && (string?)setter.Attribute("Value") == "{x:Null}");
        Assert.Contains(setters, setter => (string?)setter.Attribute("Property") == "Focusable"
            && (string?)setter.Attribute("Value") == "False");
        Assert.Contains(setters, setter => (string?)setter.Attribute("Property") == "Template");
    }
}
