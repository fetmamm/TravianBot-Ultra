using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProxyStatusUiSourceTests
{
    [Fact]
    public void ProxyCard_IsBetweenActiveVillageAndGoldInTopbar()
    {
        var xaml = ReadDesktopSource("MainWindow.xaml");
        var activeVillage = xaml.IndexOf("x:Name=\"ActiveVillageTextBlock\"", StringComparison.Ordinal);
        var proxyCard = xaml.IndexOf("x:Name=\"ProxyStatusCard\"", StringComparison.Ordinal);
        var goldCard = xaml.IndexOf("Text=\"Gold\"", proxyCard, StringComparison.Ordinal);

        Assert.True(activeVillage >= 0);
        Assert.True(proxyCard > activeVillage);
        Assert.True(goldCard > proxyCard);
        Assert.Equal(1, CountOccurrences(xaml, "Text=\"Proxy\""));
    }

    [Fact]
    public void AccountEditor_OffersHiddenIpDefaultBelowOwnIpProtection()
    {
        var xaml = ReadDesktopSource("AccountsWindow.xaml");
        var neverUseOwnIp = xaml.IndexOf("x:Name=\"NeverUseOwnIpCheckBox\"", StringComparison.Ordinal);
        var hideIp = xaml.IndexOf("x:Name=\"HideIpFromUiCheckBox\"", StringComparison.Ordinal);
        var code = ReadDesktopSource("AccountsWindow.xaml.cs");

        Assert.True(hideIp > neverUseOwnIp);
        Assert.Contains("HideIpFromUiCheckBox.IsChecked = true;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_MasksConnectionDetailsWhenConfigured()
    {
        var source = ReadDesktopSource("MainWindow.ServerAccount.cs");

        Assert.Contains("_proxyStatusHideIpFromUi ? \"IP hidden\" : visibleDetail", source, StringComparison.Ordinal);
        Assert.Contains("allowConfiguredName: !_proxyStatusHideIpFromUi", source, StringComparison.Ordinal);
    }

    private static string ReadDesktopSource(string fileName) => File.ReadAllText(Path.Combine(
        ProjectRootLocator.FindProjectRoot(),
        "src",
        "TbotUltra.Desktop",
        fileName));

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0; index += search.Length)
        {
            count++;
        }

        return count;
    }
}
