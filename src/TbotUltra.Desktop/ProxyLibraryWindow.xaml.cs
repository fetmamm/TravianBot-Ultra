using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

public partial class ProxyLibraryWindow : Window
{
    private readonly ProxyLibraryStore _store;
    private readonly ProxyListTester _tester = new(log: message => System.Diagnostics.Debug.WriteLine(message));
    private readonly string _activeProxyServer;
    private readonly string? _activeAccountName;
    private CancellationTokenSource? _checkCts;
    private List<ProxyLibraryEntry> _workingProxies = [];
    private string _savedSnapshot = string.Empty;
    private bool _isClosing;

    public IReadOnlyList<string> AccountChoices { get; }

    public ProxyLibraryWindow(
        ProxyLibraryStore store,
        IEnumerable<string> accountNames,
        string? activeProxyServer = null,
        string? activeAccountName = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        DataContext = this;
        _store = store;
        _activeProxyServer = activeProxyServer?.Trim() ?? string.Empty;
        _activeAccountName = activeAccountName;
        AccountChoices = new[] { string.Empty }
            .Concat(accountNames.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Length == 0 ? string.Empty : item, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ReloadWorkingCopy(_store.Load());
        _savedSnapshot = BuildSnapshot();
        UpdateActiveProxyText(_activeAccountName);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidate(out var validated))
        {
            return;
        }

        _store.Save(validated);
        _savedSnapshot = BuildSnapshot(validated);
        _isClosing = true;
        DialogResult = true;
        Close();
    }

    private void AddProxyButton_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Text = "New proxy", MinWidth = 260 };
        var schemeBox = new ComboBox { Width = 100, SelectedValuePath = "Tag" };
        schemeBox.Items.Add(new ComboBoxItem { Content = "SOCKS5", Tag = "socks5" });
        schemeBox.Items.Add(new ComboBoxItem { Content = "SOCKS4", Tag = "socks4" });
        schemeBox.Items.Add(new ComboBoxItem { Content = "HTTP", Tag = "http" });
        schemeBox.SelectedIndex = 0;
        var hostBox = new TextBox { MinWidth = 260 };
        var portBox = new TextBox { MinWidth = 90 };

        var panel = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
        {
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddDialogRow(panel, 0, "Name", nameBox);
        AddDialogRow(panel, 1, "Type", schemeBox);
        AddDialogRow(panel, 2, "Host/IP", hostBox);
        AddDialogRow(panel, 3, "Port", portBox);

        var result = AppDialog.ShowContent(this, panel, "Add proxy", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var scheme = (schemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "socks5";
        var host = hostBox.Text.Trim();
        if (!int.TryParse(portBox.Text.Trim(), out var port) || port is < 1 or > 65535 || host.Length == 0 || host.Any(char.IsWhiteSpace))
        {
            AppDialog.Show(this, "Enter a valid host/IP and port.", "Add proxy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var entry = ProxyLibraryStore.Upsert(_workingProxies, new ProxyLibraryEntry
        {
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? $"{host}:{port}" : nameBox.Text.Trim(),
            Scheme = scheme,
            Host = host,
            Port = port,
            CreatedAtUtc = DateTime.UtcNow,
        });

        ProxyDataGrid.Items.Refresh();
        ProxyDataGrid.SelectedItem = entry;
        ProxyDataGrid.ScrollIntoView(entry);
    }

    private void DeleteRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProxyLibraryEntry entry })
        {
            return;
        }

        _workingProxies.Remove(entry);
        ProxyDataGrid.Items.Refresh();
    }

    private void DeleteAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workingProxies.Count == 0)
        {
            return;
        }

        var result = AppDialog.ShowCustom(
            this,
            $"Delete all {_workingProxies.Count} saved proxies? This cannot be undone after saving.",
            "Delete all proxies",
            [("Delete all", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _workingProxies.Clear();
        ProxyDataGrid.Items.Refresh();
        UpdateActiveProxyText(_activeAccountName);
    }

    private void DeleteFailedProxiesButton_Click(object sender, RoutedEventArgs e)
    {
        var failed = _workingProxies.Where(entry => entry.IsWorking == false).ToList();
        if (failed.Count == 0)
        {
            AppDialog.Show(this, "No failed proxies to delete.", "Delete failed proxies", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = AppDialog.ShowCustom(
            this,
            $"Delete all {failed.Count} failed proxies? This cannot be undone after saving.",
            "Delete failed proxies",
            [("Delete failed", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var entry in failed)
        {
            _workingProxies.Remove(entry);
        }

        ProxyDataGrid.Items.Refresh();
        UpdateActiveProxyText(_activeAccountName);
    }

    private async void CheckProxiesButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(CheckProxiesAsync, message => System.Diagnostics.Debug.WriteLine(message));

    private async Task CheckProxiesAsync()
    {
        if (_workingProxies.Count == 0)
        {
            AppDialog.Show(this, "Proxy list is empty.", "Check proxies", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();
        var token = _checkCts.Token;
        BusyOverlay.ShowCancel = true;
        BusyOverlay.Show("Checking proxies", $"Testing 0 / {_workingProxies.Count}...");

        try
        {
            var candidates = _workingProxies
                .Select(entry => new ProxyCandidate(entry.Scheme, entry.Host, entry.Port))
                .ToList();
            var progress = new Progress<ProxyTestProgress>(p =>
                BusyOverlay.Text = $"Testing {p.Tested} / {p.Total} - {p.Found} working");
            var results = await _tester.TestAsync(candidates, maxConcurrency: 100, topCount: candidates.Count, progress, token);
            token.ThrowIfCancellationRequested();

            var resultByServer = results.ToDictionary(item => item.Candidate.Server, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _workingProxies)
            {
                if (resultByServer.TryGetValue(entry.Server, out var result))
                {
                    entry.IsWorking = true;
                    entry.LatencyMs = result.LatencyMs;
                }
                else
                {
                    entry.IsWorking = false;
                    entry.LatencyMs = null;
                }
            }

            BusyOverlay.Show("Checking proxies", $"Updating locations for {results.Count} working proxies...");
            var enrichTasks = _workingProxies
                .Where(entry => entry.IsWorking == true)
                .Select(async entry => (entry, info: await _tester.EnrichAsync(entry.Server, token)))
                .ToList();
            var enriched = await Task.WhenAll(enrichTasks);
            foreach (var (entry, info) in enriched)
            {
                if (!string.IsNullOrWhiteSpace(info.Country))
                {
                    entry.Country = info.Country;
                }
            }

            ApplyActiveState();
            ProxyDataGrid.Items.Refresh();
            _store.Save(_workingProxies);
            _savedSnapshot = BuildSnapshot();
            BusyOverlay.Hide();
            AppDialog.Show(
                this,
                $"Checked {_workingProxies.Count} proxies. {results.Count} working.",
                "Check proxies",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            BusyOverlay.Hide();
            AppDialog.Show(this, "Proxy check cancelled.", "Check proxies", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            BusyOverlay.Hide();
            AppDialog.Show(this, $"Could not check proxies: {ex.Message}", "Check proxies", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BusyOverlay.Hide();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PromptToSaveUnsavedChanges())
        {
            return;
        }

        _isClosing = true;
        DialogResult = false;
        Close();
    }

    private void ProxyLibraryWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (!PromptToSaveUnsavedChanges())
        {
            e.Cancel = true;
            return;
        }

        _isClosing = true;
        _checkCts?.Cancel();
    }

    private void ReloadWorkingCopy(IEnumerable<ProxyLibraryEntry> source)
    {
        _workingProxies = source
            .Select(item => item.Clone())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyActiveState();
        ProxyDataGrid.ItemsSource = _workingProxies;
    }

    private bool TryValidate(out List<ProxyLibraryEntry> validated)
    {
        validated = new List<ProxyLibraryEntry>();
        foreach (var item in _workingProxies)
        {
            var name = item.Name.Trim();
            if (name.Length == 0)
            {
                AppDialog.Show(this, "Proxy name cannot be empty.", "Save proxy list", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!ProxyLibraryStore.TryCanonicalize(item.Server, out var scheme, out var host, out var port))
            {
                AppDialog.Show(this, $"Invalid proxy for '{name}'.", "Save proxy list", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            validated.Add(new ProxyLibraryEntry
            {
                Id = item.Id,
                Name = name,
                Scheme = scheme,
                Host = host,
                Port = port,
                Country = item.Country.Trim(),
                LatencyMs = item.LatencyMs,
                IsWorking = item.IsWorking,
                AssignedAccount = item.AssignedAccount,
                UsedByAccounts = item.UsedByAccounts.ToList(),
                CreatedAtUtc = item.CreatedAtUtc,
            });
        }

        return true;
    }

    private void BusyOverlay_Cancelled(object sender, EventArgs e)
    {
        _checkCts?.Cancel();
    }

    private void ApplyActiveState()
    {
        foreach (var entry in _workingProxies)
        {
            entry.IsActive = !string.IsNullOrWhiteSpace(_activeProxyServer)
                && ProxyLibraryStore.FindByServer(new[] { entry }, _activeProxyServer) is not null;
        }
    }

    private void UpdateActiveProxyText(string? activeAccountName)
    {
        if (string.IsNullOrWhiteSpace(_activeProxyServer))
        {
            ActiveProxyTextBlock.Text = "Active proxy: none for this account.";
            return;
        }

        var match = ProxyLibraryStore.FindByServer(_workingProxies, _activeProxyServer);
        var accountText = string.IsNullOrWhiteSpace(activeAccountName) ? string.Empty : $" for {activeAccountName}";
        ActiveProxyTextBlock.Text = match is null
            ? $"Active proxy{accountText}: {ProxyParser.MaskForLog(_activeProxyServer)} (not in saved list)"
            : $"Active proxy{accountText}: {match.DisplayName}";
    }

    private bool PromptToSaveUnsavedChanges()
    {
        if (!HasUnsavedChanges())
        {
            return true;
        }

        var result = AppDialog.ShowCustom(
            this,
            "You have unsaved proxy list changes.\n\nSave: Save changes\nDiscard: Discard changes\nCancel: Stay on this window",
            "Unsaved changes",
            [("Save", MessageBoxResult.Yes), ("Discard", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.No)
        {
            return true;
        }

        if (result == MessageBoxResult.Yes)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            return DialogResult == true;
        }

        return false;
    }

    private bool HasUnsavedChanges()
    {
        return !string.Equals(_savedSnapshot, BuildSnapshot(), StringComparison.Ordinal);
    }

    private string BuildSnapshot()
    {
        return BuildSnapshot(_workingProxies);
    }

    private static string BuildSnapshot(IEnumerable<ProxyLibraryEntry> entries)
    {
        return string.Join(
            "|",
            entries
                .OrderBy(item => item.Server, StringComparer.OrdinalIgnoreCase)
                .Select(item => string.Join(
                    "::",
                    item.Id,
                    item.Name.Trim(),
                    item.Server,
                    item.Country.Trim(),
                    item.LatencyMs?.ToString() ?? string.Empty,
                    item.IsWorking?.ToString() ?? string.Empty,
                    item.AssignedAccount ?? string.Empty,
                    string.Join(",", item.UsedByAccounts.OrderBy(account => account, StringComparer.OrdinalIgnoreCase)))));
    }

    private static void AddDialogRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 10, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        control.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
    }
}
