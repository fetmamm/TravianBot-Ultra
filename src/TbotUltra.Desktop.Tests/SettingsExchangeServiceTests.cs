using System.Text.Json.Nodes;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class SettingsExchangeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-settings-exchange-{Guid.NewGuid():N}");
    private readonly SettingsExchangeService _service = new();

    [Fact]
    public void ExportThenImport_RoundTripsPortableValuesAndMetadata()
    {
        var path = TempPath();
        var exportedAt = new DateTimeOffset(2026, 8, 27, 8, 30, 0, TimeSpan.Zero);
        var draft = new JsonObject
        {
            [BotOptionPayloadKeys.AutomaticallyCheckLanguage] = false,
            [BotOptionPayloadKeys.SessionPacingEnabled] = true,
            [BotOptionPayloadKeys.SessionPacingDailyMaxHours] = 10,
            [BotOptionPayloadKeys.SessionPacingAllowedHours] = new JsonArray(2, 3, 4, 18),
            [BotOptionPayloadKeys.SmartSleepDeadlineGroups] = new JsonArray("construction", "hero"),
            [BotOptionPayloadKeys.SmartSleepWakeWhenConstructionQueueClears] = false,
            [BotOptionPayloadKeys.ActionPacingTaskMinSeconds] = 1.5,
            [BotOptionPayloadKeys.ActionPacingTaskMaxSeconds] = 4.5,
            [BotOptionPayloadKeys.ConstructionStorageUpgradeLevelsAhead] = 4,
            [BotOptionPayloadKeys.HeroHpRegenPerDayPercent] = 70,
            [BotOptionPayloadKeys.ShowFarmListLastSentTimer] = true,
            [BotOptionPayloadKeys.TroopTrainingFallbackCooldownSeconds] = 300,
            [BotOptionPayloadKeys.TownHallCelebrationCount] = 2,
        };

        _service.Export(path, draft, "3.1.4", exportedAt);
        var result = _service.Import(path, new JsonObject());

        Assert.Equal(SettingsExchangeService.CurrentSchemaVersion, result.SchemaVersion);
        Assert.Equal("3.1.4", result.AppVersion);
        Assert.Equal(exportedAt, result.ExportedAtUtc);
        Assert.Equal(draft.Count, result.ChangedKeys.Count);
        foreach (var (key, value) in draft)
        {
            Assert.True(JsonNode.DeepEquals(value, result.MergedConfig[key]), key);
        }
    }

    [Fact]
    public void Export_AlwaysExcludesIdentitySecretsVillagesRuntimeAndLocalOnlySettings()
    {
        var path = TempPath();
        var draft = new JsonObject
        {
            [BotOptionPayloadKeys.AutomaticallyCheckLanguage] = true,
            ["username"] = "person@example.com",
            ["password"] = "secret",
            ["server_name"] = "Server 1",
            ["base_url"] = "https://example.test",
            ["proxy_password"] = "proxy-secret",
            ["villages"] = new JsonArray("Home"),
            [BotOptionPayloadKeys.PostLoginAnalyzeBrewery] = true,
            [BotOptionPayloadKeys.VillageStatusSweepBreweryEnabled] = true,
            [BotOptionPayloadKeys.SessionPacingRuntimeSeconds] = 1234,
            [BotOptionPayloadKeys.ConstructionHumanizeStateVersion] = 8,
            [BotOptionPayloadKeys.DailyServerResetManualOverrideEnabled] = true,
            [BotOptionPayloadKeys.DailyServerResetManualHour] = 4,
            [BotOptionPayloadKeys.DontNotifyNewVersion] = true,
            [BotOptionPayloadKeys.DetailedBrowserLoggingEnabled] = true,
        };

        _service.Export(path, draft, "dev", DateTimeOffset.UtcNow);
        var settings = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path))!["settings"]);

        Assert.Single(settings);
        Assert.True(settings.ContainsKey(BotOptionPayloadKeys.AutomaticallyCheckLanguage));
        Assert.DoesNotContain("secret", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_OverlaysValidValuesAndPreservesMissingOrExcludedValues()
    {
        var current = new JsonObject
        {
            [BotOptionPayloadKeys.AutomaticallyCheckLanguage] = true,
            [BotOptionPayloadKeys.TurnOffVideoSound] = true,
            ["server_name"] = "Current server",
        };
        var result = _service.ImportJson(Document(new JsonObject
        {
            [BotOptionPayloadKeys.AutomaticallyCheckLanguage] = false,
        }), current);

        Assert.False(result.MergedConfig[BotOptionPayloadKeys.AutomaticallyCheckLanguage]!.GetValue<bool>());
        Assert.True(result.MergedConfig[BotOptionPayloadKeys.TurnOffVideoSound]!.GetValue<bool>());
        Assert.Equal("Current server", result.MergedConfig["server_name"]!.GetValue<string>());
        Assert.Equal([BotOptionPayloadKeys.AutomaticallyCheckLanguage], result.ChangedKeys);
    }

    [Fact]
    public void Import_SkipsUnknownInvalidAndBrokenRangesButKeepsValidValues()
    {
        var current = new JsonObject
        {
            [BotOptionPayloadKeys.SessionPacingRunMinMinutes] = 10,
            [BotOptionPayloadKeys.SessionPacingRunMaxMinutes] = 20,
        };
        var result = _service.ImportJson(Document(new JsonObject
        {
            [BotOptionPayloadKeys.AutomaticallyCheckLanguage] = false,
            [BotOptionPayloadKeys.GoldLimit] = -1,
            [BotOptionPayloadKeys.SessionPacingRunMinMinutes] = 30,
            ["future_setting"] = true,
        }), current);

        Assert.False(result.MergedConfig[BotOptionPayloadKeys.AutomaticallyCheckLanguage]!.GetValue<bool>());
        Assert.Equal(10, result.MergedConfig[BotOptionPayloadKeys.SessionPacingRunMinMinutes]!.GetValue<int>());
        Assert.Equal(3, result.SkippedSettings.Count);
        Assert.Contains(result.SkippedSettings, item => item.Key == BotOptionPayloadKeys.GoldLimit);
        Assert.Contains(result.SkippedSettings, item => item.Key == BotOptionPayloadKeys.SessionPacingRunMinMinutes);
        Assert.Contains(result.SkippedSettings, item => item.Key == "future_setting");
    }

    [Fact]
    public void Import_ReportsCombinedSpendingAndRuntimeRisksOnlyWhenActivated()
    {
        var current = new JsonObject
        {
            [BotOptionPayloadKeys.AllowGoldSpending] = false,
            ["allow_silver_spending"] = false,
            [BotOptionPayloadKeys.SessionPacingDailyMaxHours] = 8,
        };
        var result = _service.ImportJson(Document(new JsonObject
        {
            [BotOptionPayloadKeys.AllowGoldSpending] = true,
            ["allow_silver_spending"] = true,
            [BotOptionPayloadKeys.SessionPacingDailyMaxHours] = 0,
        }), current);

        Assert.True(result.EnablesGoldSpending);
        Assert.True(result.EnablesSilverSpending);
        Assert.True(result.EnablesRiskyDailyRuntime);
    }

    [Fact]
    public void Import_WithNoValidValues_DoesNotChangeCurrentDraft()
    {
        var current = new JsonObject
        {
            [BotOptionPayloadKeys.AutomaticallyCheckLanguage] = true,
        };

        var result = _service.ImportJson(Document(new JsonObject
        {
            [BotOptionPayloadKeys.GoldLimit] = -5,
            ["unknown"] = true,
        }), current);

        Assert.Empty(result.ChangedKeys);
        Assert.True(JsonNode.DeepEquals(current, result.MergedConfig));
        Assert.Equal(2, result.SkippedSettings.Count);
    }

    [Fact]
    public void Import_RejectsFilesLargerThanOneMegabyte()
    {
        var path = TempPath();
        File.WriteAllText(path, new string(' ', (1024 * 1024) + 1));

        var error = Assert.Throws<InvalidDataException>(() => _service.Import(path, new JsonObject()));

        Assert.Contains("1 MB", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":99,\"appVersion\":\"99\",\"exportedAtUtc\":\"2026-08-27T00:00:00Z\",\"settings\":{}}")]
    [InlineData("{not-json")]
    public void Import_RejectsBrokenOrNewerDocuments(string json)
    {
        Assert.Throws<InvalidDataException>(() => _service.ImportJson(json, new JsonObject()));
    }

    private string TempPath()
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, $"settings-{Guid.NewGuid():N}{SettingsExchangeService.FileExtension}");
    }

    private static string Document(JsonObject settings) => new JsonObject
    {
        ["schemaVersion"] = 1,
        ["appVersion"] = "test",
        ["exportedAtUtc"] = "2026-08-27T00:00:00Z",
        ["settings"] = settings,
    }.ToJsonString();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
