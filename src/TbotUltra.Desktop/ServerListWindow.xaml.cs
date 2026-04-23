using System.Windows;
using System.ComponentModel;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class ServerListWindow : Window
{
    private readonly List<ServerOption> _sourceServers;
    private readonly List<ServerOption> _defaultServers;
    private readonly ServerCatalogStore _serverCatalogStore;
    private List<ServerOption> _workingServers = [];
    private string _savedSnapshot = string.Empty;
    private bool _isClosing;

    public ServerListWindow(IEnumerable<ServerOption> serverOptions, IEnumerable<ServerOption> defaultServers, ServerCatalogStore serverCatalogStore)
    {
        InitializeComponent();
        _sourceServers = serverOptions.ToList();
        _defaultServers = defaultServers.ToList();
        _serverCatalogStore = serverCatalogStore;
        ReloadWorkingCopy(_sourceServers);
        _savedSnapshot = BuildSnapshot();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var warning = MessageBox.Show(
            this,
            "Changing server URLs is not recommended and may break login or actions.\n\nPress OK to save anyway, or Cancel to abort.",
            "Warning",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (warning != MessageBoxResult.OK)
        {
            return;
        }

        var validated = new List<ServerOption>();
        foreach (var item in _workingServers)
        {
            var name = (item.Name ?? string.Empty).Trim();
            var url = (item.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (name.Length == 0 || url.Length == 0)
            {
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                MessageBox.Show(this, $"Invalid URL for '{name}'.", "Save server list", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            validated.Add(new ServerOption
            {
                Name = name,
                BaseUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            });
        }

        if (validated.Count == 0)
        {
            MessageBox.Show(this, "Server list cannot be empty.", "Save server list", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _sourceServers.Clear();
        _sourceServers.AddRange(validated.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        _serverCatalogStore.Save(_sourceServers);
        _savedSnapshot = BuildSnapshot();
        _isClosing = true;
        DialogResult = true;
        Close();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadWorkingCopy(_defaultServers);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PromptToSaveUnsavedChanges())
        {
            return;
        }

        _isClosing = true;
        DialogResult = false;
        Close();
    }

    private void ServerListWindow_Closing(object? sender, CancelEventArgs e)
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

    private void ReloadWorkingCopy(IEnumerable<ServerOption> source)
    {
        _workingServers = source
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.BaseUrl))
            .Select(item => new ServerOption
            {
                Name = item.Name.Trim(),
                BaseUrl = item.BaseUrl.Trim().TrimEnd('/'),
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ServerDataGrid.ItemsSource = _workingServers;
    }

    private bool PromptToSaveUnsavedChanges()
    {
        if (!HasUnsavedChanges())
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "You have unsaved server URL changes.\n\nYes: Save changes\nNo: Discard changes\nCancel: Stay on this window",
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
        return string.Join(
            "|",
            _workingServers
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{(item.Name ?? string.Empty).Trim()}::{(item.BaseUrl ?? string.Empty).Trim().TrimEnd('/')}"));
    }
}
