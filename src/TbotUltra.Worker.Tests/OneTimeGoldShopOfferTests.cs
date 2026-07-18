using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class OneTimeGoldShopOfferTests
{
    [Fact]
    public void Selectors_AreScopedToCapturedOfficialOneTimeOfferDialog()
    {
        const string capturedDialog = """
            <div class="dialogOverlay enabled dialogVisible">
              <div class="dialogWrapper dialogV2" data-context="oneTimeOfferAnnouncement">
                <div class="dialog oneTimeOfferAnnouncement">
                  <div class="dialogCancelButton buttonFramed green withIcon rectangle cancel"></div>
                </div>
              </div>
            </div>
            """;

        Assert.Contains("dialogOverlay enabled dialogVisible", capturedDialog);
        Assert.Contains("data-context=\"oneTimeOfferAnnouncement\"", capturedDialog);
        Assert.Contains("dialog oneTimeOfferAnnouncement", capturedDialog);
        Assert.Contains("dialogCancelButton buttonFramed green withIcon rectangle cancel", capturedDialog);

        Assert.Contains(".dialogOverlay.enabled.dialogVisible", TravianClient.OneTimeGoldShopOfferDialogSelector);
        Assert.Contains("[data-context='oneTimeOfferAnnouncement']", TravianClient.OneTimeGoldShopOfferDialogSelector);
        Assert.EndsWith(".dialog.oneTimeOfferAnnouncement", TravianClient.OneTimeGoldShopOfferDialogSelector);
        Assert.Equal(
            TravianClient.OneTimeGoldShopOfferDialogSelector + " .dialogCancelButton.cancel",
            TravianClient.OneTimeGoldShopOfferCloseButtonSelector);
    }
}
