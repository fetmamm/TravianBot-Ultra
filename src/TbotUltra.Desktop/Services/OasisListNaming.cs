namespace TbotUltra.Desktop.Services;

public static class OasisListNaming
{
    public static readonly IReadOnlyList<string> TypeOrder =
    [
        "Wood",
        "Clay",
        "Iron",
        "Crop",
        "Wood+Crop",
        "Clay+Crop",
        "Iron+Crop",
    ];

    public static string CreateName(
        IReadOnlyCollection<string> selectedTypes,
        IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(selectedTypes);
        ArgumentNullException.ThrowIfNull(existingNames);

        var selected = new HashSet<string>(selectedTypes, StringComparer.OrdinalIgnoreCase);
        var ordered = TypeOrder.Where(selected.Contains).ToList();
        if (ordered.Count == 0)
        {
            throw new InvalidOperationException("Select at least one oasis type.");
        }

        var baseName = ordered.Count == TypeOrder.Count
            ? "Map Oasis"
            : $"Map Oasis_{string.Join("_", ordered)}";
        var names = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} {suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
