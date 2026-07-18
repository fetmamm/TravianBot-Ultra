using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

public partial class AccountsWindow : Window
{
    private readonly EnvAccountStore _store;
    private readonly AccountDeletionService _deletionService;
    private readonly ServerCatalogStore _serverCatalogStore;
    private readonly ProxyLibraryStore _proxyLibraryStore = new();
    private readonly string _projectRoot;
    private readonly AccountProxyPlanStore _proxyPlanStore;
    private readonly BotConfigStore _botConfigStore;
    private readonly string _defaultServerName;
    private readonly string _defaultServerUrl;
    private readonly List<ServerOption> _serverOptions;
    private readonly List<ServerOption> _defaultServerOptions;

    private List<AccountEntry> _accounts = [];
    private List<ProxyLibraryEntry> _proxyLibraryEntries = [];
    private List<SavedProxyOption> _savedProxyOptions = [];
    // Custom servers + built-in official servers, in combo display order. The combo's
    // ItemsSource is a grouped view over this list, so selection lookups go through it.
    private List<ServerOption> _comboServers = [];
    private List<ServerOption> _specialServerOptions = [];
    private readonly CancellationTokenSource _specialServerLoadCts = new();
    private string _activeAccountName = string.Empty;
    private bool _showPassword;
    private bool _editingExistingAccount;
    private string _editingOriginalName = string.Empty;
    private string _editingOriginalServerName = string.Empty;
    private string _selectedAccountName = string.Empty;
    private AccountEditorSnapshot _baselineEditorState = new(string.Empty, string.Empty, string.Empty, false, string.Empty, false);
    private bool _suppressSelectionChanged;
    private bool _suppressSavedProxySelection;
    private bool _isClosing;
    private CancellationTokenSource? _proxyCheckCts;
    private bool _proxyCheckCompleted;
    private bool _suppressProxyRotationChange;
    private AccountProxyPlan _editorProxyPlan = new();
    private string _baselineProxyPlanJson = string.Empty;

    public AccountsWindow(
        EnvAccountStore store,
        AccountDeletionService deletionService,
        ServerCatalogStore serverCatalogStore,
        string defaultServerName,
        string defaultServerUrl,
        IEnumerable<ServerOption> serverOptions,
        IEnumerable<ServerOption> defaultServerOptions)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _store = store;
        _deletionService = deletionService;
        _serverCatalogStore = serverCatalogStore;
        _defaultServerName = defaultServerName;
        _defaultServerUrl = defaultServerUrl;
        _serverOptions = serverOptions.ToList();
        _defaultServerOptions = defaultServerOptions.ToList();
        _projectRoot = ProjectRootLocator.FindProjectRoot();
        _proxyPlanStore = new AccountProxyPlanStore(_projectRoot);
        _botConfigStore = new BotConfigStore(System.IO.Path.Combine(_projectRoot, "config", "bot.json"), _projectRoot, () => _store.ActiveAccountName());
        SafeRunAccountEditorAction(() => SelectProxyScheme("socks5"), "initialize proxy type");
        Loaded += AccountsWindow_Loaded;
        Closed += (_, _) => _specialServerLoadCts.Cancel();
        Reload();
    }

    private async void AccountsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AccountsWindow_Loaded;
        try
        {
            _specialServerOptions = await OfficialServerDiscoveryService.FetchSpecialServersAsync(_specialServerLoadCts.Token);
            var selectedName = (ServerComboBox.SelectedItem as ServerOption)?.Name ?? string.Empty;
            var selectedUrl = (ServerComboBox.SelectedItem as ServerOption)?.BaseUrl ?? string.Empty;
            EnsureServerListContainsDefaults();
            if (selectedName.Length > 0 || selectedUrl.Length > 0)
            {
                SelectServer(selectedName, selectedUrl);
            }
        }
        catch (OperationCanceledException) when (_specialServerLoadCts.IsCancellationRequested)
        {
            // Window closed while the calendar request was running.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[official-server-discovery] Could not refresh special gameworlds: {ex.Message}");
        }
    }

    private void Reload()
    {
        ReconcileAccountServerNames();
        _activeAccountName = _store.ActiveAccountName();
        _accounts = _store.ListAccounts()
            .OrderByDescending(account => string.Equals(account.Name, _activeAccountName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(account => account.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var account in _accounts)
        {
            account.IsActive = string.Equals(account.Name, _activeAccountName, StringComparison.OrdinalIgnoreCase);
        }

        EnsureServerListContainsDefaults();
        ReloadProxyLibraryEntries();

        AccountsListBox.ItemsSource = null;
        AccountsListBox.ItemsSource = _accounts;

        if (_accounts.Count > 0)
        {
            var selected = _accounts.FirstOrDefault(a => a.IsActive) ?? _accounts[0];
            AccountsListBox.SelectedItem = selected;
        }
        else
        {
            ClearEditor();
        }

        var activeAccount = _accounts.FirstOrDefault(a => a.IsActive);
        InfoTextBlock.Text = activeAccount is null
            ? $"Active account: {_activeAccountName}"
            : $"Active account: {activeAccount.Username} @ {(string.IsNullOrWhiteSpace(activeAccount.ServerName) ? activeAccount.ServerUrl : activeAccount.ServerName)}";
        UpdateActionButtons();
    }

    private void AccountsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (AccountsListBox.SelectedItem is not AccountEntry selected)
        {
            return;
        }

        var nextName = selected.Name;
        if (_selectedAccountName.Length > 0 && !string.Equals(_selectedAccountName, nextName, StringComparison.OrdinalIgnoreCase))
        {
            if (!PromptToSaveUnsavedChanges())
            {
                _suppressSelectionChanged = true;
                AccountsListBox.SelectedItem = _accounts.FirstOrDefault(item =>
                    string.Equals(item.Name, _selectedAccountName, StringComparison.OrdinalIgnoreCase));
                _suppressSelectionChanged = false;
                return;
            }
        }

        _selectedAccountName = nextName;
        _editingExistingAccount = true;
        _editingOriginalName = selected.Name;
        _editingOriginalServerName = string.IsNullOrWhiteSpace(selected.ServerName) ? _defaultServerName : selected.ServerName;

        UsernameTextBox.Text = selected.Username;
        PasswordBox.Password = selected.Password;
        PasswordTextBox.Text = selected.Password;
        // Set the proxy fields before the InfoTextBlock assignment below so the checkbox handler's
        // hint does not overwrite the "Editing existing account" message.
        UseProxyCheckBox.IsChecked = selected.ProxyEnabled;
        NeverUseOwnIpCheckBox.IsChecked = selected.NeverUseOwnIp;
        SafeRunAccountEditorAction(() =>
        {
            LoadProxyFields(selected.ProxyServer);
            RefreshSavedProxySelection();
            SetProxyFieldsEnabled(selected.ProxyEnabled);
        }, "load proxy fields");
        _editorProxyPlan = _proxyPlanStore.LoadActive(selected.Name)
            ?? _proxyPlanStore.BuildLegacyPlan(selected.ProxyServer, _proxyLibraryEntries);
        if (_editorProxyPlan.Enabled && UseProxyCheckBox.IsChecked != true)
        {
            UseProxyCheckBox.IsChecked = true;
            SetProxyFieldsEnabled(true);
        }
        _suppressProxyRotationChange = true;
        UseProxyRotationCheckBox.IsChecked = _editorProxyPlan.IsRotation;
        _suppressProxyRotationChange = false;
        UpdateProxyPlanSummary(selected.Name);
        var serverName = string.IsNullOrWhiteSpace(selected.ServerName) ? _defaultServerName : selected.ServerName;
        var serverUrl = string.IsNullOrWhiteSpace(selected.ServerUrl) ? _defaultServerUrl : selected.ServerUrl;
        SelectServer(serverName, serverUrl);
        ServerComboBox.IsEnabled = false;
        InfoTextBlock.Text = "Editing existing account. Server is locked for this account.";
        CaptureBaseline();
        UpdateActionButtons();
    }

    private void AccountsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AccountsListBox.SelectedItem is not AccountEntry selected)
        {
            return;
        }

        try
        {
            _store.SetActive(selected.Name);
            Reload();
            SelectByName(selected.Name);
            InfoTextBlock.Text = $"Active account set to '{selected.Username}'.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Set active account", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ServerComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ServerComboBox.SelectedItem is ServerOption option)
        {
            ServerUrlTextBlock.Text = option.BaseUrl;
        }

        UpdateActionButtons();
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PromptToSaveUnsavedChanges())
        {
            return;
        }

        _suppressSelectionChanged = true;
        AccountsListBox.SelectedItem = null;
        _suppressSelectionChanged = false;
        _selectedAccountName = string.Empty;
        ClearEditor();
        UsernameTextBox.Focus();
        InfoTextBlock.Text = "Create a new account: set username, password, and server.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveEditor(isUpdate: false);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        SaveEditor(isUpdate: true);
    }

    private bool SaveEditor(bool isUpdate)
    {
        try
        {
            var entry = ReadEditor();
            if (isUpdate && !_editingExistingAccount)
            {
                throw new InvalidOperationException("Use Save when creating a new account.");
            }

            if (!isUpdate && _editingExistingAccount)
            {
                throw new InvalidOperationException("Use Update when editing an existing account.");
            }

            if (_editingExistingAccount)
            {
                entry.Name = _editingOriginalName;
                if (!string.Equals(entry.ServerName, _editingOriginalServerName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Server cannot be changed for an existing account.");
                }
            }

            var rotationEnabled = UseProxyRotationCheckBox.IsChecked == true;
            _editorProxyPlan.Enabled = rotationEnabled;
            if (rotationEnabled)
            {
                if (_editorProxyPlan.Assignments.Select(item => item.ProxyId).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
                {
                    throw new InvalidOperationException("Proxy rotation requires at least two different proxies. Open Schedule and add another proxy.");
                }

                var validation = ValidateEditorProxyPlan(entry, requireHealth: _editorProxyPlan.IsRotation);
                if (!validation.IsValid)
                {
                    AppDialog.Show(
                        this,
                        string.Join("\n", validation.Errors.Select(issue => $"• {issue.Message}")),
                        "Proxy setup errors",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                var runtime = _proxyPlanStore.LoadRuntime(entry.Name);
                var resolution = AccountProxyPlanResolver.Resolve(_editorProxyPlan, entry.Name, DateTimeOffset.Now, runtime);
                var current = _proxyLibraryEntries.FirstOrDefault(proxy => string.Equals(proxy.Id, resolution.ProxyId, StringComparison.OrdinalIgnoreCase));
                if (current is null && !string.IsNullOrWhiteSpace(resolution.ProxyId))
                {
                    throw new InvalidOperationException("The current scheduled proxy no longer exists in the proxy list.");
                }

                entry.ProxyEnabled = current is not null;
                if (current is not null)
                {
                    entry.ProxyServer = current.Server;
                }
            }

            if (entry.ProxyEnabled && !ConfirmProxyReuseForSave(entry.ProxyServer, entry.Name))
            {
                return false;
            }

            var setActiveForSave = !_editingExistingAccount && _accounts.Count == 0;
            _store.SaveAccount(entry, setActive: setActiveForSave);
            _proxyPlanStore.SaveActive(entry.Name, _editorProxyPlan);
            _proxyPlanStore.DeleteDraft(entry.Name);
            if (rotationEnabled)
            {
                foreach (var assignment in _editorProxyPlan.Assignments)
                {
                    _proxyLibraryStore.AddUsage(assignment.ProxyId, entry.Name);
                }
            }
            MarkSavedProxyUsed(entry);
            Reload();
            SelectByName(entry.Name);
            InfoTextBlock.Text = isUpdate
                ? $"Updated account '{entry.Username}'."
                : $"Saved account '{entry.Username}'.";
            return true;
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Save account", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsListBox.SelectedItem is not AccountEntry selected)
        {
            return;
        }

        try
        {
            var blockingQueueItems = _deletionService.CountActiveQueueItemsBlockingDeletion(selected.Name);
            var deleteAnyway = blockingQueueItems > 0;
            if (deleteAnyway)
            {
                var confirmAnyway = AppDialog.ShowCustom(
                    this,
                    $"Delete active account '{selected.Username}' anyway?\n\nThis clears {blockingQueueItems} pending, running, or paused queue item(s) and removes the account state. This cannot be undone.",
                    "Delete account",
                    [("Delete anyway", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel,
                    MessageBoxResult.Cancel);
                if (confirmAnyway != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            else
            {
                var confirm = AppDialog.Show(this, $"Delete account '{selected.Username}'?", "Delete account", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            // Deleting an account should not trigger an extra unsaved-changes prompt
            // when the selection is reloaded after removal.
            _selectedAccountName = string.Empty;
            _deletionService.DeleteAccount(selected.Name, deleteAnyway: deleteAnyway);
            Reload();
            InfoTextBlock.Text = deleteAnyway
                ? $"Deleted account '{selected.Username}' and cleared {blockingQueueItems} queue item(s)."
                : $"Deleted account '{selected.Username}'.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Delete account", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearAccountsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accounts.Count == 0)
        {
            InfoTextBlock.Text = "No accounts to clear.";
            return;
        }

        var confirm = AppDialog.Show(
            this,
            $"Clear all {_accounts.Count} account(s)? This cannot be undone.",
            "Clear accounts",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            // Clearing the editor selection avoids an extra unsaved-changes prompt when the
            // list is reloaded after removal.
            _selectedAccountName = string.Empty;
            foreach (var account in _accounts.ToList())
            {
                _deletionService.DeleteAccount(account.Name);
            }

            Reload();
            InfoTextBlock.Text = "Cleared all accounts.";
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Clear accounts", MessageBoxButton.OK, MessageBoxImage.Warning);
            Reload();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PromptToSaveUnsavedChanges())
        {
            return;
        }

        _isClosing = true;
        DialogResult = true;
        Close();
    }

    private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        _showPassword = !_showPassword;
        if (_showPassword)
        {
            PasswordTextBox.Text = PasswordBox.Password;
        }
        else
        {
            PasswordBox.Password = PasswordTextBox.Text;
        }

        PasswordBox.Visibility = _showPassword ? Visibility.Collapsed : Visibility.Visible;
        PasswordTextBox.Visibility = _showPassword ? Visibility.Visible : Visibility.Collapsed;
        TogglePasswordButton.Content = _showPassword ? "Hide" : "Show";
        UpdateActionButtons();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_showPassword)
        {
            PasswordTextBox.Text = PasswordBox.Password;
        }

        UpdateActionButtons();
    }

    private void PasswordTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_showPassword)
        {
            PasswordBox.Password = PasswordTextBox.Text;
        }

        UpdateActionButtons();
    }

    private void EditorField_Changed(object sender, RoutedEventArgs e)
    {
        UpdateActionButtons();
    }

    private void ProxySchemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateActionButtons();
    }

    private void UseProxyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (UseProxyCheckBox.IsChecked != true && NeverUseOwnIpCheckBox.IsChecked == true)
        {
            UseProxyCheckBox.IsChecked = true;
            return;
        }

        var enabled = UseProxyCheckBox.IsChecked == true;
        if (!enabled && UseProxyRotationCheckBox is not null)
        {
            _suppressProxyRotationChange = true;
            UseProxyRotationCheckBox.IsChecked = false;
            _suppressProxyRotationChange = false;
            _editorProxyPlan.Enabled = false;
        }
        SafeRunAccountEditorAction(() => SetProxyFieldsEnabled(enabled), "toggle proxy fields");
        if (enabled)
        {
            InfoTextBlock.Text = "Proxy applies on next bot start. Enabling a proxy on an already "
                + "logged-in account may require a fresh login.";
        }

        UpdateActionButtons();
    }

    private void UseProxyRotationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressProxyRotationChange || UseProxyRotationCheckBox is null)
        {
            return;
        }

        var enabled = UseProxyRotationCheckBox.IsChecked == true;
        if (enabled && UseProxyCheckBox.IsChecked != true)
        {
            UseProxyCheckBox.IsChecked = true;
        }

        _editorProxyPlan.Enabled = enabled;
        UpdateProxyPlanSummary(ResolveCurrentEditorAccountName());
        InfoTextBlock.Text = enabled
            ? "Proxy rotation enabled. Configure at least two proxies in Schedule."
            : "Proxy rotation disabled. The saved schedule is kept for later.";
        UpdateActionButtons();
    }

    private void NeverUseOwnIpCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (NeverUseOwnIpCheckBox.IsChecked == true && UseProxyCheckBox.IsChecked != true)
        {
            UseProxyCheckBox.IsChecked = true;
            SafeRunAccountEditorAction(() => SetProxyFieldsEnabled(true), "enable proxy fields");
        }

        UpdateActionButtons();
    }

    private async void CheckIpAddressButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(CheckIpAddressAsync, message => System.Diagnostics.Debug.WriteLine(message));

    private async Task CheckIpAddressAsync()
    {
        CheckIpAddressButton.IsEnabled = false;
        _proxyCheckCompleted = false;
        _proxyCheckCts?.Dispose();
        _proxyCheckCts = new CancellationTokenSource();
        ShowProxyCheckOverlay("Check IP address", "Preparing IP check...", completed: false);

        try
        {
            Proxy? proxy = null;
            string? proxyServer = null;
            var mode = "Direct";
            string? proxyWarning = null;

            if (UseProxyCheckBox.IsChecked == true)
            {
                proxyServer = ValidateCurrentProxyFields();
                if (!ProxyParser.TryBuild(proxyServer, out proxy, out proxyWarning) || proxy is null)
                {
                    CompleteProxyCheckOverlay("Check IP address", ProxyCheckResultCodec.BuildFailure("Invalid proxy settings.", proxyServer), string.Empty, success: false);
                    return;
                }

                mode = "Proxy";
            }

            var result = await ProxyCheckService.CheckIpAsync(mode, proxyServer, proxy, UpdateProxyCheckStatus, _proxyCheckCts.Token);
            var warningText = string.IsNullOrWhiteSpace(proxyWarning) ? string.Empty : $" Warning: {proxyWarning}";
            CompleteProxyCheckOverlay("Check IP address", result, warningText, success: true);
        }
        catch (OperationCanceledException)
        {
            CompleteProxyCheckOverlay("Check IP address", "IP check cancelled.", string.Empty, success: false);
        }
        catch (Exception ex)
        {
            CompleteProxyCheckOverlay("Check IP address", ProxyCheckResultCodec.BuildFailure(ProxyCheckResultCodec.SummarizeError(ex.Message), TryBuildProxyServerStringForDisplay()), string.Empty, success: false);
        }
        finally
        {
            CheckIpAddressButton.IsEnabled = true;
        }
    }

    private void ProxyCheckOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_proxyCheckCompleted)
        {
            ProxyCheckOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        ProxyCheckOverlayButton.IsEnabled = false;
        UpdateProxyCheckStatus("Cancelling proxy check...");
        _proxyCheckCts?.Cancel();
    }

    private void ProxyFinderButton_Click(object sender, RoutedEventArgs e)
    {
        var initialScheme = (ProxySchemeComboBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        var finder = new ProxyFinderWindow(initialScheme) { Owner = this };
        if (finder.ShowDialog() != true || finder.SelectedProxy is not { } pick)
        {
            return;
        }

        // Apply the chosen proxy to this account's fields and turn the proxy on.
        UseProxyCheckBox.IsChecked = true;
        SafeRunAccountEditorAction(() =>
        {
            SelectProxyScheme(pick.Scheme);
            ProxyHostTextBox.Text = pick.Host;
            ProxyPortTextBox.Text = pick.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RefreshSavedProxySelection();
        }, "apply selected proxy");
        UpdateActionButtons();
    }

    private void ProxyLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var accountNames = _store.ListAccounts()
            .Select(account => account.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var activeProxyServer = UseProxyCheckBox.IsChecked == true ? BuildProxyServerString() : string.Empty;
        var dialog = new ProxyLibraryWindow(_proxyLibraryStore, accountNames, activeProxyServer, ResolveCurrentEditorAccountName())
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true)
        {
            ReloadProxyLibraryEntries();
            RefreshSavedProxySelection();
            InfoTextBlock.Text = "Proxy list updated.";
        }
    }

    private void ProxyScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var accountName = ResolveCurrentEditorAccountName();
            if (string.IsNullOrWhiteSpace(accountName))
            {
                throw new InvalidOperationException("Enter username and select a server before configuring proxy rotation.");
            }

            ReloadProxyLibraryEntries();
            if (_proxyLibraryEntries.Count == 0)
            {
                throw new InvalidOperationException("Add proxies to the proxy list first.");
            }

            var settings = ReadProxyPlanSettings(accountName);
            var dialog = new ProxyScheduleWindow(
                _proxyPlanStore,
                _proxyLibraryStore,
                _editorProxyPlan,
                _proxyLibraryEntries,
                accountName,
                (ServerComboBox.SelectedItem as ServerOption)?.BaseUrl ?? _defaultServerUrl,
                NeverUseOwnIpCheckBox.IsChecked == true,
                settings.PacingEnabled,
                settings.AllowedHours,
                settings.SleepMinMinutes,
                _accounts.Select(account => account.Name),
                ResolveProxyScheduleServerTimeOffset())
            {
                Owner = this,
            };

            if (dialog.ShowDialog() == true)
            {
                _editorProxyPlan = dialog.ResultPlan;
                UseProxyCheckBox.IsChecked = _editorProxyPlan.Enabled;
                _suppressProxyRotationChange = true;
                UseProxyRotationCheckBox.IsChecked = _editorProxyPlan.IsRotation;
                _suppressProxyRotationChange = false;
                _editorProxyPlan.Enabled = UseProxyRotationCheckBox.IsChecked == true;
                SynchronizeSavedScheduleProxy(accountName);
                _baselineProxyPlanJson = JsonSerializer.Serialize(_editorProxyPlan);
                UpdateProxyPlanSummary(accountName);
                UpdateActionButtons();
                InfoTextBlock.Text = "Proxy schedule saved and activated.";
            }

            ReloadProxyLibraryEntries();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Proxy schedule", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private ProxyPlanValidationResult ValidateEditorProxyPlan(AccountEntry entry, bool requireHealth)
    {
        var settings = ReadProxyPlanSettings(entry.Name);
        return AccountProxyPlanValidator.Validate(
            _editorProxyPlan,
            _proxyLibraryEntries,
            entry.Name,
            entry.NeverUseOwnIp,
            settings.PacingEnabled,
            settings.AllowedHours,
            settings.SleepMinMinutes,
            requireHealth);
    }

    private (bool PacingEnabled, IReadOnlyCollection<int> AllowedHours, int SleepMinMinutes) ReadProxyPlanSettings(string accountName)
    {
        var config = _botConfigStore.LoadForAccount(accountName);
        var pacingEnabled = config[BotOptionPayloadKeys.SessionPacingEnabled]?.GetValue<bool>() ?? PacingDefaults.SessionPacingEnabled;
        var sleepMin = config[BotOptionPayloadKeys.SessionPacingSleepMinMinutes]?.GetValue<int>() ?? PacingDefaults.SessionPacingSleepMinMinutes;
        var hours = config[BotOptionPayloadKeys.SessionPacingAllowedHours] is JsonArray array
            ? array.Select(node => node?.GetValue<int>() ?? -1).Where(hour => hour is >= 0 and <= 23).ToArray()
            : Enumerable.Range(0, 24).ToArray();
        return (pacingEnabled, hours, sleepMin);
    }

    private TimeSpan ResolveProxyScheduleServerTimeOffset()
    {
        try
        {
            return ServerTimeClock.ResolveUtcOffset(_botConfigStore.Load(), DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[proxy-plan] could not resolve server time offset: {ex.Message}");
            return TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        }
    }

    private void ApplyCurrentPlanProxyToFields(string accountName)
    {
        if (!_editorProxyPlan.Enabled)
        {
            return;
        }

        var runtime = _proxyPlanStore.LoadRuntime(accountName);
        var resolution = AccountProxyPlanResolver.Resolve(_editorProxyPlan, accountName, DateTimeOffset.Now, runtime);
        var proxy = _proxyLibraryEntries.FirstOrDefault(item => string.Equals(item.Id, resolution.ProxyId, StringComparison.OrdinalIgnoreCase));
        if (proxy is null)
        {
            return;
        }

        SelectProxyScheme(proxy.Scheme);
        ProxyHostTextBox.Text = proxy.Host;
        ProxyPortTextBox.Text = proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void SynchronizeSavedScheduleProxy(string accountName)
    {
        if (!_editingExistingAccount)
        {
            ApplyCurrentPlanProxyToFields(accountName);
            return;
        }

        var account = _store.ListAccounts().FirstOrDefault(item =>
            string.Equals(item.Name, accountName, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            throw new InvalidOperationException("The account could not be found while synchronizing the proxy schedule.");
        }

        var runtime = _proxyPlanStore.LoadRuntime(accountName);
        var resolution = AccountProxyPlanResolver.Resolve(_editorProxyPlan, accountName, DateTimeOffset.Now, runtime);
        var proxy = _proxyLibraryEntries.FirstOrDefault(item =>
            string.Equals(item.Id, resolution.ProxyId, StringComparison.OrdinalIgnoreCase));

        account.ProxyEnabled = proxy is not null;
        if (proxy is not null)
        {
            account.ProxyServer = proxy.Server;
            SelectProxyScheme(proxy.Scheme);
            ProxyHostTextBox.Text = proxy.Host;
            ProxyPortTextBox.Text = proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        _store.SaveAccount(account, setActive: false);
        _baselineEditorState = _baselineEditorState with
        {
            ProxyEnabled = account.ProxyEnabled,
            ProxyServer = account.ProxyServer,
        };
        RefreshSavedProxySelection();
    }

    private void UpdateProxyPlanSummary(string accountName)
    {
        if (ProxyPlanSummaryTextBlock is null)
        {
            return;
        }

        if (UseProxyRotationCheckBox?.IsChecked != true)
        {
            ProxyPlanSummaryTextBlock.Text = _editorProxyPlan.Assignments.Count > 1
                ? $"Rotation off · {_editorProxyPlan.Assignments.Count} saved proxies"
                : "Single proxy mode";
            ProxyPlanNextTextBlock.Text = string.Empty;
            return;
        }

        ProxyPlanSummaryTextBlock.Text = $"{_editorProxyPlan.Assignments.Count} proxies ({_editorProxyPlan.VariationPercent}% variation)";
        var runtime = string.IsNullOrWhiteSpace(accountName) ? new AccountProxyRuntimeState() : _proxyPlanStore.LoadRuntime(accountName);
        var resolution = AccountProxyPlanResolver.Resolve(_editorProxyPlan, accountName, DateTimeOffset.Now, runtime);
        var current = _proxyLibraryEntries.FirstOrDefault(proxy => string.Equals(proxy.Id, resolution.ProxyId, StringComparison.OrdinalIgnoreCase));
        var next = _proxyLibraryEntries.FirstOrDefault(proxy => string.Equals(proxy.Id, resolution.NextProxyId, StringComparison.OrdinalIgnoreCase));
        var currentName = string.IsNullOrWhiteSpace(resolution.ProxyId) ? "Direct connection" : current?.DisplayName ?? "Unknown";
        var nextName = string.IsNullOrWhiteSpace(resolution.NextProxyId) ? "Direct connection" : next?.DisplayName ?? "Unknown";
        ProxyPlanNextTextBlock.Text = resolution.NextTransitionAt is { } transition
            ? $"Current: {currentName}{Environment.NewLine}Next: {nextName} at {transition:ddd HH:mm}"
            : $"Current: {currentName}";
    }

    private void SavedProxyComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSavedProxySelection || SavedProxyComboBox.SelectedItem is not SavedProxyOption { Entry: { } entry })
        {
            return;
        }

        var currentAccountName = ResolveCurrentEditorAccountName();
        var reuse = _proxyLibraryStore.ClassifyReuse(entry.Server, currentAccountName);
        if (reuse.Reuse == ProxyReuse.LockedToOther)
        {
            AppDialog.Show(
                this,
                $"Proxy is locked to account '{string.Join(", ", reuse.Accounts)}'.",
                "Saved proxy",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RefreshSavedProxySelection();
            return;
        }

        if (reuse.Reuse == ProxyReuse.UsedByOthers && !ConfirmProxyReuse(reuse))
        {
            RefreshSavedProxySelection();
            return;
        }

        UseProxyCheckBox.IsChecked = true;
        SafeRunAccountEditorAction(() =>
        {
            SelectProxyScheme(entry.Scheme);
            ProxyHostTextBox.Text = entry.Host;
            ProxyPortTextBox.Text = entry.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }, "apply saved proxy");
        UpdateActionButtons();
    }

    private void ServerListButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerListWindow(_serverOptions, _defaultServerOptions, _serverCatalogStore)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            // Refresh from the saved catalog, then reload accounts so a server edit (e.g. a
            // renamed server) propagates to the accounts that use it. Reload() reconciles names.
            var editingName = _editingExistingAccount ? _editingOriginalName : null;
            ReloadServerOptionsFromCatalog();
            _selectedAccountName = string.Empty;
            Reload();
            if (!string.IsNullOrEmpty(editingName))
            {
                SelectByName(editingName);
            }
        }
    }

    private void ReloadServerOptionsFromCatalog()
    {
        List<ServerOption> latest;
        try
        {
            latest = _serverCatalogStore.Load();
        }
        catch (Exception ex)
        {
            AppendCatalogLoadFailure(ex);
            return;
        }

        if (latest.Count == 0)
        {
            return;
        }

        _serverOptions.Clear();
        _serverOptions.AddRange(latest);
    }

    // Update accounts whose server URL still matches a known server but whose stored server name
    // has drifted (e.g. the server was renamed). Runs on every Reload so the list self-heals.
    private void ReconcileAccountServerNames()
    {
        var nameByUrl = _serverOptions
            .Where(option => !string.IsNullOrWhiteSpace(option.BaseUrl) && !string.IsNullOrWhiteSpace(option.Name))
            .GroupBy(option => NormalizeServerUrl(option.BaseUrl), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name.Trim(), StringComparer.OrdinalIgnoreCase);

        if (nameByUrl.Count == 0)
        {
            return;
        }

        foreach (var account in _store.ListAccounts())
        {
            if (string.IsNullOrWhiteSpace(account.ServerUrl))
            {
                continue;
            }

            if (!nameByUrl.TryGetValue(NormalizeServerUrl(account.ServerUrl), out var newName))
            {
                continue;
            }

            if (string.Equals(account.ServerName.Trim(), newName, StringComparison.Ordinal))
            {
                continue;
            }

            account.ServerName = newName;
            _store.SaveAccount(account, setActive: false);
        }
    }

    private static string NormalizeServerUrl(string url)
        => (url ?? string.Empty).Trim().TrimEnd('/');

    private void AppendCatalogLoadFailure(Exception ex)
    {
        InfoTextBlock.Text = $"Could not reload server list: {ex.Message}";
    }

    private void ReloadProxyLibraryEntries()
    {
        try
        {
            _proxyLibraryEntries = _proxyLibraryStore.Load();
        }
        catch (Exception ex)
        {
            _proxyLibraryEntries = [];
            InfoTextBlock.Text = $"Could not reload proxy list: {ex.Message}";
        }

        RebuildSavedProxyOptions(ResolveCurrentEditorAccountName());
        SetProxyFieldsEnabled(UseProxyCheckBox.IsChecked == true);
    }

    private void RefreshSavedProxySelection()
    {
        if (SavedProxyComboBox is null)
        {
            return;
        }

        RebuildSavedProxyOptions(ResolveCurrentEditorAccountName());
        var proxyServer = BuildProxyServerString();
        var match = _proxyLibraryEntries.Count == 0
            ? null
            : ProxyLibraryStore.FindByServer(_proxyLibraryEntries, proxyServer);
        var option = match is null
            ? _savedProxyOptions.FirstOrDefault(item => item.Entry is null)
            : _savedProxyOptions.FirstOrDefault(item => item.Entry is not null
                && string.Equals(item.Entry.Id, match.Id, StringComparison.OrdinalIgnoreCase));

        _suppressSavedProxySelection = true;
        SavedProxyComboBox.SelectedItem = option ?? _savedProxyOptions.FirstOrDefault();
        _suppressSavedProxySelection = false;
    }

    private void RebuildSavedProxyOptions(string? accountName)
    {
        _savedProxyOptions = AccountEditorState.BuildSavedProxyOptions(_proxyLibraryEntries, accountName, _accounts);

        _suppressSavedProxySelection = true;
        SavedProxyComboBox.ItemsSource = null;
        SavedProxyComboBox.ItemsSource = _savedProxyOptions;
        SavedProxyComboBox.SelectedIndex = 0;
        _suppressSavedProxySelection = false;
    }

    private bool ConfirmProxyReuseForSave(string proxyServer, string accountName)
    {
        var reuse = _proxyLibraryStore.ClassifyReuse(proxyServer, accountName);
        return reuse.Reuse switch
        {
            ProxyReuse.LockedToOther => ShowProxyLocked(reuse),
            ProxyReuse.UsedByOthers => ConfirmProxyReuse(reuse),
            _ => true,
        };
    }

    private bool ShowProxyLocked(ProxyReuseClassification reuse)
    {
        AppDialog.Show(
            this,
            $"Proxy is locked to account '{string.Join(", ", reuse.Accounts)}'.",
            "Save account",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private bool ConfirmProxyReuse(ProxyReuseClassification reuse)
    {
        var accounts = string.Join(", ", reuse.Accounts);
        var result = AppDialog.ShowCustom(
            this,
            $"This proxy has been used by: {accounts}.\n\nUse it anyway?",
            "Proxy reuse warning",
            [("Use anyway", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        return result == MessageBoxResult.Yes;
    }

    private void MarkSavedProxyUsed(AccountEntry entry)
    {
        if (!entry.ProxyEnabled || string.IsNullOrWhiteSpace(entry.ProxyServer))
        {
            return;
        }

        try
        {
            var match = _proxyLibraryStore.FindByServer(entry.ProxyServer);
            if (match is null)
            {
                return;
            }

            _proxyLibraryStore.AddUsage(match.Id, entry.Name);
        }
        catch (Exception ex)
        {
            InfoTextBlock.Text = $"Account saved, but proxy usage could not be updated: {ex.Message}";
        }
    }

    private string ResolveCurrentEditorAccountName()
    {
        if (_editingExistingAccount)
        {
            return _editingOriginalName;
        }

        var username = UsernameTextBox.Text.Trim();
        if (username.Length == 0)
        {
            return string.Empty;
        }

        var selectedServer = ServerComboBox.SelectedItem as ServerOption;
        var serverUrl = selectedServer?.BaseUrl ?? _defaultServerUrl;
        return AccountKeyNormalizer.MakeKey(username, serverUrl);
    }

    private void AccountsWindow_Closing(object? sender, CancelEventArgs e)
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
    }

    private AccountEntry ReadEditor()
    {
        var password = _showPassword ? PasswordTextBox.Text : PasswordBox.Password;
        var selectedServer = ServerComboBox.SelectedItem as ServerOption;
        var serverName = _editingExistingAccount
            ? _editingOriginalServerName
            : selectedServer?.Name ?? _defaultServerName;
        var serverUrl = selectedServer?.BaseUrl ?? _defaultServerUrl;
        var proxyScheme = (ProxySchemeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString()
            ?? "socks5";
        return AccountEditorState.BuildAccountEntry(new AccountEditorInput(
            UsernameTextBox.Text,
            password,
            serverName,
            serverUrl,
            UseProxyCheckBox.IsChecked == true,
            NeverUseOwnIpCheckBox.IsChecked == true,
            proxyScheme,
            ProxyHostTextBox.Text,
            ProxyPortTextBox.Text,
            _editingExistingAccount,
            _editingOriginalName));
    }

    private void SelectByName(string name)
    {
        var match = _accounts.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            AccountsListBox.SelectedItem = match;
        }
    }

    private void ClearEditor()
    {
        _editingExistingAccount = false;
        _editingOriginalName = string.Empty;
        _editingOriginalServerName = string.Empty;
        UsernameTextBox.Text = string.Empty;
        PasswordBox.Password = string.Empty;
        PasswordTextBox.Text = string.Empty;
        _showPassword = false;
        PasswordBox.Visibility = Visibility.Visible;
        PasswordTextBox.Visibility = Visibility.Collapsed;
        TogglePasswordButton.Content = "Show";
        UseProxyCheckBox.IsChecked = false;
        _suppressProxyRotationChange = true;
        UseProxyRotationCheckBox.IsChecked = false;
        _suppressProxyRotationChange = false;
        NeverUseOwnIpCheckBox.IsChecked = false;
        _editorProxyPlan = new AccountProxyPlan();
        UpdateProxyPlanSummary(string.Empty);
        SafeRunAccountEditorAction(() =>
        {
            LoadProxyFields(string.Empty);
            RefreshSavedProxySelection();
            SetProxyFieldsEnabled(false);
        }, "clear proxy fields");
        SelectServer(_defaultServerName, _defaultServerUrl);
        ServerComboBox.IsEnabled = true;
        CaptureBaseline();
        UpdateActionButtons();
    }

    private void EnsureServerListContainsDefaults()
    {
        var officialServers = OfficialServerCatalog.GetOfficialServers();

        // Only add the configured default server as a custom entry when neither the
        // custom list nor the built-in official catalog already covers its URL.
        var defaultUrlKnown = string.IsNullOrWhiteSpace(_defaultServerUrl)
            || _serverOptions.Concat(officialServers).Any(option =>
                string.Equals(NormalizeServerUrl(option.BaseUrl), NormalizeServerUrl(_defaultServerUrl), StringComparison.OrdinalIgnoreCase));
        if (!defaultUrlKnown)
        {
            _serverOptions.Add(new ServerOption { Name = _defaultServerName, BaseUrl = _defaultServerUrl });
        }

        RebuildServerComboItems(officialServers);
    }

    // Current special worlds from Travian's public calendar are shown first, then custom
    // servers and the built-in regional catalog. A custom entry that matches a discovered
    // special world is hidden from the picker to avoid showing the same world twice.
    private void RebuildServerComboItems(List<ServerOption> officialServers)
    {
        _comboServers = OfficialServerCatalog.BuildPickerServers(_serverOptions, _specialServerOptions, officialServers);
        var view = new ListCollectionView(_comboServers);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServerOption.Group)));
        ServerComboBox.ItemsSource = view;
    }

    private void SelectServer(string serverName, string serverUrl)
    {
        var match = _comboServers.FirstOrDefault(option =>
            string.Equals(option.BaseUrl, serverUrl, StringComparison.OrdinalIgnoreCase))
            ?? _comboServers.FirstOrDefault(option =>
                string.Equals(option.Name, serverName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            match = new ServerOption
            {
                Name = serverName,
                BaseUrl = serverUrl,
            };
            _serverOptions.Add(match);
            EnsureServerListContainsDefaults();
        }

        ServerComboBox.SelectedItem = match;
        ServerUrlTextBlock.Text = match.BaseUrl;
    }

    private void CaptureBaseline()
    {
        _baselineEditorState = ReadEditorSnapshot();
        _baselineProxyPlanJson = JsonSerializer.Serialize(_editorProxyPlan);
    }

    private bool HasUnsavedChanges()
    {
        return AccountEditorState.HasChanges(_baselineEditorState, ReadEditorSnapshot())
            || !string.Equals(_baselineProxyPlanJson, JsonSerializer.Serialize(_editorProxyPlan), StringComparison.Ordinal);
    }

    private AccountEditorSnapshot ReadEditorSnapshot()
        => new(
            UsernameTextBox.Text.Trim(),
            (_showPassword ? PasswordTextBox.Text : PasswordBox.Password) ?? string.Empty,
            (ServerComboBox.SelectedItem as ServerOption)?.BaseUrl?.Trim() ?? string.Empty,
            UseProxyCheckBox.IsChecked == true,
            BuildProxyServerString(),
            NeverUseOwnIpCheckBox.IsChecked == true);

    private void SetProxyFieldsEnabled(bool enabled)
    {
        if (ProxySchemeComboBox is not null)
        {
            ProxySchemeComboBox.IsEnabled = enabled;
        }

        if (SavedProxyComboBox is not null)
        {
            SavedProxyComboBox.IsEnabled = enabled && _savedProxyOptions.Count > 1;
        }

        if (ProxyHostTextBox is not null)
        {
            ProxyHostTextBox.IsEnabled = enabled;
        }

        if (ProxyPortTextBox is not null)
        {
            ProxyPortTextBox.IsEnabled = enabled;
        }

        if (UseProxyRotationCheckBox is not null)
        {
            UseProxyRotationCheckBox.IsEnabled = enabled;
        }

        if (ProxyScheduleButton is not null)
        {
            ProxyScheduleButton.IsEnabled = enabled;
        }
    }

    private void LoadProxyFields(string? proxyServer)
    {
        var value = proxyServer?.Trim() ?? string.Empty;
        var scheme = "socks5";
        var rest = value;
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            scheme = value[..schemeIndex].ToLowerInvariant();
            rest = value[(schemeIndex + 3)..];
        }

        var atIndex = rest.LastIndexOf('@');
        if (atIndex >= 0)
        {
            rest = rest[(atIndex + 1)..];
        }

        var host = rest;
        var port = string.Empty;
        var colonIndex = rest.LastIndexOf(':');
        if (colonIndex > 0 && colonIndex < rest.Length - 1)
        {
            host = rest[..colonIndex];
            port = rest[(colonIndex + 1)..];
        }

        SelectProxyScheme(scheme);
        if (ProxyHostTextBox is not null)
        {
            ProxyHostTextBox.Text = host;
        }

        if (ProxyPortTextBox is not null)
        {
            ProxyPortTextBox.Text = port;
        }
    }

    private void SelectProxyScheme(string scheme)
    {
        if (ProxySchemeComboBox is null)
        {
            return;
        }

        var normalized = scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase)
            ? "socks4"
            : scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                ? "http"
                : "socks5";

        foreach (var item in ProxySchemeComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                ProxySchemeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private string BuildProxyServerString()
    {
        var scheme = (ProxySchemeComboBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(scheme))
        {
            scheme = "socks5";
        }

        return AccountEditorState.BuildProxyServer(scheme, ProxyHostTextBox?.Text, ProxyPortTextBox?.Text);
    }

    private string ValidateCurrentProxyFields()
    {
        var scheme = (ProxySchemeComboBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        return AccountEditorState.ValidateProxyFieldsForCheck(
            scheme,
            ProxyHostTextBox?.Text,
            ProxyPortTextBox?.Text);
    }

    private void ShowProxyCheckOverlay(string title, string status, bool completed)
    {
        ProxyCheckOverlay.Visibility = Visibility.Visible;
        _proxyCheckCompleted = completed;
        ProxyCheckTitleTextBlock.Text = title;
        ProxyCheckStatusTextBlock.Text = status;
        ProxyCheckResultGrid.Visibility = Visibility.Collapsed;
        ProxyCheckOverlayButton.IsEnabled = true;
        ProxyCheckOverlayButton.Content = completed ? "Continue" : "Cancel";
        ProxyCheckOverlayButton.Background = completed
            ? FindResource("PrimaryButtonBrush") as System.Windows.Media.Brush
            : FindResource("DangerBgBrush") as System.Windows.Media.Brush;
        ProxyCheckOverlayButton.BorderBrush = completed
            ? FindResource("PrimaryButtonBrush") as System.Windows.Media.Brush
            : FindResource("DangerBorderBrush") as System.Windows.Media.Brush;
        ProxyCheckOverlayButton.Foreground = completed
            ? FindResource("TooltipForegroundBrush") as System.Windows.Media.Brush
            : FindResource("DangerTextBrush") as System.Windows.Media.Brush;
    }

    private void UpdateProxyCheckStatus(string status)
    {
        ProxyCheckStatusTextBlock.Text = status;
    }

    private void CompleteProxyCheckOverlay(string title, string status, string warning, bool success)
    {
        ShowProxyCheckOverlay(title, success ? "Your IP is:" : status, completed: true);
        if (success && ProxyCheckResultCodec.TryParseSuccess(status, out var result))
        {
            ProxyCheckResultGrid.Visibility = Visibility.Visible;
            SetProxyCheckResultLabels("IP", "Location", "ISP", "Route", "Latency", null);
            ProxyCheckIpTextBlock.Text = result.Ip;
            ProxyCheckLocationTextBlock.Text = result.Location;
            ProxyCheckIspTextBlock.Text = result.Isp;
            ProxyCheckRouteTextBlock.Text = result.Route;
            ProxyCheckLatencyTextBlock.Text = result.Latency + warning;
        }
        else if (!success && ProxyCheckResultCodec.TryParseFailure(status, out var failure))
        {
            ProxyCheckStatusTextBlock.Text = "Proxy check did not complete.";
            ProxyCheckResultGrid.Visibility = Visibility.Visible;
            SetProxyCheckResultLabels("Status", "Error", "Route", "Target", null, null);
            ProxyCheckIpTextBlock.Text = "Failed";
            ProxyCheckLocationTextBlock.Text = failure.Error;
            ProxyCheckIspTextBlock.Text = failure.Route;
            ProxyCheckRouteTextBlock.Text = failure.Target;
            ProxyCheckLatencyTextBlock.Text = string.Empty;
            ProxyCheckTargetTextBlock.Text = string.Empty;
        }

        InfoTextBlock.Text = success ? "Proxy check completed." : "Proxy check did not complete successfully.";
    }

    private void SetProxyCheckResultLabels(string row0, string row1, string row2, string row3, string? row4, string? row5)
    {
        ProxyCheckRow0LabelTextBlock.Text = row0;
        ProxyCheckRow1LabelTextBlock.Text = row1;
        ProxyCheckRow2LabelTextBlock.Text = row2;
        ProxyCheckRow3LabelTextBlock.Text = row3;
        ProxyCheckRow4LabelTextBlock.Text = row4 ?? string.Empty;
        ProxyCheckRow5LabelTextBlock.Text = row5 ?? string.Empty;
        var row4Visible = row4 is not null;
        ProxyCheckRow4LabelTextBlock.Visibility = row4Visible ? Visibility.Visible : Visibility.Collapsed;
        ProxyCheckLatencyTextBlock.Visibility = row4Visible ? Visibility.Visible : Visibility.Collapsed;
        var row5Visible = row5 is not null;
        ProxyCheckRow5LabelTextBlock.Visibility = row5Visible ? Visibility.Visible : Visibility.Collapsed;
        ProxyCheckTargetTextBlock.Visibility = row5Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private string TryBuildProxyServerStringForDisplay()
    {
        var proxyServer = BuildProxyServerString();
        return string.IsNullOrWhiteSpace(proxyServer) ? "Direct" : ProxyParser.MaskForLog(proxyServer);
    }


    private void SafeRunAccountEditorAction(Action action, string actionName)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (InfoTextBlock is not null)
            {
                InfoTextBlock.Text = $"Could not {actionName}: {ex.Message}";
            }
        }
    }

    private bool PromptToSaveUnsavedChanges()
    {
        if (!HasUnsavedChanges())
        {
            return true;
        }

        var result = AppDialog.ShowCustom(
            this,
            "You have unsaved changes.\n\nSave: Save changes\nDiscard: Discard changes\nCancel: Stay on this window",
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

        return result == MessageBoxResult.Yes && SaveEditor(isUpdate: _editingExistingAccount);
    }

    private void UpdateActionButtons()
    {
        SaveButton.IsEnabled = !_editingExistingAccount;
        UpdateButton.IsEnabled = _editingExistingAccount && HasUnsavedChanges();
        DeleteButton.IsEnabled = _editingExistingAccount;
    }

}
