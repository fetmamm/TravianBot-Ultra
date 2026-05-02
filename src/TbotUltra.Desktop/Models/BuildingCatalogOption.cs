using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public enum BuildingConstructAvailability
{
    Available = 0,
    Locked = 1,
    AlreadyBuilt = 2,
    Unavailable = 3,
}

public sealed class BuildingCatalogOption
{
    public int Gid { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsSpecial { get; init; }
    public string? Tribe { get; init; }
    public int MaxLevel { get; init; } = 20;
    public string Requirements { get; init; } = string.Empty;
    public IReadOnlyList<BuildingRequirementEntry> RequirementEntries { get; init; } = [];

    public BuildingConstructAvailability Availability { get; set; } = BuildingConstructAvailability.Available;
    public string UnavailableReason { get; set; } = string.Empty;
    public IReadOnlyList<BuildingRequirementEntry> MissingRequirements { get; set; } = [];

    public string DisplayLabel => IsSpecial
        ? $"{Name} (special)"
        : Name;

    public string MissingRequirementsText => MissingRequirements.Count == 0
        ? string.Empty
        : string.Join(", ", MissingRequirements.Select(req => $"{req.Name} {req.Level}+"));
}
