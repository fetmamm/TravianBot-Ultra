using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

/// <summary>
/// Import-and-rank proxy tool. The user pastes a proxy list (one host:port per line), picks the
/// matching type plus how many to test and how many to run in parallel, and the window tests them
/// all with lightweight requests and lists the fastest few. Clicking "Use" returns the chosen proxy
/// to the caller via <see cref="SelectedProxy"/>. Nothing is saved here — the caller applies it.
/// </summary>
public partial class ProxyFinderWindow : Window
{
    private const string ProxyListRepoUrl = "https://github.com/TheSpeedX/PROXY-List";
    // Guard against an accidental paste of a huge dump when "ALL" is selected.
    private const int HardMaxProxies = 20000;

    private readonly ProxyListTester _tester = new(log: message => Debug.WriteLine(message));
    private CancellationTokenSource? _operationCts;
    private bool _busy;

    /// <summary>The proxy the user chose with "Use", or null if the window was closed without a pick.</summary>
    public ProxyCandidate? SelectedProxy { get; private set; }

    public ProxyFinderWindow(string? initialScheme = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        SelectComboByTag(ProtocolComboBox, string.IsNullOrWhiteSpace(initialScheme) ? "socks5" : initialScheme, "socks5");
        SelectComboByTag(ParallelComboBox, "100", "100");
        SelectComboByTag(MaxProxiesComboBox, "1000", "1000");
        SelectComboByTag(TopComboBox, "10", "10");
    }

    protected override void OnClosed(EventArgs e)
    {
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

        var maxConcurrency = ReadIntTag(ParallelComboBox, 100);
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

        var progress = new Progress<ProxyTestProgress>(p =>
            BusyOverlay.Text = $"Testing {p.Tested} / {p.Total} — {p.Found} working");

        try
        {
            var results = await Task.Run(
                () => _tester.TestAsync(candidates, maxConcurrency, topCount, progress, token),
                token);

            // TestAsync returns partial results on cancel rather than throwing, so make the cancel
            // explicit here to route it to the catch instead of showing a misleading result state.
            token.ThrowIfCancellationRequested();

            if (results.Count == 0)
            {
                BusyOverlay.Hide();
                ValidationTextBlock.Text = $"Tested {candidates.Count} proxies but none responded. Try another list or type.";
                ResultsSummaryTextBlock.Text = "0 working";
                return;
            }

            BusyOverlay.Show("Testing proxies", $"Looking up locations for the top {results.Count}…");
            var rows = await Task.Run(() => BuildRowsAsync(results, token), token);

            BusyOverlay.Hide();
            ResultsDataGrid.ItemsSource = rows;
            ResultsSummaryTextBlock.Text = $"Top {rows.Count} of {candidates.Count} tested";
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
                Latency = $"{item.result.LatencyMs} ms",
                Country = string.IsNullOrWhiteSpace(item.info.Country) ? "-" : item.info.Country,
                Candidate = item.result.Candidate,
            })
            .ToList();
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

    private void GetListsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ProxyListRepoUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"Could not open the proxy list page: {ex.Message}";
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
        var tag = ReadComboTag(MaxProxiesComboBox, "500");
        if (string.Equals(tag, "all", StringComparison.OrdinalIgnoreCase))
        {
            return HardMaxProxies;
        }

        return int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 500;
    }

    private void LogUiGuardError(string message)
    {
        ValidationTextBlock.Text = message;
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
        public string Latency { get; init; } = string.Empty;
        public string Country { get; init; } = string.Empty;
        public required ProxyCandidate Candidate { get; init; }
    }
}
