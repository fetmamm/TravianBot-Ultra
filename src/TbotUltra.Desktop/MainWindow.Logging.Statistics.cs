using System.Text;
using System.Windows;
using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services.Logging;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private readonly DateTimeOffset _browserStatisticsProgramSessionStartedUtc = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, BrowserActivityStatisticsSnapshot> _browserSessionStatistics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BrowserActivityStatisticsSnapshot> _browserLifetimeStatistics = new(StringComparer.OrdinalIgnoreCase);

    private bool TryRecordBrowserActivityStatistics(string line)
    {
        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return false;
        }

        var session = GetBrowserSessionStatistics(accountName);
        var lifetime = GetBrowserLifetimeStatistics(accountName);
        var nowUtc = DateTimeOffset.UtcNow;
        var sessionChanged = BrowserActivityStatisticsParser.TryApply(line, session, nowUtc);
        var lifetimeChanged = BrowserActivityStatisticsParser.TryApply(line, lifetime, nowUtc);
        return sessionChanged || lifetimeChanged;
    }

    private void PersistAndRefreshBrowserActivityStatistics()
    {
        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        try
        {
            BrowserActivityStatisticsStore.Save(
                _projectRoot,
                accountName,
                GetBrowserLifetimeStatistics(accountName));
        }
        catch (Exception ex)
        {
            AppendLog($"[browser-statistics] could not save lifetime statistics: {ex.Message}");
        }

        RefreshBrowserStatisticsUi();
    }

    private BrowserActivityStatisticsSnapshot GetBrowserSessionStatistics(string accountName)
    {
        var key = AccountStoragePaths.NormalizeAccountKey(accountName);
        if (_browserSessionStatistics.TryGetValue(key, out var snapshot))
        {
            return snapshot;
        }

        snapshot = new BrowserActivityStatisticsSnapshot
        {
            FirstRecordedUtc = _browserStatisticsProgramSessionStartedUtc,
        };
        _browserSessionStatistics[key] = snapshot;
        return snapshot;
    }

    private BrowserActivityStatisticsSnapshot GetBrowserLifetimeStatistics(string accountName)
    {
        var key = AccountStoragePaths.NormalizeAccountKey(accountName);
        if (_browserLifetimeStatistics.TryGetValue(key, out var snapshot))
        {
            return snapshot;
        }

        snapshot = BrowserActivityStatisticsStore.Load(_projectRoot, accountName);
        _browserLifetimeStatistics[key] = snapshot;
        return snapshot;
    }

    private void RefreshBrowserStatisticsUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshBrowserStatisticsUi);
            return;
        }

        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            BrowserStatisticsAccountTextBlock.Text = "Browser statistics — no active account";
            BrowserStatisticsPeriodTextBlock.Text = "Login or select an account to collect statistics.";
            BrowserStatisticsSummaryDataGrid.ItemsSource = Array.Empty<BrowserActivityStatisticsRow>();
            BrowserStatisticsDestinationDataGrid.ItemsSource = Array.Empty<BrowserDestinationStatisticsRow>();
            return;
        }

        var session = GetBrowserSessionStatistics(accountName);
        var lifetime = GetBrowserLifetimeStatistics(accountName);
        BrowserStatisticsAccountTextBlock.Text = $"Browser statistics — {accountName}";
        BrowserStatisticsPeriodTextBlock.Text =
            $"Session since {FormatStatisticsDate(session.FirstRecordedUtc)} · " +
            $"Lifetime since {FormatStatisticsDate(lifetime.FirstRecordedUtc)} · " +
            "Trace rows are added when Detailed browser logging is enabled.";

        BrowserStatisticsSummaryDataGrid.ItemsSource = BrowserActivityStatisticsParser.MetricOrder
            .Select(metric => new BrowserActivityStatisticsRow(
                metric,
                FormatStatisticsMetric(metric, session.Metric(metric)),
                FormatStatisticsMetric(metric, lifetime.Metric(metric))))
            .ToList();

        var destinations = session.Destinations.Keys
            .Concat(lifetime.Destinations.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(destination =>
            {
                session.Destinations.TryGetValue(destination, out var sessionCounters);
                lifetime.Destinations.TryGetValue(destination, out var lifetimeCounters);
                return new BrowserDestinationStatisticsRow(
                    destination,
                    sessionCounters?.Navigations ?? 0,
                    sessionCounters?.Reloads ?? 0,
                    lifetimeCounters?.Navigations ?? 0,
                    lifetimeCounters?.Reloads ?? 0);
            })
            .OrderByDescending(row => row.SessionNavigations + row.SessionReloads)
            .ThenByDescending(row => row.LifetimeNavigations + row.LifetimeReloads)
            .ThenBy(row => row.Destination, StringComparer.OrdinalIgnoreCase)
            .ToList();
        BrowserStatisticsDestinationDataGrid.ItemsSource = destinations;

        ClearBrowserStatisticsSessionButton.IsEnabled = HasBrowserStatistics(session);
        ClearBrowserStatisticsLifetimeButton.IsEnabled = HasBrowserStatistics(lifetime);
        if (_logsPopupStatisticsTextBox is not null)
        {
            _logsPopupStatisticsTextBox.Text = BuildBrowserStatisticsReport();
        }
    }

    private void ClearBrowserStatisticsSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        _browserSessionStatistics[AccountStoragePaths.NormalizeAccountKey(accountName)] = new BrowserActivityStatisticsSnapshot
        {
            FirstRecordedUtc = DateTimeOffset.UtcNow,
        };
        RefreshBrowserStatisticsUi();
        StatusTextBlock.Text = $"Session browser statistics cleared for {accountName}.";
    }

    private void ClearBrowserStatisticsLifetimeButton_Click(object sender, RoutedEventArgs e)
    {
        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var confirm = AppDialog.Show(
            this,
            $"Clear all lifetime browser statistics for '{accountName}'? Session statistics are kept.",
            "Clear lifetime browser statistics?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var empty = new BrowserActivityStatisticsSnapshot();
        _browserLifetimeStatistics[AccountStoragePaths.NormalizeAccountKey(accountName)] = empty;
        BrowserActivityStatisticsStore.Clear(_projectRoot, accountName);
        RefreshBrowserStatisticsUi();
        StatusTextBlock.Text = $"Lifetime browser statistics cleared for {accountName}.";
    }

    private string BuildBrowserStatisticsReport()
    {
        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return "No active account.";
        }

        var session = GetBrowserSessionStatistics(accountName);
        var lifetime = GetBrowserLifetimeStatistics(accountName);
        var builder = new StringBuilder();
        builder.AppendLine($"Browser statistics — {accountName}");
        builder.AppendLine($"Session since {FormatStatisticsDate(session.FirstRecordedUtc)} | Lifetime since {FormatStatisticsDate(lifetime.FirstRecordedUtc)}");
        builder.AppendLine();
        builder.AppendLine("Metric\tSession\tLifetime");
        foreach (var metric in BrowserActivityStatisticsParser.MetricOrder)
        {
            builder.AppendLine($"{metric}\t{FormatStatisticsMetric(metric, session.Metric(metric))}\t{FormatStatisticsMetric(metric, lifetime.Metric(metric))}");
        }

        builder.AppendLine();
        builder.AppendLine("Destination\tSession nav\tSession reload\tLifetime nav\tLifetime reload");
        foreach (var destination in session.Destinations.Keys
                     .Concat(lifetime.Destinations.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            session.Destinations.TryGetValue(destination, out var sessionCounters);
            lifetime.Destinations.TryGetValue(destination, out var lifetimeCounters);
            builder.AppendLine(
                $"{destination}\t{sessionCounters?.Navigations ?? 0}\t{sessionCounters?.Reloads ?? 0}\t" +
                $"{lifetimeCounters?.Navigations ?? 0}\t{lifetimeCounters?.Reloads ?? 0}");
        }

        return builder.ToString().TrimEnd();
    }

    private static bool HasBrowserStatistics(BrowserActivityStatisticsSnapshot snapshot)
        => snapshot.Metrics.Values.Any(value => value > 0)
           || snapshot.Destinations.Values.Any(value => value.Navigations > 0 || value.Reloads > 0);

    private static string FormatStatisticsMetric(string metric, long value)
        => string.Equals(metric, BrowserActivityStatisticsParser.WaitMilliseconds, StringComparison.Ordinal)
            ? TimeSpan.FromMilliseconds(Math.Max(0, value)).ToString(@"d\.hh\:mm\:ss")
            : Math.Max(0, value).ToString("N0");

    private static string FormatStatisticsDate(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "not recorded";
}
