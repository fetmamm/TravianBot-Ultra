using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed class BuildingCatalogOption
{
    public int Gid { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsSpecial { get; init; }
    public string Requirements { get; init; } = string.Empty;
    public IReadOnlyList<BuildingRequirementEntry> RequirementEntries { get; init; } = [];

    public string DisplayLabel => IsSpecial
        ? $"{Name} (special)"
        : Name;
}
