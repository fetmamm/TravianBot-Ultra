using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using TbotUltra.Core.Accounts;

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
    }

    public sealed class TravcoSavedRow
    {
        public double? Distance { get; set; }
        public string Account { get; set; } = string.Empty;
        public string Village { get; set; } = string.Empty;
        public long? Pop { get; set; }
        public string Coordinates { get; set; } = string.Empty;
        public bool Selected { get; set; } = true;
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
            }).ToList(),
        };
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
