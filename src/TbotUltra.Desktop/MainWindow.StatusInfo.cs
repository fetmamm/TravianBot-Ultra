using System.Text.Json;
using System.Windows.Threading;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

// Account status indicators: Gold Club / Travian Plus labels, parsing those (and
// tribe / ui-sync payloads) out of worker log lines, and reading stored Gold Club
// analysis. Extracted verbatim from MainWindow.xaml.cs to keep that file focused;
// same class, so this is a pure relocation with no behavior change.
public partial class MainWindow
{
    private void UpdateGoldClubInfo(bool? enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateGoldClubInfo(enabled));
            return;
        }

        if (enabled == true)
        {
            GoldClubInfoTextBlock.Text = "Yes";
            GoldClubInfoTextBlock.Foreground = System.Windows.Media.Brushes.Green;
            if (string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoGoldClub, StringComparison.OrdinalIgnoreCase))
            {
                ClearFarmingBlockedState();
            }
        }
        else if (enabled == false)
        {
            GoldClubInfoTextBlock.Text = "No";
            GoldClubInfoTextBlock.Foreground = System.Windows.Media.Brushes.Red;
        }
        else
        {
            GoldClubInfoTextBlock.Text = "-";
            GoldClubInfoTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
        }
        UpdateAccountInfoLabel(_accountStore.ActiveAccountName());
    }

    private void ApplyFarmingAvailabilityFromGoldClubStatus(bool? enabled)
    {
        if (enabled == true)
        {
            if (string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoGoldClub, StringComparison.OrdinalIgnoreCase))
            {
                ClearFarmingBlockedState();
            }

            return;
        }

        if (enabled == false
            && !string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoGoldClub, StringComparison.OrdinalIgnoreCase))
        {
            SetFarmingBlockedState(FarmingBlockedReasonNoGoldClub, "No goldclub");
        }
    }

    private void UpdatePlusInfo(bool? active)
    {
        _travianPlusActive = active;
        if (active == true)
        {
            PlusInfoTextBlock.Text = "Yes";
            PlusInfoTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
        else if (active == false)
        {
            PlusInfoTextBlock.Text = "No";
            PlusInfoTextBlock.Foreground = System.Windows.Media.Brushes.Red;
        }
        else
        {
            PlusInfoTextBlock.Text = "-";
            PlusInfoTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex PlusStatusRegex =
        new(@"\[plus\]\s*active=(True|False)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex GoldClubStatusRegex =
        new(@"\[goldclub\]\s*active=(True|False)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex UiSyncRegex =
        new(@"\[ui-sync\]\s*(\{.*\})",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // [herohome] away=<bool> dead=<bool> reviving=<bool> name=<village name to end of line> — emitted by the
    // worker's quick dorf1 hero-widget read so the dashboard hero icon updates without a full attributes
    // navigation.
    private static readonly System.Text.RegularExpressions.Regex HeroHomeRegex =
        new(@"\[herohome\]\s*away=(true|false)\s+dead=(true|false)\s+reviving=(true|false)\s+name=(.+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void TryApplyPlusStatusFromLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        var plusMatch = PlusStatusRegex.Match(line);
        if (plusMatch.Success)
        {
            var active = string.Equals(plusMatch.Groups[1].Value, "True", StringComparison.OrdinalIgnoreCase);
            UpdatePlusInfo(active);
        }

        var goldMatch = GoldClubStatusRegex.Match(line);
        if (goldMatch.Success)
        {
            var active = string.Equals(goldMatch.Groups[1].Value, "True", StringComparison.OrdinalIgnoreCase);
            UpdateGoldClubInfo(active ? true : false);
        }

        var uiSyncMatch = UiSyncRegex.Match(line);
        if (uiSyncMatch.Success)
        {
            TryApplyUiSyncPayload(uiSyncMatch.Groups[1].Value);
        }

        var heroHomeMatch = HeroHomeRegex.Match(line);
        if (heroHomeMatch.Success)
        {
            var away = string.Equals(heroHomeMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            var dead = string.Equals(heroHomeMatch.Groups[2].Value, "true", StringComparison.OrdinalIgnoreCase);
            var reviving = string.Equals(heroHomeMatch.Groups[3].Value, "true", StringComparison.OrdinalIgnoreCase);
            var name = heroHomeMatch.Groups[4].Value.Trim();
            SetHeroState(name, away, dead, reviving);
        }
    }

    // The top-bar tribe follows the village selected in the UI. Unknown must clear the previous value so
    // selecting an unscanned village never leaves another village's tribe visible.
    private void SetTribeText(string? tribe)
    {
        var trimmed = tribe?.Trim();
        var meaningful = !string.IsNullOrWhiteSpace(trimmed)
            && !string.Equals(trimmed, "Unknown", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(trimmed, "-", StringComparison.Ordinal);
        var value = meaningful ? trimmed! : "-";

        if (!string.Equals(TribeInfoTextBlock.Text?.Trim(), value, StringComparison.OrdinalIgnoreCase))
        {
            TribeInfoTextBlock.Text = value;
        }
    }

    private void ApplyVillageTribeToUiIfSelected(VillageStatus status)
    {
        if (!IsStatusForSelectedVillage(status))
        {
            return;
        }

        SetTribeText(status.Tribe);
        ApplyTroopTrainingTribeState(status.Tribe);
    }

    private void TryApplyUiSyncPayload(string rawJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<UiSyncPayload>(rawJson);
            if (payload is null)
            {
                return;
            }

            var goldText = payload.Gold?.ToString() ?? "-";
            var silverText = payload.Silver?.ToString() ?? "-";
            SetGoldSilverStatusText(ServerResourcesTextBlock, SilverInfoTextBlock, goldText, silverText);

            if (payload.Villages is { Count: > 0 })
            {
                VillagesInfoTextBlock.Text = $"Villages: {payload.Villages.Count}";
                SyncDashboardVillageUiFromPayloadVillages(payload.Villages, payload.ActiveVillage);
            }
        }
        catch
        {
            // Ignore malformed sync lines.
        }
    }

    private void UpdateGoldClubInfoFromStoredAnalysis()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null)
            {
                UpdateGoldClubInfo(analysis.GoldClubEnabled);
                return;
            }
        }
        catch
        {
            // Ignore temporary read failures and fall back to unknown.
        }

        UpdateGoldClubInfo(null);
    }

    private bool? TryGetStoredGoldClubEnabled(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return null;
        }

        try
        {
            if (_accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl()) && analysis is not null)
            {
                return analysis.GoldClubEnabled;
            }
        }
        catch
        {
            // Ignore temporary read errors for account info label.
        }

        return null;
    }
}
