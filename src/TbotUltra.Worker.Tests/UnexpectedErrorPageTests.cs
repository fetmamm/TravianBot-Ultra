using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class UnexpectedErrorPageTests
{
    [Fact]
    public void BringMeBackSelector_IsScopedToCapturedUnexpectedErrorPage()
    {
        const string capturedErrorPage = """
            <main id="errorPage" class="unexpectedError">
              <h1>500 Error</h1>
              <button class="gold decorative buttonSecondary withText" type="button"><div><span>Bring me back</span></div></button>
            </main>
            """;

        Assert.Contains("<h1>500 Error</h1>", capturedErrorPage);
        Assert.Contains("Bring me back", capturedErrorPage);
        Assert.Equal("main#errorPage.unexpectedError", TravianClient.UnexpectedErrorPageSelector);
        Assert.Equal(
            "main#errorPage.unexpectedError button.gold.decorative.buttonSecondary.withText",
            TravianClient.UnexpectedErrorBringMeBackButtonSelector);
    }

    [Fact]
    public void LoginSelector_IsScopedToCapturedLoggedOutStartPage()
    {
        const string capturedStartPage = """
            <header>
              <div class="headerContainerEnd">
                <button class="gold buttonSecondary login withText" type="button"><div><span>Login</span></div></button>
              </div>
            </header>
            """;

        Assert.Contains("<span>Login</span>", capturedStartPage);
        Assert.Equal(
            "header .headerContainerEnd button.gold.buttonSecondary.login.withText",
            TravianClient.StartPageLoginButtonSelector);
    }

    [Fact]
    public void GoToLobbySelector_IsScopedToCapturedLoggedInStartPage()
    {
        const string capturedStartPage = """
            <button type="button" class="playNowCTAButton">
              <div class="inner"><svg><text>Go to lobby</text></svg></div>
            </button>
            """;

        Assert.Contains("Go to lobby", capturedStartPage);
        Assert.Equal("button.playNowCTAButton", TravianClient.StartPageGoToLobbyButtonSelector);
    }
}
