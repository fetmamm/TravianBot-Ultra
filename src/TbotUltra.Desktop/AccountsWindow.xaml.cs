using System.ComponentModel;
using System.Windows;
using TbotUltra.Core.Accounts;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class AccountsWindow : Window
{
    private readonly EnvAccountStore _store;
    private readonly AccountDeletionService _deletionService;
    private readonly ServerCatalogStore _serverCatalogStore;
    private readonly string _defaultServerName;
    private readonly string _defaultServerUrl;
    private readonly List<ServerOption> _serverOptions;
    private readonly List<ServerOption> _defaultServerOptions;

    private List<AccountEntry> _accounts = [];
    private string _activeAccountName = string.Empty;
    private bool _showPassword;
    private bool _editingExistingAccount;
    private string _editingOriginalName = string.Empty;
    private string _editingOriginalServerName = string.Empty;
    private string _selectedAccountName = string.Empty;
    private string _baselineUsername = string.Empty;
    private string _baselinePassword = string.Empty;
    private string _baselineServerUrl = string.Empty;
    private bool _suppressSelectionChanged;
    private bool _isClosing;

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

            var setActiveForSave = !_editingExistingAccount && _accounts.Count == 0;
            _store.SaveAccount(entry, setActive: setActiveForSave);
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
        SelectServer(_defaultServerName, _defaultServerUrl);
        ServerComboBox.IsEnabled = true;
        CaptureBaseline();
        UpdateActionButtons();
    }

    private void EnsureServerListContainsDefaults()
    {
        if (_serverOptions.Count == 0)
        {
            _serverOptions.Add(new ServerOption { Name = _defaultServerName, BaseUrl = _defaultServerUrl });
        }

        if (!_serverOptions.Any(option => string.Equals(option.BaseUrl, _defaultServerUrl, StringComparison.OrdinalIgnoreCase)))
        {
            _serverOptions.Add(new ServerOption { Name = _defaultServerName, BaseUrl = _defaultServerUrl });
        }

        ServerComboBox.ItemsSource = null;
        ServerComboBox.ItemsSource = _serverOptions
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SelectServer(string serverName, string serverUrl)
    {
        var match = _serverOptions.FirstOrDefault(option =>
            string.Equals(option.BaseUrl, serverUrl, StringComparison.OrdinalIgnoreCase))
            ?? _serverOptions.FirstOrDefault(option =>
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
    }

    private bool HasUnsavedChanges()
    {
        var currentUsername = UsernameTextBox.Text.Trim();
        var currentPassword = (_showPassword ? PasswordTextBox.Text : PasswordBox.Password) ?? string.Empty;
        var currentServerUrl = (ServerComboBox.SelectedItem as ServerOption)?.BaseUrl?.Trim() ?? string.Empty;

        return !string.Equals(currentUsername, _baselineUsername, StringComparison.Ordinal)
            || !string.Equals(currentPassword, _baselinePassword, StringComparison.Ordinal)
            || !string.Equals(currentServerUrl, _baselineServerUrl, StringComparison.OrdinalIgnoreCase);
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
