using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TbotUltra.Worker;

namespace TbotUltra.Desktop.Services;

/// <summary>One saved proxy from a finder run: enough to re-list it and re-apply it after a restart.</summary>
public sealed class ProxyFinderSavedResult
{
    public string Scheme { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public long LatencyMs { get; set; }
    public string Country { get; set; } = string.Empty;
}

/// <summary>
/// Everything the proxy finder remembers between sessions: the pasted list, the run settings and the
/// last ranked results — so reopening the window is instant to re-check without pasting again.
/// </summary>
public sealed class ProxyFinderState
{
    public string ProxyList { get; set; } = string.Empty;
    public string Protocol { get; set; } = "socks5";
    public string Parallel { get; set; } = "200";
    public string MaxProxies { get; set; } = "2000";
    public string Top { get; set; } = "10";
    public List<ProxyFinderSavedResult> Results { get; set; } = new();
}

/// <summary>
/// Persists the proxy finder's pasted list, settings and last results to a single global JSON file
/// (config/proxyfinder.json) so nothing is lost when the window or app closes. Not account-scoped —
/// the pasted list is a shared resource reused across accounts. Best-effort: a missing or corrupt
/// file just yields no saved state. Mirrors <see cref="ProductionBonusStateStore"/>'s read-retry +
/// <see cref="AtomicFile"/> write so an OneDrive/AV lock never corrupts or aborts a save.
/// </summary>
public static class ProxyFinderStateStore
{
    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string StatePath()
        => Path.Combine(ProjectRootLocator.FindProjectRoot(), "config", "proxyfinder.json");

    /// <summary>Returns the saved state, or null when nothing has been saved yet or the file is unreadable.</summary>
    public static ProxyFinderState? Load()
    {
        lock (FileIoLock)
        {
            var path = StatePath();
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var raw = ReadAllTextWithRetry(path);
                return JsonSerializer.Deserialize<ProxyFinderState>(raw, SerializerOptions);
            }
            catch
            {
                return null; // corrupt or locked — behave as if nothing was saved
            }
        }
    }

    public static void Save(ProxyFinderState state)
    {
        lock (FileIoLock)
        {
            try
            {
                AtomicFile.WriteAllText(StatePath(), JsonSerializer.Serialize(state, SerializerOptions));
            }
            catch
            {
                // Persistence is a convenience; never let a save failure disrupt the UI.
            }
        }
    }

    private static string ReadAllTextWithRetry(string path)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(40 * attempt);
            }
        }
    }
}
