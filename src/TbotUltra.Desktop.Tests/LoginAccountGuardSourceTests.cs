using System.Xml.Linq;
using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LoginAccountGuardSourceTests
{
    [Fact]
    public void LoginClick_BlocksBeforeStartingWhenTbotAccountIsSignedOut()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot, "src", "TbotUltra.Desktop", "MainWindow.Session.cs"));
        var methodStart = source.IndexOf("private async void LoginButton_Click", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("    // Login function", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];
        var signedInCheck = methodBody.IndexOf("_licenseClient?.IsSignedIn", StringComparison.Ordinal);
        var dialog = methodBody.IndexOf("AppDialog.Show", StringComparison.Ordinal);
        var loginFlow = methodBody.IndexOf("ExecuteLoginFlowAsync", StringComparison.Ordinal);

        Assert.True(signedInCheck >= 0);
        Assert.True(dialog > signedInCheck);
        Assert.True(loginFlow > dialog);
        Assert.DoesNotContain("AppendLog", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginAndLogoutButtons_AreInsideTheAccountCard()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var document = XDocument.Load(Path.Combine(
            projectRoot, "src", "TbotUltra.Desktop", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var accountCombo = document.Descendants(presentation + "ComboBox")
            .Single(element => (string?)element.Attribute(x + "Name") == "AccountComboBox");
        var loginButton = document.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "LoginButton");
        var logoutButton = document.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "LogoutButton");

        var accountCard = accountCombo.Ancestors(presentation + "Border").First();
        Assert.Same(accountCard, loginButton.Ancestors(presentation + "Border").First());
        Assert.Same(accountCard, logoutButton.Ancestors(presentation + "Border").First());
    }
}
