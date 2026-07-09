using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

/// <summary>
/// Import-and-rank proxy tool. The user pastes a proxy list (one host:port per line), picks the
/// matching type plus how many to test and how many to run in parallel, and the window tests them
/// all with lightweight requests and lists the fastest few. Clicking "Use" returns the chosen proxy
/// to the caller via <see cref="SelectedProxy"/> (the caller applies it). The pasted list, settings
/// and last results are persisted via <see cref="ProxyFinderStateStore"/> so reopening the window
/// restores everything for a quick re-check.
/// </summary>
public partial class ProxyFinderWindow : Window
{
    private const string ProxyListRepoUrl = "https://github.com/TheSpeedX/PROXY-List";
    // Stage 2 reaches this to prove a proxy can actually connect to Travian's CDN. Server-agnostic on
    // purpose (users play on many different game servers) and carries no account data.
    private const string TravianTargetUrl = "https://www.travian.com/";
    // Guard against an accidental paste of a huge dump when "ALL" is selected.
    private const int HardMaxProxies = 20000;

    private readonly ProxyListTester _tester = new(log: message => Debug.WriteLine(message));
    private readonly ProxyLibraryStore _proxyLibraryStore = new();
    private CancellationTokenSource? _operationCts;
    private bool _busy;
    // The rows currently shown; persisted on close so a re-open restores the last results.
    private List<ProxyResultRow> _lastRows = new();

    /// <summary>The proxy the user chose with "Use", or null if the window was closed without a pick.</summary>
    public ProxyCandidate? SelectedProxy { get; private set; }

    public ProxyFinderWindow(string? initialScheme = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        var saved = ProxyFinderStateStore.Load();
        if (saved is not null)
        {
            RestoreState(saved);
        }
        else
        {
            SelectComboByTag(ProtocolComboBox, string.IsNullOrWhiteSpace(initialScheme) ? "socks5" : initialScheme, "socks5");
            SelectComboByTag(ParallelComboBox, "200", "200");
            SelectComboByTag(MaxProxiesComboBox, "2000", "2000");
            SelectComboByTag(TopComboBox, "10", "10");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Keep the pasted list, settings and last results for next time, even on an accidental close.
        SaveState();
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        base.OnClosed(e);
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(TestAndRankAsync, LogUiGuardError);

    private async Task TestAndRankAsync()
    {
        if (_busy)
        {
            return;
        }

        ValidationTextBlock.Text = string.Empty;
        var scheme = ReadComboTag(ProtocolComboBox, "socks5");
        var maxProxies = ReadMaxProxies();
        var candidates = ProxyListTester.ParseCandidates(ProxyListTextBox.Text, scheme, maxProxies);
        if (candidates.Count == 0)
        {
            ValidationTextBlock.Text = "No valid proxies found. Paste one host:port per line.";
            return;
        }

        var maxConcurrency = ReadIntTag(ParallelComboBox, 200);
        var topCount = ReadIntTag(TopComboBox, 10);
        _busy = true;
        SelectedProxy = null;
        ResultsDataGrid.ItemsSource = null;
        ResultsSummaryTextBlock.Text = string.Empty;

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;

        BusyOverlay.ShowCancel = true;
        BusyOverlay.Show("Testing proxies", $"Testing 0 / {candidates.Count}…");

        var liveProgress = new Progress<ProxyTestProgress>(p =>
            BusyOverlay.Text = $"Testing {p.Tested} / {p.Total} — {p.Found} working");

        try
        {
            // Stage 1: quick liveness test against a neutral endpoint. Keep more than we need so
            // stage 2 can drop unreachable ones and still fill the list.
            var poolSize = Math.Max(topCount * 2, 20);
            var live = await Task.Run(
                () => _tester.TestAsync(candidates, maxConcurrency, poolSize, liveProgress, token),
                token);

            // TestAsync returns partial results on cancel rather than throwing, so make the cancel
            // explicit here to route it to the catch instead of showing a misleading result state.
            token.ThrowIfCancellationRequested();

            if (live.Count == 0)
            {
                BusyOverlay.Hide();
                ValidationTextBlock.Text = $"Tested {candidates.Count} proxies but none responded. Try another list or type.";
                ResultsSummaryTextBlock.Text = "0 working";
                return;
            }

            // Stage 2: keep only proxies that can actually reach Travian, which is what the bot needs.
            BusyOverlay.Show("Testing proxies", $"Checking which of {live.Count} can reach travian.com…");
            var reachProgress = new Progress<ProxyTestProgress>(p =>
                BusyOverlay.Text = $"Reaching travian.com {p.Tested} / {p.Total} — {p.Found} ok");
            var reachable = await Task.Run(
                () => _tester.FilterReachableAsync(live, TravianTargetUrl, maxConcurrency, topCount, reachProgress, token),
                token);
            token.ThrowIfCancellationRequested();

            if (reachable.Count == 0)
            {
                BusyOverlay.Hide();
                ValidationTextBlock.Text = $"{live.Count} proxies are alive but none could reach travian.com. Try another list or type.";
                ResultsSummaryTextBlock.Text = "0 usable";
                return;
            }

            BusyOverlay.Show("Testing proxies", $"Looking up locations for the top {reachable.Count}…");
            var rows = await Task.Run(() => BuildRowsAsync(reachable, token), token);

            BusyOverlay.Hide();
            _lastRows = rows;
            RefreshLibraryStateForRows();
            ResultsDataGrid.ItemsSource = rows;
            ResultsSummaryTextBlock.Text = $"Top {rows.Count} of {candidates.Count} tested";
            SaveState();
        }
        catch (OperationCanceledException)
        {
            BusyOverlay.Hide();
            ValidationTextBlock.Text = "Proxy test cancelled.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<List<ProxyResultRow>> BuildRowsAsync(IReadOnlyList<ProxyTestResult> results, CancellationToken token)
    {
        // Enrich the handful of top results in parallel; order is preserved so ranking stays by latency.
        var enrichTasks = results
            .Select(async result => (result, info: await _tester.EnrichAsync(result.Candidate.Server, token)))
            .ToList();
        var enriched = await Task.WhenAll(enrichTasks);

        return enriched
            .Select((item, index) => new ProxyResultRow
            {
                Rank = index + 1,
                Proxy = item.result.Candidate.HostPort,
                LatencyMs = item.result.LatencyMs,
                Latency = $"{item.result.LatencyMs} ms",
                Country = string.IsNullOrWhiteSpace(item.info.Country) ? "-" : item.info.Country,
                Candidate = item.result.Candidate,
            })
            .ToList();
    }

    // Restores the pasted list, settings and last results saved from a previous session.
    private void RestoreState(ProxyFinderState saved)
    {
        SelectComboByTag(ProtocolComboBox, saved.Protocol, "socks5");
        SelectComboByTag(ParallelComboBox, saved.Parallel, "200");
        SelectComboByTag(MaxProxiesComboBox, saved.MaxProxies, "2000");
        SelectComboByTag(TopComboBox, saved.Top, "10");
        ProxyListTextBox.Text = saved.ProxyList;

        if (saved.Results.Count == 0)
        {
            return;
        }

        _lastRows = saved.Results
            .Where(result => !string.IsNullOrWhiteSpace(result.Host) && result.Port is > 0 and <= 65535)
            .Select((result, index) => new ProxyResultRow
            {
                Rank = index + 1,
                Proxy = $"{result.Host}:{result.Port}",
                LatencyMs = result.LatencyMs,
                Latency = $"{result.LatencyMs} ms",
                Country = string.IsNullOrWhiteSpace(result.Country) ? "-" : result.Country,
                Candidate = new ProxyCandidate(
                    string.IsNullOrWhiteSpace(result.Scheme) ? "socks5" : result.Scheme,
                    result.Host,
                    result.Port),
            })
            .ToList();

        ResultsDataGrid.ItemsSource = _lastRows;
        RefreshLibraryStateForRows();
        ResultsSummaryTextBlock.Text = $"Top {_lastRows.Count} (saved)";
    }

    // Persists the current list, settings and last results so nothing is lost on close/restart.
    private void SaveState()
    {
        ProxyFinderStateStore.Save(new ProxyFinderState
        {
            ProxyList = ProxyListTextBox.Text,
            Protocol = ReadComboTag(ProtocolComboBox, "socks5"),
            Parallel = ReadComboTag(ParallelComboBox, "200"),
            MaxProxies = ReadComboTag(MaxProxiesComboBox, "2000"),
            Top = ReadComboTag(TopComboBox, "10"),
            Results = _lastRows
                .Select(row => new ProxyFinderSavedResult
                {
                    Scheme = row.Candidate.Scheme,
                    Host = row.Candidate.Host,
                    Port = row.Candidate.Port,
                    LatencyMs = row.LatencyMs,
                    Country = row.Country,
                })
                .ToList(),
        });
    }

    private void UseProxyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProxyResultRow row })
        {
            return;
        }

        SelectedProxy = row.Candidate;
        DialogResult = true;
        Close();
    }

    private void AddToLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProxyResultRow row })
        {
            return;
        }

        try
        {
            var added = AddMissingRowToLibrary(row);
            row.IsInLibrary = true;
            ResultsDataGrid.Items.Refresh();
            ValidationTextBlock.Text = !added
                ? $"Already in proxy list: {row.Candidate.HostPort}"
                : $"Added to proxy list: {row.Candidate.HostPort}";
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"Could not add proxy: {ex.Message}";
        }
    }

    private void AddAllToLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRows.Count == 0)
        {
            ValidationTextBlock.Text = "No top proxies to add yet.";
            return;
        }

        try
        {
            var added = 0;
            foreach (var row in _lastRows)
            {
                if (AddMissingRowToLibrary(row))
                {
                    added++;
                }

                row.IsInLibrary = true;
            }

            ResultsDataGrid.Items.Refresh();
            ValidationTextBlock.Text = added == 0
                ? "All top proxies are already in the proxy list."
                : $"Added {added} new proxy/proxies to the proxy list.";
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"Could not add proxies: {ex.Message}";
        }
    }

    private void ProxyListButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProxyLibraryWindow(_proxyLibraryStore, LoadAccountNames())
        {
            Owner = this,
        };
        _ = dialog.ShowDialog();
        RefreshLibraryStateForRows();
        ResultsDataGrid.Items.Refresh();
    }

    private bool AddMissingRowToLibrary(ProxyResultRow row)
    {
        if (_proxyLibraryStore.FindByServer(row.Candidate.Server) is not null)
        {
            return false;
        }

        var country = row.Country == "-" ? string.Empty : row.Country;
        var name = string.IsNullOrWhiteSpace(country)
            ? row.Candidate.HostPort
            : $"{country} {row.Candidate.Host}";
        _proxyLibraryStore.Upsert(new ProxyLibraryEntry
        {
            Name = name,
            Scheme = row.Candidate.Scheme,
            Host = row.Candidate.Host,
            Port = row.Candidate.Port,
            Country = country,
            LatencyMs = row.LatencyMs,
            CreatedAtUtc = DateTime.UtcNow,
        });
        return true;
    }

    private void RefreshLibraryStateForRows()
    {
        if (_lastRows.Count == 0)
        {
            return;
        }

        var entries = _proxyLibraryStore.Load();
        foreach (var row in _lastRows)
        {
            row.IsInLibrary = ProxyLibraryStore.FindByServer(entries, row.Candidate.Server) is not null;
        }
    }

    private static IReadOnlyList<string> LoadAccountNames()
    {
        try
        {
            var envPath = Path.Combine(ProjectRootLocator.FindProjectRoot(), ".env");
            return new EnvAccountStore(envPath).ListAccounts()
                .Select(account => account.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void GetListsButton_Click(object sender, RoutedEventArgs e)
        => OpenUrl(ProxyListRepoUrl);

    // Quick-pick a list type: set the paste type to match, then download the raw list into the editor.
    private async void GetListButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var url = tag.ToLowerInvariant() switch
        {
            "socks4" => "https://raw.githubusercontent.com/TheSpeedX/SOCKS-List/master/socks4.txt",
            "http" => "https://raw.githubusercontent.com/TheSpeedX/SOCKS-List/master/http.txt",
            _ => "https://raw.githubusercontent.com/TheSpeedX/SOCKS-List/master/socks5.txt",
        };
        await AsyncUi.GuardAsync(() => LoadProxyListAsync(tag, url), LogUiGuardError);
    }

    private async Task LoadProxyListAsync(string scheme, string url)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        ValidationTextBlock.Text = string.Empty;
        ResultsSummaryTextBlock.Text = string.Empty;
        ResultsDataGrid.ItemsSource = null;
        _lastRows = [];
        SelectedProxy = null;
        SelectComboByTag(ProtocolComboBox, scheme, "socks5");

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TbotUltra/1.0");
            using var response = await client.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(token);
            token.ThrowIfCancellationRequested();

            ProxyListTextBox.Text = text.Replace("\r\n", "\n").Trim();
            var parsed = ProxyListTester.ParseCandidates(ProxyListTextBox.Text, scheme, HardMaxProxies);
            ValidationTextBlock.Text = parsed.Count == 0
                ? $"Downloaded the {scheme.ToUpperInvariant()} list, but no valid host:port proxies were found."
                : $"Loaded {parsed.Count} {scheme.ToUpperInvariant()} proxies. Click Test & rank to check them.";
            SaveState();
        }
        catch (OperationCanceledException)
        {
            ValidationTextBlock.Text = "Proxy list download cancelled.";
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"Could not load proxy list: {SummarizeError(ex.Message)}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"Could not open the link: {ex.Message}";
        }
    }

    private void BusyOverlay_Cancelled(object sender, EventArgs e)
    {
        // The overlay already showed "Cancelling…" and disabled its button; we just cancel the work.
        _operationCts?.Cancel();
    }

    private void ProxyListTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_busy)
        {
            ValidationTextBlock.Text = string.Empty;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private int ReadMaxProxies()
    {
        var tag = ReadComboTag(MaxProxiesComboBox, "2000");
        if (string.Equals(tag, "all", StringComparison.OrdinalIgnoreCase))
        {
            return HardMaxProxies;
        }

        return int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 2000;
    }

    private void LogUiGuardError(string message)
    {
        ValidationTextBlock.Text = message;
    }

    private static string SummarizeError(string? message)
    {
        var value = message ?? string.Empty;
        var firstLine = value.Replace("\r", string.Empty).Split('\n').FirstOrDefault() ?? string.Empty;
        return firstLine.Length == 0 ? "Unknown error." : firstLine;
    }

    private static int ReadIntTag(ComboBox comboBox, int fallback)
    {
        var tag = ReadComboTag(comboBox, fallback.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static string ReadComboTag(ComboBox comboBox, string fallback)
    {
        var tag = (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.IsNullOrWhiteSpace(tag) ? fallback : tag;
    }

    private static void SelectComboByTag(ComboBox comboBox, string tag, string fallbackTag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), fallbackTag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>Row shown in the results grid. Built once after enrichment, so no change notifications.</summary>
    public sealed class ProxyResultRow
    {
        public int Rank { get; init; }
        public string Proxy { get; init; } = string.Empty;
        public long LatencyMs { get; init; }
        public string Latency { get; init; } = string.Empty;
        public string Country { get; init; } = string.Empty;
        public required ProxyCandidate Candidate { get; init; }
        public bool IsInLibrary { get; set; }
    }
}
