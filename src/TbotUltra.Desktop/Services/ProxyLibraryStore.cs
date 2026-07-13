using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Worker;

namespace TbotUltra.Desktop.Services;

public enum ProxyReuse
{
    Ok,
    UsedByOthers,
    LockedToOther,
}

public sealed record ProxyReuseClassification(ProxyReuse Reuse, IReadOnlyList<string> Accounts)
{
    public static ProxyReuseClassification Ok { get; } = new(ProxyReuse.Ok, Array.Empty<string>());
}

public sealed class ProxyLibraryEntry : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _scheme = "socks5";
    private string _host = string.Empty;
    private int _port;
    private string _country = string.Empty;
    private long? _latencyMs;
    private bool? _isWorking;
    private DateTime? _lastFailureUtc;
    private string? _assignedAccount;
    private List<string> _usedByAccounts = new();
    private DateTime _createdAtUtc;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value ?? string.Empty, nameof(Id));
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty, nameof(Name));
    }

    public string Scheme
    {
        get => _scheme;
        set
        {
            var normalized = NormalizeScheme(value);
            if (SetField(ref _scheme, normalized, nameof(Scheme)))
            {
                OnPropertyChanged(nameof(Server));
                OnPropertyChanged(nameof(HostPort));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetField(ref _host, value?.Trim() ?? string.Empty, nameof(Host)))
            {
                OnPropertyChanged(nameof(Server));
                OnPropertyChanged(nameof(HostPort));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            if (SetField(ref _port, value, nameof(Port)))
            {
                OnPropertyChanged(nameof(Server));
                OnPropertyChanged(nameof(HostPort));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string Country
    {
        get => _country;
        set
        {
            if (SetField(ref _country, value ?? string.Empty, nameof(Country)))
            {
                OnPropertyChanged(nameof(CountryDisplay));
            }
        }
    }

    public long? LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (SetField(ref _latencyMs, value, nameof(LatencyMs)))
            {
                OnPropertyChanged(nameof(LatencyText));
            }
        }
    }

    public bool? IsWorking
    {
        get => _isWorking;
        set
        {
            if (SetField(ref _isWorking, value, nameof(IsWorking)))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public DateTime? LastFailureUtc
    {
        get => _lastFailureUtc;
        set => SetField(ref _lastFailureUtc, value, nameof(LastFailureUtc));
    }

    public string? AssignedAccount
    {
        get => _assignedAccount;
        set => SetField(ref _assignedAccount, string.IsNullOrWhiteSpace(value) ? null : value.Trim(), nameof(AssignedAccount));
    }

    public List<string> UsedByAccounts
    {
        get => _usedByAccounts;
        set
        {
            _usedByAccounts = value ?? new List<string>();
            OnPropertyChanged(nameof(UsedByAccounts));
            OnPropertyChanged(nameof(UsedByText));
        }
    }

    public DateTime CreatedAtUtc
    {
        get => _createdAtUtc;
        set => SetField(ref _createdAtUtc, value, nameof(CreatedAtUtc));
    }

    [JsonIgnore]
    public string Server => $"{NormalizeScheme(Scheme)}://{Host.Trim()}:{Port}";

    [JsonIgnore]
    public string HostPort => $"{Host.Trim()}:{Port}";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? HostPort : $"{Name.Trim()} — {HostPort}";

    [JsonIgnore]
    public string UsedByText => string.Join(", ", UsedByAccounts.Where(item => !string.IsNullOrWhiteSpace(item)));

    [JsonIgnore]
    public string LatencyText => LatencyMs is > 0 ? $"{LatencyMs} ms" : string.Empty;

    [JsonIgnore]
    public string CountryDisplay => string.IsNullOrWhiteSpace(Country) ? "Unknown" : Country.Trim();

    [JsonIgnore]
    public string StatusText => IsWorking switch
    {
        true => "Working",
        false => "Failed",
        _ => "Unknown",
    };

    [JsonIgnore]
    public bool IsActive { get; set; }

    [JsonIgnore]
    public string ActiveText => IsActive ? "Active" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static string NormalizeScheme(string? scheme)
    {
        var value = (scheme ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "socks4" => "socks4",
            "socks4a" => "socks4a",
            "http" or "https" => "http",
            _ => "socks5",
        };
    }

    internal ProxyLibraryEntry Clone()
    {
        return new ProxyLibraryEntry
        {
            Id = Id,
            Name = Name,
            Scheme = Scheme,
            Host = Host,
            Port = Port,
            Country = Country,
            LatencyMs = LatencyMs,
            IsWorking = IsWorking,
            LastFailureUtc = LastFailureUtc,
            AssignedAccount = AssignedAccount,
            UsedByAccounts = UsedByAccounts.ToList(),
            CreatedAtUtc = CreatedAtUtc,
        };
    }

    private bool SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ProxyLibraryStore
{
    private static readonly object FileIoLock = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public ProxyLibraryStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(ProjectRootLocator.FindProjectRoot(), "config", "proxies.json")
            : path;
    }

    public List<ProxyLibraryEntry> Load()
    {
        lock (FileIoLock)
        {
            if (!File.Exists(_path))
            {
                return new List<ProxyLibraryEntry>();
            }

            try
            {
                var raw = ReadAllTextWithRetry(_path);
                var entries = JsonSerializer.Deserialize<List<ProxyLibraryEntry>>(raw, SerializerOptions) ?? new List<ProxyLibraryEntry>();
                return NormalizeEntries(entries);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[proxylib] failed to load proxy library: {ex.Message}");
                return new List<ProxyLibraryEntry>();
            }
        }
    }

    public void Save(IEnumerable<ProxyLibraryEntry> entries)
    {
        lock (FileIoLock)
        {
            try
            {
                var normalized = NormalizeEntries(entries);
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(normalized, SerializerOptions));
                Debug.WriteLine($"[proxylib] saved {normalized.Count} proxy entries.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[proxylib] failed to save proxy library: {ex.Message}");
                throw;
            }
        }
    }

    public ProxyLibraryEntry Upsert(ProxyLibraryEntry entry)
    {
        var entries = Load();
        var result = Upsert(entries, entry);
        Save(entries);
        return result;
    }

    public ProxyLibraryEntry? FindByServer(string? server)
    {
        return FindByServer(Load(), server);
    }

    public void AddUsage(string? entryId, string? accountName)
    {
        var entries = Load();
        AddUsage(entries, entryId, accountName);
        Save(entries);
    }

    public ProxyReuseClassification ClassifyReuse(string? server, string? currentAccountName)
    {
        return ClassifyReuse(Load(), server, currentAccountName);
    }

    public static ProxyLibraryEntry Upsert(List<ProxyLibraryEntry> entries, ProxyLibraryEntry entry)
    {
        if (!TryCanonicalize(entry.Server, out var scheme, out var host, out var port))
        {
            throw new InvalidOperationException("Proxy must include host and valid port.");
        }

        var existing = entries.FirstOrDefault(item => SameServer(item, scheme, host, port));
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                existing.Name = entry.Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.Country) && entry.Country != "-")
            {
                existing.Country = entry.Country.Trim();
            }

            if (entry.LatencyMs is > 0)
            {
                existing.LatencyMs = entry.LatencyMs;
            }

            if (entry.IsWorking is not null)
            {
                existing.IsWorking = entry.IsWorking;
            }

            return existing;
        }

        var added = entry.Clone();
        added.Id = string.IsNullOrWhiteSpace(added.Id) ? Guid.NewGuid().ToString("N") : added.Id.Trim();
        added.Name = string.IsNullOrWhiteSpace(added.Name) ? $"{host}:{port}" : added.Name.Trim();
        added.Scheme = scheme;
        added.Host = host;
        added.Port = port;
        added.Country = added.Country == "-" ? string.Empty : added.Country.Trim();
        added.CreatedAtUtc = added.CreatedAtUtc == default ? DateTime.UtcNow : added.CreatedAtUtc;
        added.UsedByAccounts = DistinctAccounts(added.UsedByAccounts);
        entries.Add(added);
        return added;
    }

    public static ProxyLibraryEntry? FindByServer(IEnumerable<ProxyLibraryEntry> entries, string? server)
    {
        return TryCanonicalize(server, out var scheme, out var host, out var port)
            ? entries.FirstOrDefault(item => SameServer(item, scheme, host, port))
            : null;
    }

    public static void AddUsage(List<ProxyLibraryEntry> entries, string? entryId, string? accountName)
    {
        var account = accountName?.Trim();
        if (string.IsNullOrWhiteSpace(entryId) || string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        var entry = entries.FirstOrDefault(item => string.Equals(item.Id, entryId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        if (!entry.UsedByAccounts.Any(item => string.Equals(item, account, StringComparison.OrdinalIgnoreCase)))
        {
            entry.UsedByAccounts.Add(account);
            entry.UsedByAccounts = DistinctAccounts(entry.UsedByAccounts);
            Debug.WriteLine($"[proxylib] marked proxy {entry.Id} used by account '{account}'.");
        }
    }

    public static ProxyReuseClassification ClassifyReuse(IEnumerable<ProxyLibraryEntry> entries, string? server, string? currentAccountName)
    {
        var entry = FindByServer(entries, server);
        if (entry is null)
        {
            return ProxyReuseClassification.Ok;
        }

        var current = currentAccountName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(entry.AssignedAccount)
            && !string.Equals(entry.AssignedAccount, current, StringComparison.OrdinalIgnoreCase))
        {
            return new ProxyReuseClassification(ProxyReuse.LockedToOther, new[] { entry.AssignedAccount! });
        }

        var others = entry.UsedByAccounts
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Where(item => !string.Equals(item.Trim(), current, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return others.Count == 0
            ? ProxyReuseClassification.Ok
            : new ProxyReuseClassification(ProxyReuse.UsedByOthers, others);
    }

    public static bool TryCanonicalize(string? server, out string scheme, out string host, out int port)
    {
        scheme = "socks5";
        host = string.Empty;
        port = 0;

        var value = server?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return false;
        }

        var rest = value;
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            scheme = ProxyLibraryEntry.NormalizeScheme(value[..schemeIndex]);
            rest = value[(schemeIndex + 3)..];
        }

        var atIndex = rest.LastIndexOf('@');
        if (atIndex >= 0)
        {
            rest = rest[(atIndex + 1)..];
        }

        var colonIndex = rest.LastIndexOf(':');
        if (colonIndex <= 0 || colonIndex >= rest.Length - 1)
        {
            return false;
        }

        host = rest[..colonIndex].Trim();
        var portText = rest[(colonIndex + 1)..].Trim();
        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
        {
            return false;
        }

        return true;
    }

    private static List<ProxyLibraryEntry> NormalizeEntries(IEnumerable<ProxyLibraryEntry> entries)
    {
        var result = new List<ProxyLibraryEntry>();
        foreach (var entry in entries)
        {
            if (!TryCanonicalize(entry.Server, out var scheme, out var host, out var port))
            {
                continue;
            }

            entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id.Trim();
            entry.Name = entry.Name?.Trim() ?? string.Empty;
            entry.Scheme = scheme;
            entry.Host = host;
            entry.Port = port;
            entry.Country = entry.Country == "-" ? string.Empty : entry.Country?.Trim() ?? string.Empty;
            entry.LastFailureUtc = entry.LastFailureUtc?.ToUniversalTime();
            entry.AssignedAccount = string.IsNullOrWhiteSpace(entry.AssignedAccount) ? null : entry.AssignedAccount.Trim();
            entry.CreatedAtUtc = entry.CreatedAtUtc == default ? DateTime.UtcNow : entry.CreatedAtUtc;
            entry.UsedByAccounts = DistinctAccounts(entry.UsedByAccounts);
            Upsert(result, entry);
        }

        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool SameServer(ProxyLibraryEntry entry, string scheme, string host, int port)
    {
        return string.Equals(ProxyLibraryEntry.NormalizeScheme(entry.Scheme), scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Host.Trim(), host, StringComparison.OrdinalIgnoreCase)
            && entry.Port == port;
    }

    private static List<string> DistinctAccounts(IEnumerable<string>? accounts)
    {
        return (accounts ?? Array.Empty<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ReadAllTextWithRetry(string path)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
