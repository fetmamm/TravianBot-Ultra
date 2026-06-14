namespace TbotUltra.Desktop.Services;

/// <summary>
/// Builds the canonical per-village identity key used everywhere a village must be matched across
/// refreshes: the settings store, the dashboard list dedup, queue-item gating and the rotation keys.
///
/// Coordinates are preferred because they are the most stable identity we have: a village never moves
/// and keeps its coordinates across renames, and the same village can otherwise be seen under multiple
/// <c>newdid</c>s (the page sometimes reports a different did, e.g. via spieler.php), which would split a
/// single village's settings into two divergent records. Falls back to the newdid, then the name, when
/// coordinates are not available (placeholder rows or reads without a coordinate).
/// </summary>
public static class VillageKey
{
    public static string FromComponents(int? coordX, int? coordY, string? newdid, string? name)
    {
        if (coordX.HasValue && coordY.HasValue)
        {
            return FromCoords(coordX.Value, coordY.Value);
        }

        if (!string.IsNullOrWhiteSpace(newdid))
        {
            return $"did:{newdid.Trim()}";
        }

        return $"name:{(name ?? string.Empty).Trim().ToLowerInvariant()}";
    }

    public static string FromCoords(int coordX, int coordY) => $"xy:{coordX}|{coordY}";
}
