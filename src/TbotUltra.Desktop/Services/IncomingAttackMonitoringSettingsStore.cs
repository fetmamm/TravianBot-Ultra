using System.IO;
using System.Text.Json;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

public sealed record IncomingAttackMonitoringSettings(
    IReadOnlySet<string> DisabledVillageKeys,
    bool SoundEnabled,
    int SoundCooldownMinutes);

public sealed class IncomingAttackMonitoringSettingsStore(string projectRoot, Action<string>? log = null)
{
    private sealed record FileModel(
        int SchemaVersion,
        IReadOnlyList<string>? DisabledVillageKeys = null,
        bool SoundEnabled = false,
        int SoundCooldownMinutes = DefaultSoundCooldownMinutes);
    private const int CurrentSchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    public const int DefaultSoundCooldownMinutes = 1;
    public static readonly IReadOnlyList<int> SupportedSoundCooldownMinutes = [1, 2, 5, 10];
    private static readonly object IoLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public IncomingAttackMonitoringSettings Load(string? accountName, string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return Defaults();
        var path = AccountStoragePaths.IncomingAttackMonitoringSettingsPath(projectRoot, accountName, serverUrl);
        if (!File.Exists(path)) return Defaults();
        try
        {
            FileModel? file;
            lock (IoLock) file = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions);
            if (file is null || file.SchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion))
            {
                throw new JsonException("Unsupported incoming-attack monitoring schema.");
            }

            var disabledVillageKeys = (file.DisabledVillageKeys ?? [])
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var soundCooldownMinutes = file.SchemaVersion == LegacySchemaVersion
                ? DefaultSoundCooldownMinutes
                : NormalizeSoundCooldownMinutes(file.SoundCooldownMinutes);
            return new IncomingAttackMonitoringSettings(
                disabledVillageKeys,
                file.SchemaVersion == CurrentSchemaVersion && file.SoundEnabled,
                soundCooldownMinutes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Quarantine(path);
            log?.Invoke($"[incoming-attacks] corrupt monitoring settings were quarantined: {ex.Message}");
            return Defaults();
        }
    }

    public void Save(
        string? accountName,
        string? serverUrl,
        IReadOnlyCollection<string> disabledVillageKeys,
        bool soundEnabled,
        int soundCooldownMinutes)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return;
        var path = AccountStoragePaths.IncomingAttackMonitoringSettingsPath(projectRoot, accountName, serverUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var file = new FileModel(
                CurrentSchemaVersion,
                disabledVillageKeys.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                soundEnabled,
                NormalizeSoundCooldownMinutes(soundCooldownMinutes));
            lock (IoLock)
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, JsonOptions));
                File.Move(temporaryPath, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[incoming-attacks] monitoring settings could not be saved: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static IncomingAttackMonitoringSettings Defaults() => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        SoundEnabled: false,
        DefaultSoundCooldownMinutes);

    private static int NormalizeSoundCooldownMinutes(int value) =>
        SupportedSoundCooldownMinutes.Contains(value) ? value : DefaultSoundCooldownMinutes;

    private static void Quarantine(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", overwrite: false);
        }
        catch { }
    }
}
