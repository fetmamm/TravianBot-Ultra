using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private BotOptions ApplyConstructFasterSettingsForVillage(
        BotOptions options,
        string? villageKey,
        string? villageName)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ApplyConstructFasterSettingsToPayload(payload, options, villageKey, villageName);
        return BotOptionsPayloadApplier.Apply(options, payload);
    }

    private void ApplyConstructFasterSettingsToPayload(Dictionary<string, string> payload, string? villageKey, string? villageName = null)
    {
        ApplyConstructFasterSettingsToPayload(payload, LoadBotOptions(), villageKey, villageName);
    }

    private void ApplyConstructFasterSettingsToPayload(
        Dictionary<string, string> payload,
        BotOptions options,
        string? villageKey,
        string? villageName)
    {
        var villageEnabled = !string.IsNullOrWhiteSpace(villageKey)
            && _villageSettingsStore.IsConstructFasterEnabledByKey(villageKey, defaultIfUnknown: false);
        if (!villageEnabled && !string.IsNullOrWhiteSpace(villageName))
        {
            villageEnabled = _villageSettingsStore.IsConstructFasterEnabledByKey($"name:{villageName.Trim()}", defaultIfUnknown: false);
        }

        var effectiveEnabled = options.ConstructFasterEnabled && villageEnabled;

        payload[BotOptionPayloadKeys.ConstructFasterEnabled] = effectiveEnabled ? "true" : "false";
        payload[BotOptionPayloadKeys.ConstructFasterMinBuildMinutes] =
            Math.Max(0, options.ConstructFasterMinBuildMinutes).ToString(CultureInfo.InvariantCulture);
        payload[BotOptionPayloadKeys.ConstructFasterRandomEnabled] =
            options.ConstructFasterRandomEnabled ? "true" : "false";
        payload[BotOptionPayloadKeys.ConstructFasterRandomChancePercent] =
            Math.Clamp(options.ConstructFasterRandomChancePercent, 0, 100).ToString(CultureInfo.InvariantCulture);
    }

    private void OpenConstructFasterSettingsWindow()
    {
        if (string.IsNullOrWhiteSpace(_accountStore.ActiveAccountName()))
        {
            AppendLog("Construct-faster settings: no active account.");
            return;
        }

        var villages = GetAllVillageKeyInfos();
        if (villages.Count == 0)
        {
            AppendLog("Construct-faster settings: no villages loaded.");
            return;
        }

        var options = LoadBotOptions();
        var rows = villages
            .Select(village => new ConstructFasterSettingsRow(
                village,
                _villageSettingsStore.GetConstructFaster(village)))
            .ToList();

        var window = new ConstructFasterSettingsWindow(
            rows,
            options.ConstructFasterMinBuildMinutes,
            options.ConstructFasterRandomEnabled,
            options.ConstructFasterRandomChancePercent)
        {
            Owner = this,
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        foreach (var result in window.Results)
        {
            _villageSettingsStore.SetConstructFaster(result.Village, result.IsEnabled);
        }

        if (_botConfigStore is not null)
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.ConstructFasterMinBuildMinutes] = window.MinimumBuildMinutes;
            config[BotOptionPayloadKeys.ConstructFasterRandomEnabled] = window.RandomEnabled;
            config[BotOptionPayloadKeys.ConstructFasterRandomChancePercent] = window.RandomChancePercent;
            _botConfigStore.Save(config);
        }

        ApplyConstructFasterConfigToUi(LoadBotOptions());

        if (IsContinuousLoopRunning())
        {
            Interlocked.Exchange(ref _continuousLoopWakeRequested, 1);
        }

        AppendLog($"Saved construct-faster settings for {window.Results.Count} village(s).");
    }
}
