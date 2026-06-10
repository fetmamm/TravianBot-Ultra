using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public sealed class TravcoListStore
{
    public sealed class TravcoSavedList
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<TravcoSavedRow> Rows { get; set; } = [];

        [JsonIgnore]
        public string SavedDateText => CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        [JsonIgnore]
        public int VillageCount => Rows.Count;
    }

    public sealed class TravcoSavedRow
    {
        public double? Distance { get; set; }
        public string Account { get; set; } = string.Empty;
        public string Village { get; set; } = string.Empty;
        public long? Pop { get; set; }
        public string Coordinates { get; set; } = string.Empty;
        public bool Selected { get; set; } = true;
        public string? OasisType { get; set; }
        public bool? IsOccupied { get; set; }
    }

    private sealed class TravcoListFile
    {
        public List<TravcoSavedList> Lists { get; set; } = [];
    }

    private static readonly object FileIoLock = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _projectRoot;
    private readonly Func<string> _activeAccountNameProvider;
    private readonly Action<string>? _log;
    private List<TravcoSavedList> _cache = [];
    private string? _cacheAccount;

    public TravcoListStore(string projectRoot, Func<string> activeAccountNameProvider, Action<string>? log = null)
    {
        _projectRoot = projectRoot;
        _activeAccountNameProvider = activeAccountNameProvider;
        _log = log;
    }

    public IReadOnlyList<TravcoSavedList> LoadAll()
    {
        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache
                .OrderByDescending(list => list.CreatedUtc)
                .Select(Clone)
                .ToList();
        }
    }

    public void Save(TravcoSavedList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (string.IsNullOrWhiteSpace(list.Name))
        {
            throw new InvalidOperationException("Travco list name is required.");
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var normalized = Clone(list);
            normalized.Name = normalized.Name.Trim();
            normalized.Rows = RemoveDuplicates(normalized.Rows, out var missingCoordinates);
            if (missingCoordinates > 0)
            {
                _log?.Invoke(
                    $"ALARM: Travco list '{normalized.Name}' skipped {missingCoordinates} village(s) because coordinates were missing or unreadable.");
            }

            if (normalized.Id == Guid.Empty)
            {
                normalized.Id = Guid.NewGuid();
            }

            var index = _cache.FindIndex(existing => existing.Id == normalized.Id);
            if (index >= 0)
            {
                _cache[index] = normalized;
            }
            else
            {
                _cache.Add(normalized);
            }

            SaveFile();
            _log?.Invoke($"[travco] saved list '{normalized.Name}' with {normalized.Rows.Count} row(s).");
        }
    }

    public bool Delete(Guid id)
    {
        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var removed = _cache.RemoveAll(list => list.Id == id) > 0;
            if (removed)
            {
                SaveFile();
                _log?.Invoke($"[travco] deleted saved list {id}.");
            }

            return removed;
        }
    }

    public void InvalidateCache()
    {
        lock (FileIoLock)
        {
            _cache = [];
            _cacheAccount = null;
        }
    }

    public int RemoveRowsByCoordinates(Guid listId, IEnumerable<FarmCoordinate> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        var coordinateKeys = coordinates
            .Select(coordinate => $"{coordinate.X}|{coordinate.Y}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (coordinateKeys.Count == 0)
        {
            return 0;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var list = _cache.FirstOrDefault(candidate => candidate.Id == listId);
            if (list is null)
            {
                return 0;
            }

            var removed = list.Rows.RemoveAll(row =>
                TryNormalizeCoordinates(row.Coordinates, out var normalized)
                && coordinateKeys.Contains(normalized));
            if (removed > 0)
            {
                SaveFile();
                _log?.Invoke($"[travco] removed {removed} invalid coordinate(s) from '{list.Name}'.");
            }

            return removed;
        }
    }

    public static List<TravcoSavedRow> RemoveDuplicates(IEnumerable<TravcoSavedRow> rows)
    {
        return RemoveDuplicates(rows, out _);
    }

    public static List<TravcoSavedRow> RemoveDuplicates(
        IEnumerable<TravcoSavedRow> rows,
        out int missingCoordinates)
    {
        ArgumentNullException.ThrowIfNull(rows);

        missingCoordinates = 0;
        var uniqueRows = new List<TravcoSavedRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!TryNormalizeCoordinates(row.Coordinates, out var coordinates))
            {
                missingCoordinates++;
                continue;
            }

            if (!seen.Add(coordinates))
            {
                continue;
            }

            uniqueRows.Add(new TravcoSavedRow
            {
                Distance = row.Distance,
                Account = row.Account,
                Village = row.Village,
                Pop = row.Pop,
                Coordinates = coordinates,
                Selected = row.Selected,
                OasisType = row.OasisType,
                IsOccupied = row.IsOccupied,
            });
        }

        return uniqueRows;
    }

    private void EnsureCacheLoaded()
    {
        var account = GetActiveAccountName();
        if (string.Equals(_cacheAccount, account, StringComparison.Ordinal))
        {
            return;
        }

        _cacheAccount = account;
        _cache = [];
        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        var path = AccountStoragePaths.TravcoListsPath(_projectRoot, account);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var raw = RetryFileIo(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            });
            _cache = JsonSerializer.Deserialize<TravcoListFile>(raw, SerializerOptions)?.Lists ?? [];
        }
        catch (Exception ex)
        {
            _cache = [];
            _log?.Invoke($"[travco] could not load saved lists: {ex.Message}");
        }
    }

    private void SaveFile()
    {
        if (string.IsNullOrWhiteSpace(_cacheAccount))
        {
            throw new InvalidOperationException("No active account is available for Travco list storage.");
        }

        var path = AccountStoragePaths.TravcoListsPath(_projectRoot, _cacheAccount);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = JsonSerializer.Serialize(new TravcoListFile { Lists = _cache }, SerializerOptions);
        RetryFileIo(() =>
        {
            File.WriteAllText(path, content);
            return true;
        });
    }

    private string GetActiveAccountName()
    {
        try
        {
            return _activeAccountNameProvider() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TravcoSavedList Clone(TravcoSavedList list)
    {
        return new TravcoSavedList
        {
            Id = list.Id,
            Name = list.Name,
            CreatedUtc = list.CreatedUtc,
            Rows = list.Rows.Select(row => new TravcoSavedRow
            {
                Distance = row.Distance,
                Account = row.Account,
                Village = row.Village,
                Pop = row.Pop,
                Coordinates = row.Coordinates,
                Selected = row.Selected,
                OasisType = row.OasisType,
                IsOccupied = row.IsOccupied,
            }).ToList(),
        };
    }

    private static bool TryNormalizeCoordinates(string? coordinates, out string normalized)
    {
        var match = Regex.Match(
            coordinates ?? string.Empty,
            @"^\s*[\[(]?\s*(?<x>-?\d+)\s*\|\s*(?<y>-?\d+)\s*[\])]?\s*$");
        if (!match.Success
            || !int.TryParse(match.Groups["x"].Value, out var x)
            || !int.TryParse(match.Groups["y"].Value, out var y))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = $"{x}|{y}";
        return true;
    }

    private static T RetryFileIo<T>(Func<T> action)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
