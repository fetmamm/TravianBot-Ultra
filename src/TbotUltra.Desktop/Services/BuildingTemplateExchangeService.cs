using System.IO;
using System.Text.Json;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

public enum BuildingTemplateImportConflictAction
{
    Overwrite,
    ImportAsCopy,
}

public sealed record BuildingTemplateImportCandidate(
    BuildingTemplate Template,
    IReadOnlyList<string> Errors,
    bool TribeMismatch)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record BuildingTemplateImportSelection(
    BuildingTemplate Template,
    bool IsSelected,
    BuildingTemplateImportConflictAction ConflictAction);

public sealed record BuildingTemplateImportApplyResult(
    IReadOnlyList<BuildingTemplate> Templates,
    IReadOnlyList<Guid> ImportedTemplateIds,
    int ImportedCount,
    int OverwrittenCount,
    int CopiedCount);

public sealed class BuildingTemplateExchangeService
{
    public const int CurrentSchemaVersion = 1;
    public const string FileExtension = ".tbot-template.json";
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxTemplates = 200;
    private const int MaxRowsPerTemplate = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public void Export(
        string path,
        IReadOnlyList<BuildingTemplate> templates,
        string appVersion,
        DateTimeOffset exportedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (templates.Count == 0)
        {
            throw new InvalidDataException("Select at least one template to export.");
        }

        if (templates.Count > MaxTemplates)
        {
            throw new InvalidDataException($"A template file may contain at most {MaxTemplates} templates.");
        }

        var document = new ExchangeDocumentDto
        {
            SchemaVersion = CurrentSchemaVersion,
            AppVersion = string.IsNullOrWhiteSpace(appVersion) ? "dev" : appVersion.Trim(),
            ExportedAtUtc = exportedAtUtc,
            Templates = templates.Select(ToDto).ToList(),
        };
        var validation = ParseDocument(JsonSerializer.Serialize(document, JsonOptions), currentTribe: null);
        var errors = validation.SelectMany(candidate => candidate.Errors).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"The export contains invalid template data: {errors[0]}");
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
    }

    public IReadOnlyList<BuildingTemplateImportCandidate> Import(string path, string currentTribe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The template file was not found.", path);
        }

        if (info.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The template file is too large.");
        }

        return ParseDocument(File.ReadAllText(path), currentTribe);
    }

    internal IReadOnlyList<BuildingTemplateImportCandidate> ParseDocument(string json, string? currentTribe)
    {
        ExchangeDocumentDto document;
        try
        {
            document = JsonSerializer.Deserialize<ExchangeDocumentDto>(json, JsonOptions)
                ?? throw new InvalidDataException("The template file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The template file is not valid JSON: {ex.Message}", ex);
        }

        if (document.SchemaVersion is null or < 1)
        {
            throw new InvalidDataException("The template file has no supported schema version.");
        }

        if (document.SchemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException("This template file was created by a newer Tbot Ultra version. Update Tbot Ultra before importing it.");
        }

        if (string.IsNullOrWhiteSpace(document.AppVersion) || document.ExportedAtUtc is null)
        {
            throw new InvalidDataException("The template file is missing required export metadata.");
        }

        if (document.Templates is null || document.Templates.Count == 0)
        {
            throw new InvalidDataException("The template file contains no templates.");
        }

        if (document.Templates.Count > MaxTemplates)
        {
            throw new InvalidDataException($"The template file contains more than {MaxTemplates} templates.");
        }

        var duplicateIds = document.Templates
            .Where(template => template?.Id is not null && template.Id != Guid.Empty)
            .GroupBy(template => template!.Id!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var candidates = new List<BuildingTemplateImportCandidate>();
        foreach (var dto in document.Templates)
        {
            if (dto is null)
            {
                candidates.Add(new BuildingTemplateImportCandidate(
                    new BuildingTemplate { Id = Guid.Empty, Name = "Invalid template", CreatedByTribe = "Unknown" },
                    ["A template entry is empty."],
                    false));
                continue;
            }

            var errors = Validate(dto);
            if (dto.Id is Guid id && duplicateIds.Contains(id))
            {
                errors.Add("The template ID occurs more than once in this file.");
            }

            var template = FromDto(dto);
            var tribeMismatch = !string.IsNullOrWhiteSpace(currentTribe)
                && !string.Equals(template.CreatedByTribe, currentTribe, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(template.CreatedByTribe, "Unknown", StringComparison.OrdinalIgnoreCase);
            candidates.Add(new BuildingTemplateImportCandidate(template, errors, tribeMismatch));
        }

        return candidates;
    }

    public BuildingTemplateImportApplyResult ApplyImport(
        IReadOnlyList<BuildingTemplate> existingTemplates,
        IReadOnlyList<BuildingTemplateImportSelection> selections,
        DateTimeOffset importedAtUtc)
    {
        var result = existingTemplates.Select(Clone).ToList();
        var importedIds = new List<Guid>();
        var imported = 0;
        var overwritten = 0;
        var copied = 0;

        foreach (var selection in selections.Where(item => item.IsSelected))
        {
            var conflictIndex = result.FindIndex(template => template.Id == selection.Template.Id);
            if (conflictIndex >= 0 && selection.ConflictAction == BuildingTemplateImportConflictAction.Overwrite)
            {
                var localCreatedAt = result[conflictIndex].CreatedAtUtc;
                var replacement = Clone(selection.Template);
                replacement.CreatedAtUtc = localCreatedAt;
                replacement.UpdatedAtUtc = importedAtUtc;
                result[conflictIndex] = replacement;
                importedIds.Add(replacement.Id);
                overwritten++;
                continue;
            }

            var addition = Clone(selection.Template);
            if (conflictIndex >= 0)
            {
                addition.Id = Guid.NewGuid();
                addition.Name = CreateCopyName(addition.Name, result);
                addition.CreatedAtUtc = importedAtUtc;
                addition.UpdatedAtUtc = importedAtUtc;
                foreach (var row in addition.Rows)
                {
                    row.Id = Guid.NewGuid();
                }

                copied++;
            }
            else
            {
                imported++;
            }

            result.Add(addition);
            importedIds.Add(addition.Id);
        }

        return new BuildingTemplateImportApplyResult(result, importedIds, imported, overwritten, copied);
    }

    private static List<string> Validate(TemplateDto template)
    {
        var errors = new List<string>();
        if (template.Id is null || template.Id == Guid.Empty) errors.Add("Template ID is missing.");
        if (string.IsNullOrWhiteSpace(template.Name)) errors.Add("Template name is required.");
        if (string.IsNullOrWhiteSpace(template.CreatedByTribe)) errors.Add("Created-by tribe is required.");
        if (template.CreatedAtUtc is null || template.UpdatedAtUtc is null) errors.Add("Template timestamps are required.");
        if (template.Rows is null)
        {
            errors.Add("Template rows are missing.");
            return errors;
        }

        if (template.Rows.Count > MaxRowsPerTemplate)
        {
            errors.Add($"A template may contain at most {MaxRowsPerTemplate} rows.");
        }

        var rowIds = new HashSet<Guid>();
        for (var index = 0; index < template.Rows.Count; index++)
        {
            var row = template.Rows[index];
            var prefix = $"Row {index + 1}";
            if (row is null)
            {
                errors.Add($"{prefix} is empty.");
                continue;
            }

            if (row.Id is null || row.Id == Guid.Empty) errors.Add($"{prefix}: row ID is missing.");
            else if (!rowIds.Add(row.Id.Value)) errors.Add($"{prefix}: row ID is duplicated.");
            if (!Enum.TryParse<BuildingTemplateRowKind>(row.Kind, ignoreCase: true, out var kind)
                || !Enum.IsDefined(kind))
            {
                errors.Add($"{prefix}: task kind is invalid.");
                continue;
            }

            if (row.TargetLevel is null or < 1 or > 20) errors.Add($"{prefix}: target level must be between 1 and 20.");
            if (row.PreferredSlotId is not null and (< 19 or > 43)) errors.Add($"{prefix}: slot must be between 19 and 43.");
            if (kind == BuildingTemplateRowKind.Building)
            {
                if (row.Gid is null or <= 0) errors.Add($"{prefix}: building ID is required.");
                if (string.IsNullOrWhiteSpace(row.BuildingName)) errors.Add($"{prefix}: building name is required.");
            }
            else if (kind == BuildingTemplateRowKind.AllResources && !IsValidResourceScope(row.ResourceScope))
            {
                errors.Add($"{prefix}: resource scope is invalid.");
            }
            else if (kind == BuildingTemplateRowKind.Watchtowers
                && (!string.Equals(row.BuildingName, "Watchtowers", StringComparison.OrdinalIgnoreCase)
                    || row.PreferredSlotId != 40))
            {
                errors.Add($"{prefix}: Watchtowers must target wall slot 40.");
            }
        }

        return errors;
    }

    private static bool IsValidResourceScope(string? scope)
        => scope is not null
            && new[] { "all", "wood", "clay", "iron", "crop" }
                .Contains(scope.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string CreateCopyName(string sourceName, IReadOnlyList<BuildingTemplate> templates)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Template" : sourceName.Trim();
        var candidate = $"{baseName} copy";
        var number = 2;
        while (templates.Any(template => string.Equals(template.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} copy {number++}";
        }

        return candidate;
    }

    private static TemplateDto ToDto(BuildingTemplate template)
        => new()
        {
            Id = template.Id,
            Name = template.Name,
            CreatedByTribe = template.CreatedByTribe,
            CreatedAtUtc = template.CreatedAtUtc,
            UpdatedAtUtc = template.UpdatedAtUtc,
            Rows = template.Rows.Select(row => new RowDto
            {
                Id = row.Id,
                Kind = row.Kind.ToString(),
                Gid = row.Gid,
                BuildingName = row.BuildingName,
                PreferredSlotId = row.PreferredSlotId,
                TargetLevel = row.TargetLevel,
                ResourceScope = row.ResourceScope,
                ResourceStrategy = row.ResourceStrategy,
            }).ToList(),
        };

    private static BuildingTemplate FromDto(TemplateDto template)
        => new()
        {
            Id = template.Id ?? Guid.Empty,
            Name = template.Name?.Trim() ?? string.Empty,
            CreatedByTribe = template.CreatedByTribe?.Trim() ?? string.Empty,
            CreatedAtUtc = template.CreatedAtUtc ?? default,
            UpdatedAtUtc = template.UpdatedAtUtc ?? default,
            Rows = (template.Rows ?? []).Where(row => row is not null).Select(row => new BuildingTemplateRow
            {
                Id = row!.Id ?? Guid.Empty,
                Kind = Enum.TryParse<BuildingTemplateRowKind>(row.Kind, true, out var kind) ? kind : (BuildingTemplateRowKind)(-1),
                Gid = row.Gid,
                BuildingName = row.BuildingName?.Trim() ?? string.Empty,
                PreferredSlotId = row.PreferredSlotId,
                TargetLevel = row.TargetLevel ?? 0,
                ResourceScope = row.ResourceScope?.Trim() ?? string.Empty,
                ResourceStrategy = row.ResourceStrategy?.Trim() ?? string.Empty,
            }).ToList(),
        };

    private static BuildingTemplate Clone(BuildingTemplate template)
        => new()
        {
            Id = template.Id,
            Name = template.Name,
            CreatedByTribe = template.CreatedByTribe,
            CreatedAtUtc = template.CreatedAtUtc,
            UpdatedAtUtc = template.UpdatedAtUtc,
            Rows = template.Rows.Select(row => new BuildingTemplateRow
            {
                Id = row.Id,
                Kind = row.Kind,
                Gid = row.Gid,
                BuildingName = row.BuildingName,
                PreferredSlotId = row.PreferredSlotId,
                TargetLevel = row.TargetLevel,
                ResourceScope = row.ResourceScope,
                ResourceStrategy = row.ResourceStrategy,
            }).ToList(),
        };

    private sealed class ExchangeDocumentDto
    {
        public int? SchemaVersion { get; set; }
        public string? AppVersion { get; set; }
        public DateTimeOffset? ExportedAtUtc { get; set; }
        public List<TemplateDto>? Templates { get; set; }
    }

    private sealed class TemplateDto
    {
        public Guid? Id { get; set; }
        public string? Name { get; set; }
        public string? CreatedByTribe { get; set; }
        public DateTimeOffset? CreatedAtUtc { get; set; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public List<RowDto>? Rows { get; set; }
    }

    private sealed class RowDto
    {
        public Guid? Id { get; set; }
        public string? Kind { get; set; }
        public int? Gid { get; set; }
        public string? BuildingName { get; set; }
        public int? PreferredSlotId { get; set; }
        public int? TargetLevel { get; set; }
        public string? ResourceScope { get; set; }
        public string? ResourceStrategy { get; set; }
    }
}
