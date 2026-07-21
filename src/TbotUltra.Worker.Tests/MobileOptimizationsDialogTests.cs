using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class MobileOptimizationsDialogTests
{
    [Fact]
    public void Selectors_AreScopedToCapturedMobileOptimizationsDialog()
    {
        const string capturedDialog = """
            <div id="mobileOptimizationsDialog" class="modal">
              <label class="switch"><input type="checkbox" name="mobileOptimizations"></label>
              <label class="switch"><input type="checkbox" checked="" name="mobileOptimizations"></label>
              <div class="action"><button class="framed green withText" type="button"><div>Play now</div></button></div>
            </div>
            """;

        Assert.Contains("id=\"mobileOptimizationsDialog\"", capturedDialog);
        Assert.Contains("name=\"mobileOptimizations\"", capturedDialog);
        Assert.Contains("<div>Play now</div>", capturedDialog);

        Assert.Equal("#mobileOptimizationsDialog", TravianClient.MobileOptimizationsDialogSelector);
        Assert.Equal(
            "#mobileOptimizationsDialog label.switch:has(input[name='mobileOptimizations'])",
            TravianClient.MobileOptimizationsSwitchSelector);
        Assert.Equal(
            "#mobileOptimizationsDialog .action button.framed.green.withText",
            TravianClient.MobileOptimizationsPlayNowButtonSelector);
    }
}
