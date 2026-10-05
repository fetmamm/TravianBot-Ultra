using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

/// <summary>
/// Guards against the "persisted but never loaded" bug class: a setting that the Desktop
/// writes to bot.json (via <see cref="BotConfigStore.AccountScopedKeyValues"/>) but that
/// <see cref="BotOptionsFactory.FromConfiguration"/> forgets to read, so it silently falls
/// back to its default every time the config is reloaded. NpcTradeBuildTimeLimit* and
/// BreweryAutoCelebrationEnabled were exactly this until 2026-07-25.
///
/// The test runs FromConfiguration against an <see cref="AccessRecordingConfiguration"/> that
/// records every key the factory touches, then asserts that every persisted key which maps to
/// a real BotOptions property was read. It measures access, not value, so per-field clamping
/// and normalization don't matter.
/// </summary>
public sealed class BotOptionsFromConfigurationCoverageTests
{
    // Persisted keys that FromConfiguration honours through an indirect path the access spy
    // cannot see (i.e. the key string is never handed to the IConfiguration). Keep this list
    // tiny and justified — anything else that surfaces is a real bug, not an exception.
    private static readonly HashSet<string> KnownIndirectlyReadKeys = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    [Fact]
    public void FromConfiguration_reads_every_persisted_BotOptions_setting()
    {
        var optionKeys = typeof(BotOptions)
            .GetProperties()
            .Select(property => property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only persisted keys that actually correspond to a BotOptions property are in scope:
        // BotConfigStore also persists Desktop-only UI/session state that FromConfiguration
        // legitimately never loads into BotOptions.
        var persistedOptionKeys = BotConfigStore.AccountScopedKeyValues
            .Where(optionKeys.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Guards against the test passing vacuously if the reflection/intersection ever breaks.
        Assert.NotEmpty(persistedOptionKeys);

        var spy = new AccessRecordingConfiguration();
        _ = BotOptionsFactory.FromConfiguration(spy);

        var missing = persistedOptionKeys
            .Where(key => !spy.AccessedKeys.Contains(key) && !KnownIndirectlyReadKeys.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "BotOptionsFactory.FromConfiguration never reads these persisted settings, so they " +
            "silently reset to their default after bot.json is reloaded. Add a read for each in " +
            "FromConfiguration (or, if it is genuinely runtime-only, remove it from " +
            "BotConfigStore.AccountScopedKeyValues): " + string.Join(", ", missing));
    }

    [Fact]
    public void LossMoveSettings_AreAccountScoped()
    {
        Assert.Contains(BotOptionPayloadKeys.ContinuousFarmMoveRedLosses, BotConfigStore.AccountScopedKeyValues);
        Assert.Contains(BotOptionPayloadKeys.ContinuousFarmMoveYellowLosses, BotConfigStore.AccountScopedKeyValues);
        Assert.Contains(BotOptionPayloadKeys.ContinuousFarmRedLossDestinationListId, BotConfigStore.AccountScopedKeyValues);
        Assert.Contains(BotOptionPayloadKeys.ContinuousFarmYellowLossDestinationListId, BotConfigStore.AccountScopedKeyValues);
    }

    [Fact]
    public void FarmListOnlyLossReportsSetting_IsAccountScoped()
    {
        Assert.Contains(
            BotOptionPayloadKeys.FarmListOnlyCreateReportsWithLosses,
            BotConfigStore.AccountScopedKeyValues);
    }

    [Fact]
    public void FromConfiguration_defaults_village_scan_to_enabled_every_40_to_60_minutes()
    {
        var options = BotOptionsFactory.FromConfiguration(new AccessRecordingConfiguration());

        Assert.True(options.VillageStatusSweepEnabled);
        Assert.Equal(40, options.VillageStatusSweepRoundMinMinutes);
        Assert.Equal(60, options.VillageStatusSweepRoundMaxMinutes);
    }

    [Fact]
    public void FromConfiguration_defaults_keep_alive_to_enabled_every_10_to_30_minutes()
    {
        var options = BotOptionsFactory.FromConfiguration(new AccessRecordingConfiguration());

        Assert.True(options.ContinuousKeepAliveEnabled);
        Assert.Equal(10, options.ContinuousKeepAliveMinMinutes);
        Assert.Equal(30, options.ContinuousKeepAliveMaxMinutes);
        Assert.False(options.ParkOnDorf2WhileIdle);
    }

    /// <summary>
    /// An empty <see cref="IConfiguration"/> that records every key requested through the
    /// indexer or <see cref="GetSection"/>. All lookups return "absent", so FromConfiguration
    /// falls back to defaults while we observe which keys it consulted.
    /// </summary>
    private sealed class AccessRecordingConfiguration : IConfiguration
    {
        public HashSet<string> AccessedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? this[string key]
        {
            get
            {
                AccessedKeys.Add(key);
                return null;
            }
            set { }
        }

        public IConfigurationSection GetSection(string key)
        {
            AccessedKeys.Add(key);
            return new EmptySection(key);
        }

        public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();

        public IChangeToken GetReloadToken() => NullChangeToken.Instance;
    }

    private sealed class EmptySection : IConfigurationSection
    {
        public EmptySection(string key)
        {
            Key = key;
            Path = key;
        }

        public string? this[string key]
        {
            get => null;
            set { }
        }

        public string Key { get; }

        public string Path { get; }

        public string? Value
        {
            get => null;
            set { }
        }

        public IConfigurationSection GetSection(string key) => new EmptySection(key);

        public IEnumerable<IConfigurationSection> GetChildren() => Array.Empty<IConfigurationSection>();

        public IChangeToken GetReloadToken() => NullChangeToken.Instance;
    }

    private sealed class NullChangeToken : IChangeToken
    {
        public static readonly NullChangeToken Instance = new();

        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose() { }
    }
}
