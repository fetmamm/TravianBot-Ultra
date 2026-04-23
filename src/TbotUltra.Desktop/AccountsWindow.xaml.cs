using System.ComponentModel;
using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class AccountsWindow : Window
{
    private readonly EnvAccountStore _store;
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
        ServerCatalogStore serverCatalogStore,
        string defaultServerName,
        string defaultServerUrl,
        IEnumerable<ServerOption> serverOptions,
        IEnumerable<ServerOption> defaultServerOptions)
    {
        InitializeComponent();
        _store = store;
        _serverCatalogStore = serverCatalogStore;
        _defaultServerName = defaultServerName;
        _defaultServerUrl = defaultServerUrl;
        _serverOptions = serverOptions.ToList();
        _defaultServerOptions = defaultServerOptions.ToList();
        Reload();
    }

    private void Reload()
    {
        _accounts = _store.ListAccounts();
        _activeAccountName = _store.ActiveAccountName();
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

        InfoTextBlock.Text = $"Active account: {_activeAccountName}";
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
            MessageBox.Show(this, ex.Message, "Set active account", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            _store.SaveAccount(entry, setActive: false);
            Reload();
            SelectByName(entry.Name);
            InfoTextBlock.Text = isUpdate
                ? $"Updated account '{entry.Username}'."
                : $"Saved account '{entry.Username}'.";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save account", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsListBox.SelectedItem is not AccountEntry selected)
        {
            return;
        }

        var confirm = MessageBox.Show(this, $"Delete account '{selected.Username}'?", "Delete account", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            // Deleting an account should not trigger an extra unsaved-changes prompt
            // when the selection is reloaded after removal.
            _selectedAccountName = string.Empty;
            _store.DeleteAccount(selected.Name);
            Reload();
            InfoTextBlock.Text = $"Deleted account '{selected.Username}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete account", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var previouslySelectedName = (ServerComboBox.SelectedItem as ServerOption)?.Name ?? _defaultServerName;
            var previouslySelectedUrl = (ServerComboBox.SelectedItem as ServerOption)?.BaseUrl ?? _defaultServerUrl;
            EnsureServerListContainsDefaults();
            if (_editingExistingAccount)
            {
                SelectServer(_editingOriginalServerName, previouslySelectedUrl);
            }
            else
            {
                SelectServer(previouslySelectedName, previouslySelectedUrl);
            }
        }
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
            : NormalizeAccountNameFromUsername(username);

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

        var result = MessageBox.Show(
            this,
            "You have unsaved changes.\n\nYes: Save changes\nNo: Discard changes\nCancel: Stay on this window",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

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

    private static string NormalizeAccountNameFromUsername(string username)
    {
        var chars = username.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var joined = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (joined.Length == 0)
        {
            throw new InvalidOperationException("Username cannot be normalized to account key.");
        }

        return joined;
    }
}
