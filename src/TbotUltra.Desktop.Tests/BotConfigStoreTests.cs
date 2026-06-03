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
        config[BotOptionPayloadKeys.ActionPacingTaskMinSeconds] = 2.5;

        store.Save(config);

        var global = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.SessionPacingSleepMinutes));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.SessionPacingVariationPercent));
        Assert.False(global.ContainsKey(BotOptionPayloadKeys.ActionPacingTaskMinSeconds));

        var account = JsonNode.Parse(File.ReadAllText(AccountStoragePaths.AccountSettingsPath(_root, "alice")))!.AsObject();
        Assert.Equal(45, account[BotOptionPayloadKeys.SessionPacingSleepMinutes]!.GetValue<int>());
        Assert.Equal(30, account[BotOptionPayloadKeys.SessionPacingVariationPercent]!.GetValue<int>());
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
