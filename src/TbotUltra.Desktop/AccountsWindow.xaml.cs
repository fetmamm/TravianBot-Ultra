using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

public partial class AccountsWindow : Window
{
    private readonly EnvAccountStore _store;
    private readonly AccountDeletionService _deletionService;
    private readonly ServerCatalogStore _serverCatalogStore;
    private readonly ProxyLibraryStore _proxyLibraryStore = new();
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
    private string _activeAccountName = string.Empty;
    private bool _showPassword;
    private bool _editingExistingAccount;
    private string _editingOriginalName = string.Empty;
    private string _editingOriginalServerName = string.Empty;
    private string _selectedAccountName = string.Empty;
    private string _baselineUsername = string.Empty;
    private string _baselinePassword = string.Empty;
    private string _baselineServerUrl = string.Empty;
    private bool _baselineProxyEnabled;
    private string _baselineProxyServer = string.Empty;
    private bool _suppressSelectionChanged;
    private bool _suppressSavedProxySelection;
    private bool _isClosing;
    private CancellationTokenSource? _proxyCheckCts;
    private bool _proxyCheckCompleted;

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
        SafeRunAccountEditorAction(() => SelectProxyScheme("socks5"), "initialize proxy type");
        Reload();
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
        SafeRunAccountEditorAction(() =>
        {
            LoadProxyFields(selected.ProxyServer);
            RefreshSavedProxySelection();
            SetProxyFieldsEnabled(selected.ProxyEnabled);
        }, "load proxy fields");
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

            if (entry.ProxyEnabled && !ConfirmProxyReuseForSave(entry.ProxyServer, entry.Name))
            {
                return false;
            }

            var setActiveForSave = !_editingExistingAccount && _accounts.Count == 0;
            _store.SaveAccount(entry, setActive: setActiveForSave);
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
        var enabled = UseProxyCheckBox.IsChecked == true;
        SafeRunAccountEditorAction(() => SetProxyFieldsEnabled(enabled), "toggle proxy fields");
        if (enabled)
        {
            InfoTextBlock.Text = "Proxy applies on next bot start. Enabling a proxy on an already "
                + "logged-in account may require a fresh login.";
        }

        UpdateActionButtons();
    }

    private async void CheckProxyButton_Click(object sender, RoutedEventArgs e)
    {
        CheckProxyButton.IsEnabled = false;
        CheckMyIpButton.IsEnabled = false;
        _proxyCheckCompleted = false;
        _proxyCheckCts?.Dispose();
        _proxyCheckCts = new CancellationTokenSource();
        ShowProxyCheckOverlay("Proxy check", "Preparing proxy check...", completed: false);

        try
        {
            var proxyServer = ValidateCurrentProxyFields();
            if (!ProxyParser.TryBuild(proxyServer, out var proxy, out var proxyWarning) || proxy is null)
            {
                CompleteProxyCheckOverlay("Proxy check", BuildProxyCheckFailure("Invalid proxy settings.", proxyServer), string.Empty, success: false);
                return;
            }

            var result = await CheckIpAsync("Proxy", proxyServer, proxy, UpdateProxyCheckStatus, _proxyCheckCts.Token);
            var warningText = string.IsNullOrWhiteSpace(proxyWarning) ? string.Empty : $" Warning: {proxyWarning}";
            CompleteProxyCheckOverlay("Proxy check", result, warningText, success: true);
        }
        catch (OperationCanceledException)
        {
            CompleteProxyCheckOverlay("Proxy check", "Proxy check cancelled.", string.Empty, success: false);
        }
        catch (Exception ex)
        {
            CompleteProxyCheckOverlay("Proxy check", BuildProxyCheckFailure(SummarizeProxyCheckError(ex.Message), TryBuildProxyServerStringForDisplay()), string.Empty, success: false);
        }
        finally
        {
            CheckProxyButton.IsEnabled = UseProxyCheckBox.IsChecked == true;
            CheckMyIpButton.IsEnabled = true;
        }
    }

    private async void CheckMyIpButton_Click(object sender, RoutedEventArgs e)
    {
        CheckProxyButton.IsEnabled = false;
        CheckMyIpButton.IsEnabled = false;
        _proxyCheckCompleted = false;
        _proxyCheckCts?.Dispose();
        _proxyCheckCts = new CancellationTokenSource();
        ShowProxyCheckOverlay("Check IP adress", "Preparing IP check...", completed: false);

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
                    CompleteProxyCheckOverlay("Check IP adress", BuildProxyCheckFailure("Invalid proxy settings.", proxyServer), string.Empty, success: false);
                    return;
                }

                mode = "Proxy";
            }

            var result = await CheckIpAsync(mode, proxyServer, proxy, UpdateProxyCheckStatus, _proxyCheckCts.Token);
            var warningText = string.IsNullOrWhiteSpace(proxyWarning) ? string.Empty : $" Warning: {proxyWarning}";
            CompleteProxyCheckOverlay("Check IP adress", result, warningText, success: true);
        }
        catch (OperationCanceledException)
        {
            CompleteProxyCheckOverlay("Check IP adress", "IP check cancelled.", string.Empty, success: false);
        }
        catch (Exception ex)
        {
            CompleteProxyCheckOverlay("Check IP adress", BuildProxyCheckFailure(SummarizeProxyCheckError(ex.Message), TryBuildProxyServerStringForDisplay()), string.Empty, success: false);
        }
        finally
        {
            CheckProxyButton.IsEnabled = UseProxyCheckBox.IsChecked == true;
            CheckMyIpButton.IsEnabled = true;
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

    private void FindBestProxyButton_Click(object sender, RoutedEventArgs e)
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
        var dialog = new ProxyLibraryWindow(_proxyLibraryStore, accountNames)
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

        _savedProxyOptions = new List<SavedProxyOption>
        {
            new(null, "Select saved proxy..."),
        };
        _savedProxyOptions.AddRange(_proxyLibraryEntries.Select(entry => new SavedProxyOption(entry, BuildSavedProxyDisplay(entry))));

        _suppressSavedProxySelection = true;
        SavedProxyComboBox.ItemsSource = null;
        SavedProxyComboBox.ItemsSource = _savedProxyOptions;
        SavedProxyComboBox.SelectedIndex = 0;
        _suppressSavedProxySelection = false;
        SetProxyFieldsEnabled(UseProxyCheckBox.IsChecked == true);
    }

    private void RefreshSavedProxySelection()
    {
        if (SavedProxyComboBox is null)
        {
            return;
        }

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

    private static string BuildSavedProxyDisplay(ProxyLibraryEntry entry)
    {
        var display = entry.DisplayName;
        if (!string.IsNullOrWhiteSpace(entry.AssignedAccount))
        {
            return $"{display} [locked: {entry.AssignedAccount}]";
        }

        var usedCount = entry.UsedByAccounts.Count(item => !string.IsNullOrWhiteSpace(item));
        return usedCount > 0 ? $"{display} [used: {usedCount}]" : display;
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
        var username = UsernameTextBox.Text.Trim();
        var password = _showPassword ? PasswordTextBox.Text : PasswordBox.Password;
        if (username.Length == 0 || password.Length == 0)
        {
            throw new InvalidOperationException("Username and password are required.");
        }

        var selectedServer = ServerComboBox.SelectedItem as ServerOption;
        var serverName = selectedServer?.Name ?? _defaultServerName;
        var serverUrl = selectedServer?.BaseUrl ?? _defaultServerUrl;

        var proxyEnabled = UseProxyCheckBox.IsChecked == true;
        var proxyServer = BuildProxyServerString();
        if (proxyEnabled)
        {
            var proxyHost = ProxyHostTextBox.Text.Trim();
            var proxyPort = ProxyPortTextBox.Text.Trim();
            if (proxyHost.Length == 0 || proxyPort.Length == 0)
            {
                throw new InvalidOperationException("Proxy host/IP and port are required when 'Use proxy' is on.");
            }

            if (proxyHost.Any(char.IsWhiteSpace) || proxyHost.Contains("://", StringComparison.Ordinal) || proxyHost.Contains(':'))
            {
                throw new InvalidOperationException("Proxy host/IP must not contain spaces, scheme, or port. Use the separate type and port fields.");
            }

            if (!int.TryParse(proxyPort, out var parsedPort) || parsedPort is < 1 or > 65535)
            {
                throw new InvalidOperationException("Proxy port must be a number between 1 and 65535.");
            }
        }

        var normalizedName = _editingExistingAccount
            ? _editingOriginalName
            : AccountKeyNormalizer.MakeKey(username, serverUrl);

        return new AccountEntry
        {
            Name = normalizedName,
            Username = username,
            Password = password,
            ServerName = serverName,
            ServerUrl = serverUrl,
            ProxyEnabled = proxyEnabled,
            ProxyServer = proxyServer,
        };
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

    // Custom servers first (the user's own entries, e.g. SS-Travi, stay on top), then the
    // full official catalog grouped by region. Officials are always shown even when a custom
    // entry has the same URL, so every region group is complete; URL-based selection matches
    // the custom entry first since it comes earlier in the list.
    private void RebuildServerComboItems(List<ServerOption> officialServers)
    {
        var combined = new List<ServerOption>();
        foreach (var option in _serverOptions.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            option.Group = OfficialServerCatalog.CustomGroupName;
            combined.Add(option);
        }

        combined.AddRange(officialServers);

        _comboServers = combined;
        var view = new ListCollectionView(combined);
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
        _baselineUsername = UsernameTextBox.Text.Trim();
        _baselinePassword = (_showPassword ? PasswordTextBox.Text : PasswordBox.Password) ?? string.Empty;
        _baselineServerUrl = (ServerComboBox.SelectedItem as ServerOption)?.BaseUrl?.Trim() ?? string.Empty;
        _baselineProxyEnabled = UseProxyCheckBox.IsChecked == true;
        _baselineProxyServer = BuildProxyServerString();
    }

    private bool HasUnsavedChanges()
    {
        var currentUsername = UsernameTextBox.Text.Trim();
        var currentPassword = (_showPassword ? PasswordTextBox.Text : PasswordBox.Password) ?? string.Empty;
        var currentServerUrl = (ServerComboBox.SelectedItem as ServerOption)?.BaseUrl?.Trim() ?? string.Empty;
        var currentProxyEnabled = UseProxyCheckBox.IsChecked == true;
        var currentProxyServer = BuildProxyServerString();

        return !string.Equals(currentUsername, _baselineUsername, StringComparison.Ordinal)
            || !string.Equals(currentPassword, _baselinePassword, StringComparison.Ordinal)
            || !string.Equals(currentServerUrl, _baselineServerUrl, StringComparison.OrdinalIgnoreCase)
            || currentProxyEnabled != _baselineProxyEnabled
            || !string.Equals(currentProxyServer, _baselineProxyServer, StringComparison.Ordinal);
    }

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

        if (CheckProxyButton is not null)
        {
            CheckProxyButton.IsEnabled = enabled;
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

        var host = ProxyHostTextBox?.Text.Trim() ?? string.Empty;
        var port = ProxyPortTextBox?.Text.Trim() ?? string.Empty;
        if (host.Length == 0 && port.Length == 0)
        {
            return string.Empty;
        }

        return $"{scheme}://{host}:{port}";
    }

    private string ValidateCurrentProxyFields()
    {
        var proxyServer = BuildProxyServerString();
        var proxyHost = ProxyHostTextBox?.Text.Trim() ?? string.Empty;
        var proxyPort = ProxyPortTextBox?.Text.Trim() ?? string.Empty;
        if (proxyHost.Length == 0 || proxyPort.Length == 0)
        {
            throw new InvalidOperationException("Enter proxy IP and port first.");
        }

        if (proxyHost.Any(char.IsWhiteSpace) || proxyHost.Contains("://", StringComparison.Ordinal) || proxyHost.Contains(':'))
        {
            throw new InvalidOperationException("Proxy IP must not contain spaces, scheme, or port.");
        }

        if (!int.TryParse(proxyPort, out var parsedPort) || parsedPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("Proxy port must be a number between 1 and 65535.");
        }

        return proxyServer;
    }

    private static async Task<string> CheckIpAsync(
        string mode,
        string? proxyServer,
        Proxy? proxy,
        Action<string> status,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        status("Starting temporary browser...");
        cancellationToken.ThrowIfCancellationRequested();
        using var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        using var registration = cancellationToken.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (browser is not null)
                    {
                        await browser.CloseAsync();
                    }
                }
                catch
                {
                    // Browser may already be closing.
                }
            });
        });

        try
        {
            status(proxy is null ? "Launching browser..." : "Launching browser through proxy...");
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 20000,
            };
            if (proxy is not null)
            {
                launchOptions.Proxy = proxy;
            }

            browser = await playwright.Chromium.LaunchAsync(launchOptions);

            cancellationToken.ThrowIfCancellationRequested();
            status("Requesting public IP...");
            var page = await browser.NewPageAsync();
            await page.GotoAsync(
                "https://ipwho.is/",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 20000 });
            cancellationToken.ThrowIfCancellationRequested();

            status("Reading proxy details...");
            var raw = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 5000 });
            stopwatch.Stop();

            var info = ParseProxyCheckResponse(raw);
            var route = string.IsNullOrWhiteSpace(proxyServer)
                ? mode
                : $"{mode} ({ProxyParser.MaskForLog(proxyServer)})";
            return JsonSerializer.Serialize(new ProxyCheckResult(
                info.Ip,
                info.Location,
                info.Isp,
                route,
                $"{stopwatch.ElapsedMilliseconds} ms"));
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync();
                }
                catch
                {
                    // Browser may already have been closed by cancellation.
                }
            }
        }
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
        if (success && TryParseProxyCheckResult(status, out var result))
        {
            ProxyCheckResultGrid.Visibility = Visibility.Visible;
            SetProxyCheckResultLabels("IP", "Location", "ISP", "Route", "Latency", null);
            ProxyCheckIpTextBlock.Text = result.Ip;
            ProxyCheckLocationTextBlock.Text = result.Location;
            ProxyCheckIspTextBlock.Text = result.Isp;
            ProxyCheckRouteTextBlock.Text = result.Route;
            ProxyCheckLatencyTextBlock.Text = result.Latency + warning;
        }
        else if (!success && TryParseProxyCheckFailure(status, out var failure))
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

    private static bool TryParseProxyCheckResult(string raw, out ProxyCheckResult result)
    {
        try
        {
            result = JsonSerializer.Deserialize<ProxyCheckResult>(raw) ?? new ProxyCheckResult("unknown", "unknown", "unknown", "unknown", "unknown");
            return true;
        }
        catch
        {
            result = new ProxyCheckResult("unknown", "unknown", "unknown", "unknown", "unknown");
            return false;
        }
    }

    private static bool TryParseProxyCheckFailure(string raw, out ProxyCheckFailure failure)
    {
        try
        {
            failure = JsonSerializer.Deserialize<ProxyCheckFailure>(raw) ?? new ProxyCheckFailure("unknown", "unknown", "https://ipwho.is/");
            return true;
        }
        catch
        {
            failure = new ProxyCheckFailure(raw, "unknown", "https://ipwho.is/");
            return false;
        }
    }

    private string TryBuildProxyServerStringForDisplay()
    {
        var proxyServer = BuildProxyServerString();
        return string.IsNullOrWhiteSpace(proxyServer) ? "Direct" : ProxyParser.MaskForLog(proxyServer);
    }

    private static string BuildProxyCheckFailure(string error, string route)
        => JsonSerializer.Serialize(new ProxyCheckFailure(error, route, "https://ipwho.is/"));

    private static string SummarizeProxyCheckError(string? message)
    {
        var value = message ?? string.Empty;
        var firstLine = value.Replace("\r", string.Empty).Split('\n').FirstOrDefault() ?? string.Empty;
        return firstLine.Length == 0 ? "Unknown error." : firstLine;
    }

    private static ProxyCheckInfo ParseProxyCheckResponse(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var success = !root.TryGetProperty("success", out var successNode) || successNode.GetBoolean();
            if (!success)
            {
                var message = TryGetJsonString(root, "message");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "IP lookup failed." : message);
            }

            var ip = TryGetJsonString(root, "ip");
            var country = TryGetJsonString(root, "country");
            var region = TryGetJsonString(root, "region");
            var city = TryGetJsonString(root, "city");
            var isp = TryGetJsonString(root, "isp");
            var location = string.Join(", ", new[] { city, region, country }.Where(item => !string.IsNullOrWhiteSpace(item)));
            return new ProxyCheckInfo(
                string.IsNullOrWhiteSpace(ip) ? "unknown" : ip,
                string.IsNullOrWhiteSpace(location) ? "unknown" : location,
                string.IsNullOrWhiteSpace(isp) ? "unknown" : isp);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"IP lookup returned invalid data: {ex.Message}");
        }
    }

    private static string TryGetJsonString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;
    }

    private sealed record ProxyCheckInfo(string Ip, string Location, string Isp);
    private sealed record ProxyCheckResult(string Ip, string Location, string Isp, string Route, string Latency);
    private sealed record ProxyCheckFailure(string Error, string Route, string Target);
    private sealed record SavedProxyOption(ProxyLibraryEntry? Entry, string DisplayText);

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
