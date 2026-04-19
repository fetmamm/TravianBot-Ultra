using System.Text.Json.Nodes;
using System.Windows;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public partial class SettingsWindow : Window
{
    private readonly BotConfigStore _store;
    private JsonObject _config = [];

    public SettingsWindow(BotConfigStore store)
    {
        InitializeComponent();
        _store = store;
        LoadConfig();
    }

    private void LoadConfig()
    {
        _config = _store.Load();

        ServerNameTextBox.Text = _config["server_name"]?.GetValue<string>() ?? string.Empty;
        BaseUrlTextBox.Text = _config["base_url"]?.GetValue<string>() ?? string.Empty;
        LoginPathTextBox.Text = _config["login_path"]?.GetValue<string>() ?? "/login.php";
        VillagePathTextBox.Text = _config["village_overview_path"]?.GetValue<string>() ?? "/dorf1.php";
        TimeoutTextBox.Text = (_config["timeout_ms"]?.GetValue<int>() ?? 15000).ToString();
        ManualTimeoutTextBox.Text = (_config["manual_login_timeout_seconds"]?.GetValue<int>() ?? 180).ToString();
        LoopIntervalTextBox.Text = (_config["loop_interval_seconds"]?.GetValue<int>() ?? 60).ToString();
        HumanSpeedTextBox.Text = _config["human_like_speed"]?.GetValue<string>() ?? "medium";
        HeadlessCheckBox.IsChecked = _config["headless"]?.GetValue<bool>() ?? false;
        HumanLikeCheckBox.IsChecked = _config["human_like_enabled"]?.GetValue<bool>() ?? false;

        var tasks = _config["loop_tasks"] as JsonArray;
        LoopTasksTextBox.Text = tasks is null
            ? "status"
            : string.Join(",", tasks.Select(item => item?.GetValue<string>() ?? string.Empty).Where(item => item.Length > 0));

        InfoTextBlock.Text = "Server and Base URL are managed via the server dropdown/Accounts. Other values can be edited here.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(TimeoutTextBox.Text.Trim(), out var timeoutMs) || timeoutMs < 1000)
            {
                throw new InvalidOperationException("Timeout must be an integer >= 1000.");
            }

            if (!int.TryParse(ManualTimeoutTextBox.Text.Trim(), out var manualTimeout) || manualTimeout < 1)
            {
                throw new InvalidOperationException("Manual login timeout must be an integer >= 1.");
            }

            if (!int.TryParse(LoopIntervalTextBox.Text.Trim(), out var loopInterval) || loopInterval < 1)
            {
                throw new InvalidOperationException("Loop interval must be an integer >= 1.");
            }

            var tasks = LoopTasksTextBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(task => task.Trim())
                .Where(task => task.Length > 0)
                .ToList();
            if (tasks.Count == 0)
            {
                tasks.Add("status");
            }

            _config["server_name"] = ServerNameTextBox.Text.Trim();
            _config["base_url"] = BaseUrlTextBox.Text.Trim().TrimEnd('/');
            _config["login_path"] = LoginPathTextBox.Text.Trim();
            _config["village_overview_path"] = VillagePathTextBox.Text.Trim();
            _config["timeout_ms"] = timeoutMs;
            _config["manual_login_timeout_seconds"] = manualTimeout;
            _config["loop_interval_seconds"] = loopInterval;
            _config["human_like_speed"] = HumanSpeedTextBox.Text.Trim();
            _config["headless"] = HeadlessCheckBox.IsChecked == true;
            _config["human_like_enabled"] = HumanLikeCheckBox.IsChecked == true;

            var taskArray = new JsonArray();
            foreach (var task in tasks)
            {
                taskArray.Add(task);
            }

            _config["loop_tasks"] = taskArray;
            _store.Save(_config);

            InfoTextBlock.Text = "Settings saved.";
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
