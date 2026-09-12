namespace TbotUltra.Worker.Services;

using TbotUltra.Worker.Domain;

public sealed partial class TravianClient
{
    public async Task<string?> ReadCurrentPlayerNameAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var playerNameLocator = _page.Locator(Selectors.CurrentPlayerName).First;
        string? playerName;
        try
        {
            await playerNameLocator.WaitForAsync(new Microsoft.Playwright.LocatorWaitForOptions
            {
                State = Microsoft.Playwright.WaitForSelectorState.Visible,
                Timeout = _config.TimeoutMs,
            }).WaitAsync(cancellationToken);
            playerName = await playerNameLocator
                .TextContentAsync(new Microsoft.Playwright.LocatorTextContentOptions { Timeout = _config.TimeoutMs })
                .WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            Notify("[all-villages] current player name did not render in the active-village sidebar.");
            return null;
        }

        playerName = string.IsNullOrWhiteSpace(playerName)
            ? null
            : System.Text.RegularExpressions.Regex.Replace(playerName, @"\s+", " ").Trim();

        Notify(playerName is null
            ? "[all-villages] current player name was not available in the active-village sidebar."
            : $"[all-villages] current player identified as '{playerName}'.");
        return playerName;
    }

    public async Task<FarmTargetIdentity> ReadCurrentFarmTargetIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var identity = await _page.EvaluateAsync<FarmTargetIdentity>(
            """
            (selectors) => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const playerName = clean(document.querySelector(selectors.player)?.textContent);
              const allianceElement = document.querySelector(selectors.alliance);
              const allianceText = clean(allianceElement?.textContent);
              const alliance = !allianceText || /^no alliance$/i.test(allianceText)
                ? null
                : allianceText;
              return {
                isResolved: playerName.length > 0 && !!allianceElement,
                playerName: playerName || null,
                alliance
              };
            }
            """,
            new
            {
                player = Selectors.CurrentPlayerName,
                alliance = Selectors.CurrentAllianceName,
            }).WaitAsync(cancellationToken)
            ?? new FarmTargetIdentity(false, null, null);

        Notify(identity.IsResolved
            ? $"[farm-list] Target protection identity loaded for '{identity.PlayerName}' " +
              $"(alliance='{identity.Alliance ?? "None"}')."
            : "[farm-list] Target protection identity was unavailable in the global sidebar.");
        return identity;
    }
}
