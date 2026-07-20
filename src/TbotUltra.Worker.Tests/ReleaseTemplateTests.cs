using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Release;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ReleaseTemplateTests
{
    [Fact]
    public void BotConfig_UsesKnownCurrentKeysAndLoadsExpectedValues()
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "ReleaseTemplate", "bot.json");
        var json = JsonNode.Parse(File.ReadAllText(templatePath))?.AsObject()
            ?? throw new InvalidOperationException("Release bot.json must contain a JSON object.");
        var knownKeys = typeof(BotOptions)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknownKeys = json.Select(pair => pair.Key)
            .Where(key => !knownKeys.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.True(unknownKeys.Length == 0, $"Release bot.json contains unknown or retired keys: {string.Join(", ", unknownKeys)}");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(templatePath, optional: false)
            .Build();
        var options = BotOptionsFactory.FromConfiguration(configuration);

        Assert.Equal(30, options.ContinuousFarmDispatchDelayMinMinutes);
        Assert.Equal(90, options.ContinuousFarmDispatchDelayMaxMinutes);
        Assert.Equal(FarmingDefaults.SendModeListPerList, options.ContinuousFarmSendMode);
        Assert.Equal("smart", options.ResourceBuildStrategy);
        Assert.Equal("resource_percent", options.TroopTrainingBarracksRunMode);
        Assert.Equal("resource_percent", options.TroopTrainingStableRunMode);
        Assert.Equal("resource_percent", options.TroopTrainingWorkshopRunMode);
        Assert.Equal(300, options.TroopTrainingFallbackCooldownSeconds);
    }

    [Fact]
    public void ReleaseSmokeScript_WaitsForCurrentStartupContract()
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "ReleaseScripts", "Test-ReleaseBundle.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains(ReleaseSmokeContract.ReadyLogMarker, script, StringComparison.Ordinal);
        Assert.Contains(ReleaseSmokeContract.CatalogFailureLogMarker, script, StringComparison.Ordinal);
        Assert.DoesNotContain("Chromium warmup completed", script, StringComparison.OrdinalIgnoreCase);
    }
}
