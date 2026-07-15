using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

public sealed class BuildingTemplateStore
{
    private readonly string _path;
    private bool _saveBlockedByLoadFailure;

    public string? LastLoadWarning { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public BuildingTemplateStore(string projectRoot)
    {
        _path = Path.Combine(projectRoot, "config", "building_templates.json");
    }

    internal BuildingTemplateStore(string path, bool useExactPath)
    {
        _path = useExactPath ? path : Path.Combine(path, "config", "building_templates.json");
    }

    public IReadOnlyList<BuildingTemplate> Load()
    {
        LastLoadWarning = null;
        _saveBlockedByLoadFailure = false;
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var raw = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<BuildingTemplateFile>(raw, JsonOptions);
            return Normalize(file?.Templates ?? []);
        }
        catch (JsonException ex)
        {
            var quarantinePath = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
            try
            {
                File.Move(_path, quarantinePath);
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

    public void Save(IReadOnlyList<BuildingTemplate> templates)
    {
        if (_saveBlockedByLoadFailure)
        {
            throw new IOException("Templates cannot be saved until the existing template file can be read or quarantined.");
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new BuildingTemplateFile(Normalize(templates).ToList());
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOptions));
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
                        PreferredSlotId = row.PreferredSlotId is >= 19 and <= 40 ? row.PreferredSlotId : null,
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
}
