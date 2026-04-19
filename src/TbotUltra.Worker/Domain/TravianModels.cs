namespace TbotUltra.Worker.Domain;

public sealed record Village(string Name, string? Url);

public sealed record AccountSnapshot(
    string Tribe,
    string ActiveVillage,
    int VillageCount,
    IReadOnlyList<Village> Villages,
    DateTimeOffset? ServerTimeUtc = null);

public sealed record ResourceField(
    int? SlotId,
    string FieldType,
    string Name,
    int? Level,
    string? Url);

public sealed record Building(
    int? SlotId,
    string Name,
    int? Level,
    string? Url,
    int? Gid = null);

public sealed record BuildQueueItem(string Text, string? TimeLeft);

public sealed record ServerBuildChoice(int Gid, string Name, bool Available, string Reason);

public sealed record VillageStatus(
    string ActiveVillage,
    IReadOnlyList<Village> Villages,
    IReadOnlyDictionary<string, string> Resources,
    IReadOnlyList<ResourceField> ResourceFields,
    IReadOnlyList<Building> Buildings,
    IReadOnlyList<BuildQueueItem> BuildQueue,
    DateTimeOffset? ServerTimeUtc = null);
