using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    internal const string OneTimeGoldShopOfferDialogSelector =
        ".dialogOverlay.enabled.dialogVisible "
        + ".dialogWrapper[data-context='oneTimeOfferAnnouncement'] "
        + ".dialog.oneTimeOfferAnnouncement";
    internal const string OneTimeGoldShopOfferCloseButtonSelector =
        OneTimeGoldShopOfferDialogSelector + " .dialogCancelButton.cancel";

    private async Task<bool> TryDismissOneTimeGoldShopOfferAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dialog = _page.Locator(OneTimeGoldShopOfferDialogSelector).First;
            if (await dialog.CountAsync() == 0 || !await dialog.IsVisibleAsync())
            {
                return false;
            }

            var clicked = await TryClickFirstVisibleEnabledAsync(
                OneTimeGoldShopOfferCloseButtonSelector,
                cancellationToken,
                reason: "dismiss one-time Gold Shop offer",
                timeoutMs: Math.Min(_config.TimeoutMs, 5_000));
            if (!clicked)
            {
                Notify("[gold-shop-offer] one-time offer detected, but its close button is unavailable.");
                return false;
            }

            try
            {
                await dialog.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Hidden,
                    Timeout = 3_000,
                }).WaitAsync(cancellationToken);
            }
            catch (TimeoutException)
            {
                Notify("[gold-shop-offer] close was clicked, but the dialog remained visible.");
                return false;
            }
            catch (PlaywrightException)
            {
                // The dialog may be detached immediately after the click, which also means it closed.
            }

            Notify("[gold-shop-offer] dismissed one-time Gold Shop offer.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlaywrightException ex)
        {
            Notify($"[gold-shop-offer] dismissal skipped: {ex.Message}");
            return false;
        }
    }
}
