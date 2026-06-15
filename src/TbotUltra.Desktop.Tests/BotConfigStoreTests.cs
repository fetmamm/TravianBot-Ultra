using System.Text.Json;
using System.Text.Json.Nodes;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class BotConfigStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _configPath;
    private string _activeAccount = "alice";

    public BotConfigStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tbot-ultra-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        _configPath = Path.Combine(_root, "config", "bot.json");
    }

    [Fact]
    public void Load_OverlaysActiveAccountSettingsOnGlobalConfig()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                ["base_url"] = "https://example.com",
                [BotOptionPayloadKeys.HeroMinHpForAdventure] = 60,
            });
        WriteJson(
            AccountStoragePaths.AccountSettingsPath(_root, "alice"),
            new JsonObject
            {
                [BotOptionPayloadKeys.HeroMinHpForAdventure] = 30,
            });
        var store = CreateStore();

        var config = store.Load();

        Assert.Equal("Global", config["server_name"]!.GetValue<string>());
        Assert.Equal(30, config[BotOptionPayloadKeys.HeroMinHpForAdventure]!.GetValue<int>());
    }

    [Fact]
    public void Save_MovesAccountScopedValuesToActiveAccountSettings()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                ["base_url"] = "https://example.com",
                [BotOptionPayloadKeys.HeroMinHpForAdventure] = 60,
                ["server_flavor"] = "ss_travi",
                ["login_path"] = "/login.php",
                ["village_overview_path"] = "/dorf1.php",
            });
        var store = CreateStore();
        var config = store.Load();
        config[BotOptionPayloadKeys.HeroMinHpForAdventure] = 35;

        store.Save(config);

        var global = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.Equal("Global", global["server_name"]!.GetValue<string>());
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.HeroMinHpForAdventure));
        Assert.False(global.ContainsKey("server_flavor"));
        Assert.False(global.ContainsKey("login_path"));
        Assert.False(global.ContainsKey("village_overview_path"));

        var account = JsonNode.Parse(File.ReadAllText(AccountStoragePaths.AccountSettingsPath(_root, "alice")))!.AsObject();
        Assert.Equal(35, account[BotOptionPayloadKeys.HeroMinHpForAdventure]!.GetValue<int>());
    }

    [Fact]
    public void Save_PreservesHeroPrioritySeparatelyPerAccount()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                ["base_url"] = "https://example.com",
            });
        var store = CreateStore();
        var aliceConfig = store.Load();
        aliceConfig[BotOptionPayloadKeys.HeroStatPriority] =
            "resources,fighting_strength,offence_bonus,defence_bonus";
        store.Save(aliceConfig);

        _activeAccount = "bob";
        var bobConfig = store.Load();
        bobConfig[BotOptionPayloadKeys.HeroStatPriority] =
            "fighting_strength,resources,offence_bonus,defence_bonus";
        store.Save(bobConfig);

        _activeAccount = "alice";
        Assert.Equal(
            "resources,fighting_strength,offence_bonus,defence_bonus",
            store.Load()[BotOptionPayloadKeys.HeroStatPriority]!.GetValue<string>());
        _activeAccount = "bob";
        Assert.Equal(
            "fighting_strength,resources,offence_bonus,defence_bonus",
            store.Load()[BotOptionPayloadKeys.HeroStatPriority]!.GetValue<string>());
    }

    [Fact]
    public void Load_MigratesLegacyAccountSettingsWithoutLeakingToNewAccount()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                ["base_url"] = "https://example.com",
                [BotOptionPayloadKeys.HeroMinHpForAdventure] = 75,
                [BotOptionPayloadKeys.HeroResourceMaxUsePerResource] = 4000,
                [BotOptionPayloadKeys.CollectStepDelayMinMs] = 250,
                ["allow_silver_spending"] = true,
                [BotOptionPayloadKeys.ReinforcementsTroopRules] = new JsonArray(
                    new JsonObject { ["accountName"] = "alice", ["troopType"] = "Clubswinger" },
                    new JsonObject { ["accountName"] = "bob", ["troopType"] = "Spearman" }),
            });
        var store = CreateStore();

        var alice = store.Load();

        Assert.Equal(75, alice[BotOptionPayloadKeys.HeroMinHpForAdventure]!.GetValue<int>());
        Assert.Equal(4000, alice[BotOptionPayloadKeys.HeroResourceMaxUsePerResource]!.GetValue<int>());
        Assert.True(alice["allow_silver_spending"]!.GetValue<bool>());
        Assert.Single(alice[BotOptionPayloadKeys.ReinforcementsTroopRules]!.AsArray());

        var globalAfterAlice = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.False(globalAfterAlice.ContainsKey(BotOptionPayloadKeys.HeroMinHpForAdventure));
        Assert.False(globalAfterAlice.ContainsKey(BotOptionPayloadKeys.HeroResourceMaxUsePerResource));
        Assert.False(globalAfterAlice.ContainsKey(BotOptionPayloadKeys.CollectStepDelayMinMs));
        Assert.False(globalAfterAlice.ContainsKey("allow_silver_spending"));
        Assert.False(globalAfterAlice.ContainsKey(BotOptionPayloadKeys.ReinforcementsTroopRules));

        _activeAccount = "bob";
        var bob = store.Load();

        Assert.False(bob.ContainsKey(BotOptionPayloadKeys.HeroMinHpForAdventure));
        Assert.False(bob.ContainsKey(BotOptionPayloadKeys.HeroResourceMaxUsePerResource));
        Assert.False(bob.ContainsKey(BotOptionPayloadKeys.CollectStepDelayMinMs));
        Assert.False(bob.ContainsKey("allow_silver_spending"));
        Assert.Equal(
            "bob",
            bob[BotOptionPayloadKeys.ReinforcementsTroopRules]![0]!["accountName"]!.GetValue<string>());
    }

    [Fact]
    public void Save_MovesAllUserSettingsToActiveAccountSettings()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                ["base_url"] = "https://example.com",
            });
        var store = CreateStore();
        var config = store.Load();
        config[BotOptionPayloadKeys.HeroResourceMaxUseEnabled] = false;
        config[BotOptionPayloadKeys.HeroResourceMaxUsePerResource] = 3000;
        config[BotOptionPayloadKeys.CollectStepDelayMaxMs] = 900;
        config[BotOptionPayloadKeys.AllowGoldSpending] = true;
        config[BotOptionPayloadKeys.GoldLimit] = 500;
        config["allow_silver_spending"] = true;
        config["silver_limit"] = 250;
        config["loop_interval_seconds"] = 90;

        store.Save(config);

        var global = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.HeroResourceMaxUseEnabled));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.CollectStepDelayMaxMs));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.AllowGoldSpending));
        Assert.False(global.ContainsKey("allow_silver_spending"));
        Assert.False(global.ContainsKey("loop_interval_seconds"));

        var account = JsonNode.Parse(File.ReadAllText(AccountStoragePaths.AccountSettingsPath(_root, "alice")))!.AsObject();
        Assert.False(account[BotOptionPayloadKeys.HeroResourceMaxUseEnabled]!.GetValue<bool>());
        Assert.Equal(3000, account[BotOptionPayloadKeys.HeroResourceMaxUsePerResource]!.GetValue<int>());
        Assert.Equal(900, account[BotOptionPayloadKeys.CollectStepDelayMaxMs]!.GetValue<int>());
        Assert.Equal(500, account[BotOptionPayloadKeys.GoldLimit]!.GetValue<int>());
        Assert.Equal(250, account["silver_limit"]!.GetValue<int>());
        Assert.Equal(90, account["loop_interval_seconds"]!.GetValue<int>());
    }

    [Fact]
    public void Save_WithoutActiveAccountKeepsGlobalAccountScopedValues()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                [BotOptionPayloadKeys.ReinforcementsTroopRules] = new JsonArray(new JsonObject { ["accountName"] = "bob" }),
            });
        var store = new BotConfigStore(_configPath);
        var config = store.Load();

        store.Save(config);

        var global = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.True(global.ContainsKey(BotOptionPayloadKeys.ReinforcementsTroopRules));
    }

    [Fact]
    public void SaveGlobal_DoesNotWriteAccountScopedOverlayValues()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                ["base_url"] = "https://example.com",
                ["headless"] = false,
            });
        WriteJson(
            AccountStoragePaths.AccountSettingsPath(_root, "alice"),
            new JsonObject
            {
                [BotOptionPayloadKeys.HeroHpRegenPerDayPercent] = 70,
            });
        var store = CreateStore();
        var config = store.Load();
        config["headless"] = true;

        store.SaveGlobal(config);

        var global = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.True(global["headless"]!.GetValue<bool>());
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.HeroHpRegenPerDayPercent));

        var account = JsonNode.Parse(File.ReadAllText(AccountStoragePaths.AccountSettingsPath(_root, "alice")))!.AsObject();
        Assert.Equal(70, account[BotOptionPayloadKeys.HeroHpRegenPerDayPercent]!.GetValue<int>());
    }

    [Fact]
    public void Save_MovesPacingValuesToActiveAccountSettings()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                ["base_url"] = "https://example.com",
            });
        var store = CreateStore();
        var config = store.Load();
        config[BotOptionPayloadKeys.SessionPacingSleepMinutes] = 45;
        config[BotOptionPayloadKeys.SessionPacingVariationPercent] = 30;
        config[BotOptionPayloadKeys.SessionPacingAllowedHours] = new JsonArray(0, 1, 2);
        config[BotOptionPayloadKeys.SessionPacingDailyMaxHours] = 12;
        config[BotOptionPayloadKeys.SessionPacingRuntimeDate] = "2026-06-14";
        config[BotOptionPayloadKeys.SessionPacingRuntimeSeconds] = 3600;
        config[BotOptionPayloadKeys.ActionPacingTaskMinSeconds] = 2.5;

        store.Save(config);

        var global = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.SessionPacingSleepMinutes));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.SessionPacingVariationPercent));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.SessionPacingAllowedHours));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.SessionPacingDailyMaxHours));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.ActionPacingTaskMinSeconds));

        var account = JsonNode.Parse(File.ReadAllText(AccountStoragePaths.AccountSettingsPath(_root, "alice")))!.AsObject();
        Assert.Equal(45, account[BotOptionPayloadKeys.SessionPacingSleepMinutes]!.GetValue<int>());
        Assert.Equal(30, account[BotOptionPayloadKeys.SessionPacingVariationPercent]!.GetValue<int>());
        Assert.Equal(12, account[BotOptionPayloadKeys.SessionPacingDailyMaxHours]!.GetValue<int>());
        Assert.Equal("2026-06-14", account[BotOptionPayloadKeys.SessionPacingRuntimeDate]!.GetValue<string>());
        Assert.Equal(3600, account[BotOptionPayloadKeys.SessionPacingRuntimeSeconds]!.GetValue<int>());
        Assert.Equal(2.5, account[BotOptionPayloadKeys.ActionPacingTaskMinSeconds]!.GetValue<double>());
    }

    [Fact]
    public void ResetSettingsToDefaults_KeepsServerAndClearsSavedSettings()
    {
        WriteJson(
            _configPath,
            new JsonObject
            {
                ["server_name"] = "Global",
                ["base_url"] = "https://example.com",
                ["headless"] = true,
                ["silver_limit"] = 500,
                [BotOptionPayloadKeys.PostLoginAnalyzeHero] = true,
            });
        WriteJson(
            AccountStoragePaths.AccountSettingsPath(_root, "alice"),
            new JsonObject
            {
                [BotOptionPayloadKeys.HeroMinHpForAdventure] = 75,
            });
        var store = CreateStore();

        store.ResetSettingsToDefaults();

        var global = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.Equal("Global", global["server_name"]!.GetValue<string>());
        Assert.Equal("https://example.com", global["base_url"]!.GetValue<string>());
        Assert.False(global.ContainsKey("headless"));
        Assert.False(global.ContainsKey("silver_limit"));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.PostLoginAnalyzeHero));
        Assert.False(File.Exists(AccountStoragePaths.AccountSettingsPath(_root, "alice")));
    }

    private BotConfigStore CreateStore()
    {
        return new BotConfigStore(_configPath, _root, () => _activeAccount);
    }

    private static void WriteJson(string path, JsonObject config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
