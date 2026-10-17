using System.Globalization;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void ExpansionTrainSettlersRequested()
    {
        EnqueueQuickTask("train_settlers", "Train settlers");
        TroopsHubPanelControl.ExpansionPanel.ShowStatus("Settler training queued for the selected village.");
    }

    private void ExpansionFoundVillageRequested(int x, int y, int? tribeId)
    {
        var target = $"xy:{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}";
        if (tribeId is int tribe)
        {
            target += $";tribe:{tribe.ToString(CultureInfo.InvariantCulture)}";
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetBuildingSlotOrName] = target,
        };
        EnqueueQuickTask("found_village", $"Found new village ({x}|{y})", payload);
        TroopsHubPanelControl.ExpansionPanel.ShowStatus($"Founding ({x}|{y}) queued for the selected village.");
    }
}
