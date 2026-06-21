using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static VillageSettingsStore.HeroResourceSettings BuildHeroResourceDefaults()
    {
        return new VillageSettingsStore.HeroResourceSettings(
            IsEnabled: true,
            UseConstruction: true,
            UseSmithy: false,
            UseBrewery: false,
            UseTownHall: false,
            MaxUseEnabled: true,
            MaxUsePerResource: 5000);
    }

    private BotOptions ApplyHeroResourceSettingsForVillage(
        BotOptions options,
        string? villageKey,
        string? villageName)
    {
        if (string.IsNullOrWhiteSpace(villageKey) && string.IsNullOrWhiteSpace(villageName))
        {
            return options;
        }

        var settings = _villageSettingsStore.GetHeroResourceSettings(
            villageKey,
            villageName,
            BuildHeroResourceDefaults());

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroResourceTransferEnabled] = settings.IsEnabled ? "true" : "false",
            [BotOptionPayloadKeys.HeroResourceMaxUseEnabled] = "true",
            [BotOptionPayloadKeys.HeroResourceMaxUsePerResource] = settings.MaxUsePerResource.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [BotOptionPayloadKeys.HeroResourceUseConstruction] = settings.UseConstruction ? "true" : "false",
            [BotOptionPayloadKeys.HeroResourceUseSmithy] = settings.UseSmithy ? "true" : "false",
            [BotOptionPayloadKeys.HeroResourceUseBrewery] = settings.UseBrewery ? "true" : "false",
            [BotOptionPayloadKeys.HeroResourceUseTownHall] = settings.UseTownHall ? "true" : "false",
        };

        return BotOptionsPayloadApplier.Apply(options, payload);
    }

    private BotOptions ApplyHeroResourceSettingsForQueueItem(BotOptions options, QueueItem item)
    {
        return ApplyHeroResourceSettingsForVillage(
            options,
            GetQueueItemVillageKey(item),
            GetQueueItemVillageName(item));
    }

    private void OpenHeroResourceSettingsFromVillageSettings(IReadOnlyList<VillageSettingsRow> villageSettingsRows)
    {
        OpenHeroResourceSettingsWindow(villageSettingsRows);
    }

    internal void OpenHeroResourceSettingsFromHeroPanel()
    {
        OpenHeroResourceSettingsWindow(null);
    }

    private void OpenHeroResourceSettingsWindow(IReadOnlyList<VillageSettingsRow>? villageSettingsRows)
    {
        if (string.IsNullOrWhiteSpace(_accountStore.ActiveAccountName()))
        {
            AppendLog("Hero resource settings: no active account.");
            return;
        }

        var villages = GetAllVillageKeyInfos();
        if (villages.Count == 0)
        {
            AppendLog("Hero resource settings: no villages loaded.");
            return;
        }

        var defaults = BuildHeroResourceDefaults();
        var rows = villages
            .Select(village =>
            {
                var settings = _villageSettingsStore.GetHeroResourceSettings(village, defaults);
                var row = FindVillageSettingsRow(villageSettingsRows, village);
                if (row is not null)
                {
                    settings = settings with { IsEnabled = row.HeroResourcesEnabled };
                }

                return new HeroResourceOverviewRow(village.Key, village.Name, settings);
            })
            .ToList();

        var window = new HeroResourceOverviewWindow(rows) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        foreach (var result in window.Results)
        {
            var village = villages.FirstOrDefault(v => string.Equals(v.Key, result.VillageKey, StringComparison.OrdinalIgnoreCase))
                ?? new VillageSettingsStore.VillageKeyInfo(result.VillageKey, result.VillageName, null, null, false);
            _villageSettingsStore.SetHeroResourceSettings(village, result.Settings);
            UpdateVillageSettingsHeroResourcesRow(
                villageSettingsRows,
                result.VillageKey,
                result.VillageName,
                result.Settings.IsEnabled);
        }

        ApplyHeroResourceTransferConfigToUi();

        if (IsContinuousLoopRunning())
        {
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
        }

        AppendLog($"Saved Hero resource settings for {window.Results.Count} village(s).");
    }

    private static void UpdateVillageSettingsHeroResourcesRow(
        IReadOnlyList<VillageSettingsRow>? villageSettingsRows,
        string villageKey,
        string villageName,
        bool enabled)
    {
        var row = villageSettingsRows?.FirstOrDefault(candidate =>
            candidate.KeyInfo is not null
            && (string.Equals(candidate.KeyInfo.Key, villageKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.KeyInfo.Name, villageName, StringComparison.OrdinalIgnoreCase)));
        if (row is not null)
        {
            row.HeroResourcesEnabled = enabled;
        }
    }
}
