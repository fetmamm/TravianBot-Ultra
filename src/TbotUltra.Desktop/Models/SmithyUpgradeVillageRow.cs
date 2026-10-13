using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.Models;

public sealed record SmithyUpgradeVillageRow(
    VillageSettingsStore.VillageKeyInfo Village,
    int SelectedTroopCount,
    bool IsEnabled)
{
    public string VillageName => Village.Name;

    public string SelectionSummary => SelectedTroopCount switch
    {
        0 => "No troops selected",
        1 => "1 troop selected",
        _ => $"{SelectedTroopCount} troops selected",
    };
}
