using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TbotUltra.Worker.Infrastructure;

/// <summary>
/// Remembers which browser processes this app launched, so a run that crashed or was force-stopped can have
/// its leftover browser windows cleaned up on the next start.
///
/// Identifying them by executable path is not possible any more: the session runs the user's system Google
/// Chrome (for the H.264/AAC codecs the bonus videos need), so a bot-owned process looks exactly like a
/// window the user opened themselves. Killing by name or path would close the user's own browsing.
///
/// Instead each launched process is recorded as PID + start time + executable path, and cleanup only kills a
/// process when all three still match. The start time is what makes this safe: Windows reuses PIDs, and an
/// unrelated process that inherited a recorded PID will not share its start time down to the tick.
/// </summary>
public static class LaunchedBrowserRegistry
{
    private sealed record LaunchedBrowser(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("startedAtUtcTicks")] long StartedAtUtcTicks,
        [property: JsonPropertyName("executablePath")] string ExecutablePath);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly object FileGate = new();

    private static string RegistryPath(string projectRoot)
        => Path.Combine(projectRoot, "config", "cache", "launched-browsers.json");

    /// <summary>
    /// Captures the browser processes that appeared while <paramref name="launch"/> ran and records them as
    /// owned by this app. The before/after difference is narrowed to processes started after the snapshot
    /// was taken, so a browser the user happened to open at the same moment is far less likely to be caught.
    /// </summary>
    public static async Task<T> TrackAsync<T>(
        string projectRoot,
        string? channel,
        Func<Task<T>> launch,
        Action<string>? log = null)
    {
        var processName = ResolveBrowserProcessName(channel);
        var launchedAt = DateTime.UtcNow;
        var before = SnapshotProcessIds(processName);

        var result = await launch();

        try
        {
            var appeared = FindProcessesStartedDuringLaunch(processName, before, launchedAt);
            if (appeared.Count > 0)
            {
                Record(projectRoot, appeared);
                log?.Invoke($"[browser] tracking {appeared.Count} launched '{processName}' process(es) for orphan cleanup.");
            }
        }
        catch (Exception ex)
        {
            // Tracking is a convenience for the NEXT start; never fail a working launch over it.
            log?.Invoke($"[browser] could not track the launched browser process: {ex.Message}");
        }

        return result;
    }

    /// <summary>Clears the registry after a clean shutdown — those processes are gone on purpose.</summary>
    public static void Forget(string projectRoot, Action<string>? log = null)
    {
        try
        {
            lock (FileGate)
            {
                var path = RegistryPath(projectRoot);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[browser] could not clear the launched-browser registry: {ex.Message}");
        }
    }

    /// <summary>
    /// Kills browser processes recorded by a previous run that are still alive, and clears the registry.
    /// Skipped entirely while another instance of this app is running, because those browsers may be its
    /// live session. Returns how many processes were terminated.
    /// </summary>
    public static int KillOrphanedBrowsers(string projectRoot, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || IsAnotherAppInstanceRunning())
        {
            return 0;
        }

        var recorded = Read(projectRoot, log);
        if (recorded.Count == 0)
        {
            return 0;
        }

        var killed = 0;
        foreach (var entry in recorded)
        {
            if (TryKillRecordedProcess(entry, log))
            {
                killed++;
            }
        }

        Forget(projectRoot, log);
        return killed;
    }

    private static bool TryKillRecordedProcess(LaunchedBrowser entry, Action<string>? log)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(entry.Pid);
            if (process.StartTime.ToUniversalTime().Ticks != entry.StartedAtUtcTicks)
            {
                // The PID was reused by an unrelated process. Leaving it alone is the whole point.
                return false;
            }

            if (!string.Equals(TryGetExecutablePath(process), entry.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            log?.Invoke($"[browser] closed leftover browser process {entry.Pid} from a previous run.");
            return true;
        }
        catch (ArgumentException)
        {
            // Already exited — the normal case.
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[browser] could not close leftover browser process {entry.Pid}: {ex.Message}");
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static List<LaunchedBrowser> FindProcessesStartedDuringLaunch(
        string processName,
        HashSet<int> before,
        DateTime launchedAtUtc)
    {
        var appeared = new List<LaunchedBrowser>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (before.Contains(process.Id))
                {
                    continue;
                }

                var startedAtUtc = process.StartTime.ToUniversalTime();
                // A small tolerance absorbs clock granularity between the snapshot and the process start.
                if (startedAtUtc < launchedAtUtc.AddSeconds(-1))
                {
                    continue;
                }

                var executablePath = TryGetExecutablePath(process);
                if (string.IsNullOrEmpty(executablePath))
                {
                    continue;
                }

                appeared.Add(new LaunchedBrowser(process.Id, startedAtUtc.Ticks, executablePath));
            }
            catch
            {
                // Access denied / already exited / different bitness — skip.
            }
            finally
            {
                process.Dispose();
            }
        }

        return appeared;
    }

    private static HashSet<int> SnapshotProcessIds(string processName)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                ids.Add(process.Id);
            }
            catch
            {
                // Exited between enumeration and read.
            }
            finally
            {
                process.Dispose();
            }
        }

        return ids;
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    // Playwright's "msedge" channel runs msedge.exe; "chrome" and the bundled build both run chrome.exe.
    private static string ResolveBrowserProcessName(string? channel)
        => string.Equals(channel, "msedge", StringComparison.OrdinalIgnoreCase) ? "msedge" : "chrome";

    // The published portable exe is "Tbot Ultra.exe"; development builds run as TbotUltra.Desktop.exe.
    private static bool IsAnotherAppInstanceRunning()
    {
        foreach (var name in new[] { "Tbot Ultra", "TbotUltra.Desktop" })
        {
            var instances = Process.GetProcessesByName(name);
            try
            {
                if (instances.Length > 1)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var instance in instances)
                {
                    instance.Dispose();
                }
            }
        }

        return false;
    }

    private static void Record(string projectRoot, IReadOnlyList<LaunchedBrowser> launched)
    {
        lock (FileGate)
        {
            var path = RegistryPath(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Append: a session can launch the isolated bonus-video browser alongside the main one.
            var existing = ReadFile(path);
            existing.AddRange(launched);
            File.WriteAllText(path, JsonSerializer.Serialize(existing, SerializerOptions));
        }
    }

    private static List<LaunchedBrowser> Read(string projectRoot, Action<string>? log)
    {
        try
        {
            lock (FileGate)
            {
                return ReadFile(RegistryPath(projectRoot));
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[browser] could not read the launched-browser registry: {ex.Message}");
            return [];
        }
    }

    private static List<LaunchedBrowser> ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<LaunchedBrowser>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt registry must never block startup; the worst case is one uncleaned orphan.
            return [];
        }
    }
}
