using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed class HeroRaidingExclusionStore(string projectRoot, Action<string> log)
{
    public IReadOnlySet<(int X, int Y)> Load(string accountName, string? serverUrl)
    {
        var path = AccountStoragePaths.HeroRaidingExclusionsPath(projectRoot, accountName, serverUrl);
        if (!File.Exists(path)) return new HashSet<(int X, int Y)>();

        try
        {
            var saved = JsonSerializer.Deserialize<HeroRaidingExclusions>(File.ReadAllText(path));
            if (saved?.Excluded is null)
                throw new InvalidDataException("The excluded oasis list is empty or invalid.");

            var result = new HashSet<(int X, int Y)>();
            foreach (var coordinate in saved.Excluded)
            {
                if (coordinate.X is < -200 or > 200 || coordinate.Y is < -200 or > 200)
                    throw new InvalidDataException("The excluded oasis list contains an invalid coordinate.");
                result.Add((coordinate.X, coordinate.Y));
            }

            log($"[hero-raiding] loaded {result.Count} excluded oasis coordinate(s).");
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Losing an exclusion could turn an explicitly blocked target into an eligible one.
            throw new InvalidOperationException("Hero raiding cannot start because saved oasis exclusions could not be read.", ex);
        }
    }

    public void Save(string accountName, string? serverUrl, IEnumerable<(int X, int Y)> excluded)
    {
        var coordinates = excluded.Distinct()
            .OrderBy(item => item.Y).ThenBy(item => item.X)
            .Select(item => new OasisCoordinate(item.X, item.Y))
            .ToList();
        if (coordinates.Any(item => item.X is < -200 or > 200 || item.Y is < -200 or > 200))
            throw new ArgumentOutOfRangeException(nameof(excluded), "Excluded oasis coordinates must be on the map.");

        var path = AccountStoragePaths.HeroRaidingExclusionsPath(projectRoot, accountName, serverUrl);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new HeroRaidingExclusions(coordinates)));
        log($"[hero-raiding] saved {coordinates.Count} excluded oasis coordinate(s).");
    }

    private sealed record HeroRaidingExclusions(List<OasisCoordinate> Excluded);
    private sealed record OasisCoordinate(int X, int Y);
}
