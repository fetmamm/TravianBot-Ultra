using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

public sealed class BuildingTemplateStore
{
    private const string ManagedFilesIndexName = "building_templates.manifest.json";
    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly string _directory;
    private readonly string _appVersion;
    private readonly BuildingTemplateExchangeService _exchangeService = new();
    private bool _saveBlockedByLoadFailure;

    public string? LastLoadWarning { get; private set; }
    public string DirectoryPath => _directory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public BuildingTemplateStore(string projectRoot)
    {
        _directory = Path.Combine(projectRoot, "building_templates");
        _path = Path.Combine(_directory, "building_templates.json");
        _legacyPath = Path.Combine(projectRoot, "config", "building_templates.json");
        _appVersion = UpdateChecker.ReadCurrentVersion(Path.Combine(projectRoot, "VERSION"));
    }

    internal BuildingTemplateStore(string path, bool useExactPath)
    {
        _path = useExactPath ? path : Path.Combine(path, "building_templates", "building_templates.json");
        _legacyPath = useExactPath ? null : Path.Combine(path, "config", "building_templates.json");
        _directory = Path.GetDirectoryName(_path) ?? path;
        _appVersion = "dev";
    }

    public IReadOnlyList<BuildingTemplate> Load()
    {
        LastLoadWarning = null;
        _saveBlockedByLoadFailure = false;
        var loadPath = File.Exists(_path)
            ? _path
            : _legacyPath is not null && File.Exists(_legacyPath)
                ? _legacyPath
                : _path;
        if (!File.Exists(loadPath))
        {
            return [];
        }

        try
        {
            var raw = File.ReadAllText(loadPath);
            var file = JsonSerializer.Deserialize<BuildingTemplateFile>(raw, JsonOptions);
            var templates = Normalize(file?.Templates ?? []);
            if (!string.Equals(loadPath, _path, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Save(templates);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _saveBlockedByLoadFailure = true;
                    LastLoadWarning =
                        $"Templates were loaded from the legacy config file, but migration failed ({ex.Message}). The original file was preserved.";
                }
            }
            else
            {
                try
                {
                    SyncIndividualFiles(templates);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    LastLoadWarning = $"Templates were loaded, but their individual files could not be synchronized ({ex.Message}).";
                }
            }
            return templates;
        }
        catch (JsonException ex)
        {
            var quarantinePath = $"{loadPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
            try
            {
                File.Move(loadPath, quarantinePath);
                LastLoadWarning =
                    $"The template file contained invalid JSON and was moved to '{Path.GetFileName(quarantinePath)}'.";
                return [];
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                _saveBlockedByLoadFailure = true;
                LastLoadWarning =
                    $"The template file is invalid and could not be quarantined ({ex.Message}). Saving is disabled to protect it.";
                return [];
            }
        }
        catch (IOException ex)
        {
            _saveBlockedByLoadFailure = true;
            LastLoadWarning = $"The template file could not be read ({ex.Message}). Saving is disabled to protect it.";
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            _saveBlockedByLoadFailure = true;
            LastLoadWarning = $"The template file could not be accessed ({ex.Message}). Saving is disabled to protect it.";
            return [];
        }
    }

    public Task<IReadOnlyList<BuildingTemplate>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.Run(Load, cancellationToken);

    public void Save(IReadOnlyList<BuildingTemplate> templates)
    {
        if (_saveBlockedByLoadFailure)
        {
            throw new IOException("Templates cannot be saved until the existing template file can be read or quarantined.");
        }

        Directory.CreateDirectory(_directory);

        var normalized = Normalize(templates).ToList();
        var file = new BuildingTemplateFile(normalized);
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOptions));
        SyncIndividualFiles(normalized);
    }

    private void SyncIndividualFiles(IReadOnlyList<BuildingTemplate> templates)
    {
        Directory.CreateDirectory(_directory);
        var indexPath = Path.Combine(_directory, ManagedFilesIndexName);
        var priorFiles = LoadManagedFilesIndex(indexPath);
        var currentFiles = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmanagedFiles = Directory
            .EnumerateFiles(_directory, $"*{BuildingTemplateExchangeService.FileExtension}", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Except(priorFiles, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exportedAtUtc = DateTimeOffset.UtcNow;

        foreach (var template in templates)
        {
            var baseName = SanitizeTemplateFileName(template.Name);
            var fileName = baseName + BuildingTemplateExchangeService.FileExtension;
            var copyNumber = 2;
            while (!usedNames.Add(fileName) || unmanagedFiles.Contains(fileName))
            {
                fileName = $"{baseName} ({copyNumber++}){BuildingTemplateExchangeService.FileExtension}";
            }

            _exchangeService.Export(
                Path.Combine(_directory, fileName),
                [template],
                _appVersion,
                exportedAtUtc);
            currentFiles.Add(fileName);
        }

        foreach (var staleFile in priorFiles.Except(currentFiles, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSafeManagedFileName(staleFile))
            {
                continue;
            }

            var stalePath = Path.Combine(_directory, staleFile);
            if (File.Exists(stalePath))
            {
                File.Delete(stalePath);
            }
        }

        AtomicFile.WriteAllText(
            indexPath,
            JsonSerializer.Serialize(new ManagedFilesIndex(1, currentFiles), JsonOptions));
    }

    private static IReadOnlyList<string> LoadManagedFilesIndex(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var index = JsonSerializer.Deserialize<ManagedFilesIndex>(File.ReadAllText(path), JsonOptions);
            return index is { SchemaVersion: 1, Files: not null }
                ? index.Files.Where(IsSafeManagedFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static bool IsSafeManagedFileName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
            && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            && fileName.EndsWith(BuildingTemplateExchangeService.FileExtension, StringComparison.OrdinalIgnoreCase);

    internal static string SanitizeTemplateFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((string.IsNullOrWhiteSpace(name) ? "building-template" : name.Trim())
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        if (sanitized.Length > 120)
        {
            sanitized = sanitized[..120].TrimEnd('.', ' ');
        }

        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };
        if (reservedNames.Contains(sanitized))
        {
            sanitized = $"_{sanitized}";
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "building-template" : sanitized;
    }

    private static IReadOnlyList<BuildingTemplate> Normalize(IReadOnlyList<BuildingTemplate> templates)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<BuildingTemplate>();
        foreach (var template in templates.Where(item => item is not null))
        {
            var name = string.IsNullOrWhiteSpace(template.Name)
                ? "Untitled template"
                : template.Name.Trim();
            result.Add(new BuildingTemplate
            {
                Id = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id,
                Name = name,
                CreatedByTribe = string.IsNullOrWhiteSpace(template.CreatedByTribe)
                    ? "Unknown"
                    : template.CreatedByTribe.Trim(),
                CreatedAtUtc = template.CreatedAtUtc == default ? now : template.CreatedAtUtc,
                UpdatedAtUtc = template.UpdatedAtUtc == default ? now : template.UpdatedAtUtc,
                Rows = (template.Rows ?? [])
                    .Where(row => row is not null)
                    .Select(row => new BuildingTemplateRow
                    {
                        Id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id,
                        Kind = row.Kind,
                        Gid = row.Gid,
                        BuildingName = row.BuildingName?.Trim() ?? string.Empty,
                        PreferredSlotId = row.PreferredSlotId is >= 19 and <= 43 ? row.PreferredSlotId : null,
                        TargetLevel = Math.Clamp(row.TargetLevel, 1, 20),
                        ResourceScope = NormalizeResourceScope(row.ResourceScope),
                        ResourceStrategy = string.IsNullOrWhiteSpace(row.ResourceStrategy)
                            ? "lowest"
                            : row.ResourceStrategy.Trim(),
                    })
                    .ToList(),
            });
        }

        return result;
    }

    private static string NormalizeResourceScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "all";
        if (value.Contains("Wood", StringComparison.OrdinalIgnoreCase)) return "wood";
        if (value.Contains("Clay", StringComparison.OrdinalIgnoreCase)) return "clay";
        if (value.Contains("Iron", StringComparison.OrdinalIgnoreCase)) return "iron";
        if (value.Contains("Crop", StringComparison.OrdinalIgnoreCase)) return "crop";
        return string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
            ? "all"
            : value.Trim().ToLowerInvariant();
    }

    private sealed record BuildingTemplateFile(List<BuildingTemplate> Templates);
    private sealed record ManagedFilesIndex(int SchemaVersion, List<string> Files);
}
