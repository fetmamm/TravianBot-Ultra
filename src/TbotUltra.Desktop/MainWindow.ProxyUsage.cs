using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan ProxyUsageCheckpointInterval = TimeSpan.FromMinutes(1);

    private string? _proxyUsageAccountName;
    private ProxyUsageIdentity? _proxyUsageIdentity;
    private DateTimeOffset _proxyUsageCheckpointUtc;
    private bool _proxyUsageSessionPending;

    private void UpdateProxyUsageState(
        string accountName,
        bool isOnline,
        DateTimeOffset nowUtc,
        bool forcePersist)
    {
        if (!isOnline || string.IsNullOrWhiteSpace(accountName))
        {
            CloseCurrentProxyUsageInterval(nowUtc);
            return;
        }

        var identity = ResolveCurrentProxyUsageIdentity(accountName);
        if (_proxyUsageIdentity is null)
        {
            StartProxyUsageInterval(accountName, identity, nowUtc);
            return;
        }

        // A direct lookup normally completes a few seconds after tracking starts. Upgrade the pending
        // identity in place so those seconds do not create a separate "Unknown" row.
        if (string.Equals(_proxyUsageAccountName, accountName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_proxyUsageIdentity.ConnectionType, "Direct", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(_proxyUsageIdentity.ExitIp)
            && !string.IsNullOrWhiteSpace(identity.ExitIp))
        {
            _proxyUsageIdentity = identity;
        }
        else if (!string.Equals(_proxyUsageAccountName, accountName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_proxyUsageIdentity.Key, identity.Key, StringComparison.OrdinalIgnoreCase))
        {
            CloseCurrentProxyUsageInterval(nowUtc);
            StartProxyUsageInterval(accountName, identity, nowUtc);
            return;
        }
        else
        {
            // Refresh exit-IP/country metadata without splitting an otherwise identical proxy interval.
            _proxyUsageIdentity = identity;
        }

        if (forcePersist || nowUtc - _proxyUsageCheckpointUtc >= ProxyUsageCheckpointInterval)
        {
            FlushCurrentProxyUsageInterval(nowUtc);
        }
    }

    private void StartProxyUsageInterval(string accountName, ProxyUsageIdentity identity, DateTimeOffset nowUtc)
    {
        _proxyUsageAccountName = accountName;
        _proxyUsageIdentity = identity;
        _proxyUsageCheckpointUtc = nowUtc;
        _proxyUsageSessionPending = true;
        var connection = string.IsNullOrWhiteSpace(identity.ProxyEndpoint)
            ? identity.ConnectionType
            : $"{identity.ConnectionType} {identity.ProxyEndpoint}";
        AppendLog($"[proxy-usage:verbose] tracking started for account '{accountName}' via {connection}.");
    }

    private void CloseCurrentProxyUsageInterval(DateTimeOffset nowUtc)
    {
        FlushCurrentProxyUsageInterval(nowUtc);
        _proxyUsageAccountName = null;
        _proxyUsageIdentity = null;
        _proxyUsageCheckpointUtc = default;
        _proxyUsageSessionPending = false;
    }

    private void FlushCurrentProxyUsageInterval(DateTimeOffset nowUtc)
    {
        if (_proxyUsageIdentity is null
            || string.IsNullOrWhiteSpace(_proxyUsageAccountName)
            || _proxyUsageCheckpointUtc == default
            || nowUtc <= _proxyUsageCheckpointUtc)
        {
            return;
        }

        try
        {
            ProxyUsageStore.RecordUsage(
                _projectRoot,
                _proxyUsageAccountName,
                _proxyUsageIdentity,
                _proxyUsageCheckpointUtc,
                nowUtc,
                _proxyUsageSessionPending);
            _proxyUsageCheckpointUtc = nowUtc;
            _proxyUsageSessionPending = false;
        }
        catch (Exception ex)
        {
            AppendLog($"[proxy-usage] could not save usage: {ex.Message}");
        }
    }

    private ProxyUsageIdentity ResolveCurrentProxyUsageIdentity(string accountName)
    {
        var account = string.Equals(_uiActiveAccountName, accountName, StringComparison.OrdinalIgnoreCase)
            ? _uiActiveAccount
            : FindAccount(accountName);
        if (account?.ProxyEnabled == true
            && ProxyLibraryStore.TryCanonicalize(account.ProxyServer, out var scheme, out var host, out var port))
        {
            var lookupMatches = string.Equals(
                account.ProxyServer.Trim(),
                _proxyStatusLookupKey,
                StringComparison.OrdinalIgnoreCase);
            return new ProxyUsageIdentity(
                $"proxy:{scheme}:{host.ToLowerInvariant()}:{port}",
                "Proxy",
                $"{host}:{port}",
                lookupMatches ? _proxyStatusIp : string.Empty,
                lookupMatches ? _proxyStatusCountry : string.Empty);
        }

        var directLookupMatches = string.Equals(
            _proxyStatusLookupKey,
            DirectConnectionLookupKey,
            StringComparison.OrdinalIgnoreCase);
        var exitIp = directLookupMatches ? _proxyStatusIp : string.Empty;
        var key = string.IsNullOrWhiteSpace(exitIp)
            ? "direct:unknown"
            : $"direct:{exitIp.ToLowerInvariant()}";
        return new ProxyUsageIdentity(
            key,
            "Direct",
            string.Empty,
            exitIp,
            directLookupMatches ? _proxyStatusCountry : string.Empty);
    }

    private IReadOnlyList<DailyProxyUsageRow> BuildDailyProxyUsageRows(string accountName)
    {
        var records = ProxyUsageStore.Load(_projectRoot, accountName);
        if (records.Count == 0)
        {
            return
            [
                new DailyProxyUsageRow("-", "No usage recorded yet", "-", "-", "0h00min", "-", 0, "-", "-"),
            ];
        }

        var totalSeconds = records.Sum(record => Math.Max(0, record.TotalSeconds));
        var activeKey = string.Equals(_proxyUsageAccountName, accountName, StringComparison.OrdinalIgnoreCase)
            ? _proxyUsageIdentity?.Key
            : null;

        return records
            .OrderByDescending(record => string.Equals(record.Key, activeKey, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(record => record.TotalSeconds)
            .Select(record => new DailyProxyUsageRow(
                string.Equals(record.Key, activeKey, StringComparison.OrdinalIgnoreCase) ? "Active" : "Previous",
                ResolveProxyUsageIp(record),
                string.Equals(record.ConnectionType, "Proxy", StringComparison.Ordinal)
                    ? record.ProxyEndpoint
                    : "Direct connection",
                string.IsNullOrWhiteSpace(record.Country) ? "Unknown" : record.Country,
                FormatDailyDetailsDuration(TimeSpan.FromSeconds(record.TotalSeconds)),
                totalSeconds <= 0 ? "-" : $"{record.TotalSeconds / totalSeconds * 100:0}%",
                record.SessionCount,
                FormatProxyUsageTimestamp(record.FirstUsedUtc),
                FormatProxyUsageTimestamp(record.LastUsedUtc)))
            .ToList();
    }

    private static string ResolveProxyUsageIp(ProxyUsageRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ExitIp))
        {
            return record.ExitIp;
        }

        if (string.Equals(record.ConnectionType, "Proxy", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(record.ProxyEndpoint))
        {
            var separator = record.ProxyEndpoint.LastIndexOf(':');
            return separator > 0 ? record.ProxyEndpoint[..separator] : record.ProxyEndpoint;
        }

        return "Unknown";
    }

    private static string FormatProxyUsageTimestamp(DateTimeOffset value) =>
        value == default ? "-" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
