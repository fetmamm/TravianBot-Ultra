using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class AccountsWindow : Window
{
    private readonly EnvAccountStore _store;
    private readonly string _defaultServerName;
    private readonly string _defaultServerUrl;
    private readonly List<ServerOption> _serverOptions;
    private string _activeAccountName = string.Empty;
    private List<AccountEntry> _accounts = [];

    public AccountsWindow(
        EnvAccountStore store,
        string defaultServerName,
        string defaultServerUrl,
        IEnumerable<ServerOption> serverOptions)
    {
        InitializeComponent();
        _store = store;
        _defaultServerName = defaultServerName;
        _defaultServerUrl = defaultServerUrl;
        _serverOptions = serverOptions.ToList();

        Reload();
    }

    private void Reload()
    {
        _accounts = _store.ListAccounts();
        _activeAccountName = _store.ActiveAccountName();
        EnsureServerListContainsDefaults();

        AccountsListBox.ItemsSource = null;
        AccountsListBox.ItemsSource = _accounts;

        if (_accounts.Count > 0)
        {
            var selected = _accounts.FirstOrDefault(a => string.Equals(a.Name, _activeAccountName, StringComparison.OrdinalIgnoreCase))
                ?? _accounts[0];
            AccountsListBox.SelectedItem = selected;
        }
        else
        {
            ClearEditor();
        }

        InfoTextBlock.Text = $"Active account: {_activeAccountName}";
    }

    private void AccountsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AccountsListBox.SelectedItem is not AccountEntry selected)
        {
            return;
        }

        NameTextBox.Text = selected.Name;
        UsernameTextBox.Text = selected.Username;
        PasswordBox.Password = selected.Password;
        var serverName = string.IsNullOrWhiteSpace(selected.ServerName) ? _defaultServerName : selected.ServerName;
        var serverUrl = string.IsNullOrWhiteSpace(selected.ServerUrl) ? _defaultServerUrl : selected.ServerUrl;
        SelectServer(serverName, serverUrl);
        SetActiveCheckBox.IsChecked = string.Equals(selected.Name, _activeAccountName, StringComparison.OrdinalIgnoreCase);
    }

    private void ServerComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ServerComboBox.SelectedItem is ServerOption option)
        {
            ServerUrlTextBox.Text = option.BaseUrl;
        }
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        ClearEditor();
        NameTextBox.Focus();
        InfoTextBlock.Text = "Create a new account entry.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = ReadEditor();
            _store.SaveAccount(entry, SetActiveCheckBox.IsChecked == true);
            Reload();
            SelectByName(entry.Name);
            InfoTextBlock.Text = $"Saved account '{entry.Name}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save account", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            return;
        }

        var name = NameTextBox.Text.Trim();
        var confirm = MessageBox.Show(this, $"Delete account '{name}'?", "Delete account", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _store.DeleteAccount(name);
            Reload();
            InfoTextBlock.Text = $"Deleted account '{name}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete account", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetActiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            return;
        }

        var name = NameTextBox.Text.Trim();
        try
        {
            _store.SetActive(name);
            Reload();
            SelectByName(name);
            InfoTextBlock.Text = $"Active account set to '{name}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Set active account", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private AccountEntry ReadEditor()
    {
        var name = NameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Account name is required.");
        }

        var username = UsernameTextBox.Text.Trim();
        var password = PasswordBox.Password;
        if (username.Length == 0 || password.Length == 0)
        {
            throw new InvalidOperationException("Username and password are required.");
        }

        var selectedServer = ServerComboBox.SelectedItem as ServerOption;
        var serverName = selectedServer?.Name ?? _defaultServerName;
        var serverUrl = selectedServer?.BaseUrl ?? _defaultServerUrl;

        return new AccountEntry
        {
            Name = name,
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
        NameTextBox.Text = string.Empty;
        UsernameTextBox.Text = string.Empty;
        PasswordBox.Password = string.Empty;
        SelectServer(_defaultServerName, _defaultServerUrl);
        SetActiveCheckBox.IsChecked = false;
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
        ServerUrlTextBox.Text = match.BaseUrl;
    }
}
