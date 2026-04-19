using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow : Window
{
    private readonly string _projectRoot;
    private readonly string _botConfigPath;
    private readonly string _envPath;
    private readonly BotConfigStore _botConfigStore;
    private readonly IAccountProvider _accountProvider;
    private readonly EnvAccountStore _accountStore;
    private readonly ServerDiscoveryService _serverDiscoveryService;
    private readonly BotTaskRunner _taskRunner;
    private readonly DispatcherTimer _clockTimer;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _chromiumEnsured;
    private bool _suppressServerSelectionChange;

    public MainWindow()
    {
        InitializeComponent();

        _projectRoot = ProjectRootLocator.FindProjectRoot();
        _botConfigPath = Path.Combine(_projectRoot, "config", "bot.json");
        _envPath = Path.Combine(_projectRoot, ".env");

        _botConfigStore = new BotConfigStore(_botConfigPath);
        _accountProvider = new EnvAccountProvider(_envPath);
        _accountStore = new EnvAccountStore(_envPath);
        _serverDiscoveryService = new ServerDiscoveryService();
        _taskRunner = new BotTaskRunner(_accountProvider, new ProjectContext(_projectRoot));

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            ClockTextBlock.Text = $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss} local";
        };
        _clockTimer.Start();

        VillageComboBox.ItemsSource = new[] { "-" };
        VillageComboBox.SelectedIndex = 0;

        LoadConfigToUi();
        Loaded += MainWindow_Loaded;
        AppendLog("Desktop app started.");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshServerDropdownAsync();
    }

    private BotOptions LoadBotOptions()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(_projectRoot)
            .AddJsonFile(_botConfigPath, optional: false, reloadOnChange: false)
            .Build();

        var tasks = configuration.GetSection("loop_tasks").Get<List<string>>() ?? ["status"];

        return new BotOptions
        {
            ServerName = configuration["server_name"] ?? string.Empty,
            BaseUrl = (configuration["base_url"] ?? string.Empty).TrimEnd('/'),
            LoginPath = configuration["login_path"] ?? "/login.php",
            VillageOverviewPath = configuration["village_overview_path"] ?? "/dorf1.php",
            Headless = configuration.GetValue("headless", false),
            TimeoutMs = configuration.GetValue("timeout_ms", 15000),
            ManualLoginTimeoutSeconds = configuration.GetValue("manual_login_timeout_seconds", 180),
            LoopIntervalSeconds = configuration.GetValue("loop_interval_seconds", 60),
            LoopTasks = tasks,
            GithubReleasesUrl = configuration["github_releases_url"] ?? string.Empty,
            HumanLikeEnabled = configuration.GetValue("human_like_enabled", false),
            HumanLikeSpeed = configuration["human_like_speed"] ?? "medium",
            ResourceUpgradeSlotId = configuration.GetValue<int?>("resource_upgrade_slot_id"),
            ResourceUpgradeTargetLevel = configuration.GetValue<int?>("resource_upgrade_target_level"),
            ResourceUpgradeMaxAttempts = configuration.GetValue("resource_upgrade_max_attempts", 30),
            BuildingUpgradeSlotId = configuration.GetValue<int?>("building_upgrade_slot_id"),
            BuildingUpgradeTargetLevel = configuration.GetValue<int?>("building_upgrade_target_level"),
            BuildingUpgradeMaxAttempts = configuration.GetValue("building_upgrade_max_attempts", 30),
            BuildingConstructSlotId = configuration.GetValue<int?>("building_construct_slot_id"),
            BuildingConstructGid = configuration.GetValue<int?>("building_construct_gid"),
            BuildingConstructName = configuration["building_construct_name"] ?? string.Empty,
        };
    }

    private void LoadConfigToUi()
    {
        var options = LoadBotOptions();
        _ = SetSelectedServerInDropdownAsync(options.ServerName, options.BaseUrl);

        try
        {
            var account = _accountProvider.LoadAccount();
            StatusTextBlock.Text = $"Loaded account '{account.Name}'.";
            AppendLog($"Loaded account '{account.Name}'.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Failed to load account.";
            AppendLog($"Account load failed: {ex.Message}");
        }
    }

    private async Task ExecuteTasksAsync(IEnumerable<string> tasks)
    {
        var options = LoadBotOptions();

        BrowserInfoTextBlock.Text = "Browser: running";
        await EnsureChromiumInstalledAsync();

        await _taskRunner.ExecuteOnceAsync(
            options,
            AppendLog,
            tasksOverride: tasks,
            accountName: null,
            cancellationToken: CancellationToken.None);

        LastScanInfoTextBlock.Text = $"Last scan: {DateTime.Now:HH:mm:ss}";
        BrowserInfoTextBlock.Text = "Browser: idle";
        SummaryTextBlock.Text = "Last action completed successfully.";
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleUiBusy(true);
        try
        {
            var options = CloneOptions(LoadBotOptions(), headlessOverride: false);
            BrowserInfoTextBlock.Text = "Browser: starting";

            await EnsureChromiumInstalledAsync();
            AppendLog("Login started.");
            await _taskRunner.ExecuteLoginAsync(options, AppendLog, null, CancellationToken.None);

            BrowserInfoTextBlock.Text = "Browser: idle";
            StatusTextBlock.Text = "Login completed.";
            AppendLog("Login finished.");
        }
        catch (Exception ex)
        {
            BrowserInfoTextBlock.Text = "Browser: error";
            StatusTextBlock.Text = "Login failed.";
            AppendLog($"Login failed: {ex.Message}");
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var account = _accountProvider.LoadAccount();
            var statePath = Path.Combine(_projectRoot, "playwright", ".auth", $"{account.Name}.json");
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
                AppendLog($"Removed saved browser session for account '{account.Name}'.");
                StatusTextBlock.Text = "Logged out (saved session removed).";
            }
            else
            {
                AppendLog($"No saved browser session found for account '{account.Name}'.");
                StatusTextBlock.Text = "No saved session found.";
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Logout failed: {ex.Message}");
            StatusTextBlock.Text = "Logout failed.";
        }
    }

    private async void AccountsButton_Click(object sender, RoutedEventArgs e)
    {
        var options = LoadBotOptions();
        var servers = await FetchServerOptionsAsync(options);
        var window = new AccountsWindow(_accountStore, options.ServerName, options.BaseUrl, servers)
        {
            Owner = this,
        };
        window.ShowDialog();
        LoadConfigToUi();
    }

    private async void CheckStatusButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleUiBusy(true);
        try
        {
            await ExecuteTasksAsync(["status"]);
        }
        catch (Exception ex)
        {
            AppendLog($"Check status failed: {ex.Message}");
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    private async void ScanAllVillagesButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleUiBusy(true);
        try
        {
            await ExecuteTasksAsync(["scan_all_villages"]);
        }
        catch (Exception ex)
        {
            AppendLog($"Scan all villages failed: {ex.Message}");
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    private async void OpenVerificationBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleUiBusy(true);
        try
        {
            var options = CloneOptions(LoadBotOptions(), headlessOverride: false);
            await EnsureChromiumInstalledAsync();
            AppendLog("Opening verification browser via login flow...");
            await _taskRunner.ExecuteLoginAsync(options, AppendLog, null, CancellationToken.None);
            AppendLog("Verification browser action completed.");
        }
        catch (Exception ex)
        {
            AppendLog($"Verification browser failed: {ex.Message}");
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    private async void RunOnceButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleUiBusy(true);
        try
        {
            var options = LoadBotOptions();
            await ExecuteTasksAsync(options.LoopTasks);
        }
        catch (Exception ex)
        {
            AppendLog($"Run once failed: {ex.Message}");
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    private async void StartLoopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            return;
        }

        var options = LoadBotOptions();
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;

        StartLoopButton.IsEnabled = false;
        StopLoopButton.IsEnabled = true;
        LoopStateTextBlock.Text = "Loop: running";
        AppendLog($"Loop started. Interval={options.LoopIntervalSeconds}s");

        _loopTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await EnsureChromiumInstalledAsync();
                    await _taskRunner.ExecuteOnceAsync(options, AppendLog, options.LoopTasks, null, token);
                    Dispatcher.Invoke(() => LastScanInfoTextBlock.Text = $"Last scan: {DateTime.Now:HH:mm:ss}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppendLog($"Loop tick failed: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.LoopIntervalSeconds), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);

        try
        {
            await _loopTask;
        }
        finally
        {
            StartLoopButton.IsEnabled = true;
            StopLoopButton.IsEnabled = false;
            LoopStateTextBlock.Text = "Loop: stopped";
            AppendLog("Loop stopped.");
        }
    }

    private void StopLoopButton_Click(object sender, RoutedEventArgs e)
    {
        _loopCts?.Cancel();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleUiBusy(true);
        try
        {
            await EnsureChromiumInstalledAsync(forceInstall: true);
        }
        catch (Exception ex)
        {
            AppendLog($"Install Chromium failed: {ex.Message}");
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Use Login first.\nThen run Check village status or Scan all villages.\nStart/Stop controls the loop.",
            "Help",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        LoadConfigToUi();
        AppendLog("Config reloaded from config/bot.json.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_botConfigStore)
        {
            Owner = this,
        };
        window.ShowDialog();
        LoadConfigToUi();
    }

    private async void ServerComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressServerSelectionChange)
        {
            return;
        }

        if (ServerComboBox.SelectedItem is not ServerOption selected)
        {
            return;
        }

        try
        {
            var config = _botConfigStore.Load();
            var previousServer = config["server_name"]?.GetValue<string>() ?? string.Empty;
            var previousUrl = (config["base_url"]?.GetValue<string>() ?? string.Empty).TrimEnd('/');

            var newServer = selected.Name.Trim();
            var newUrl = selected.BaseUrl.TrimEnd('/');
            if (string.Equals(previousServer, newServer, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previousUrl, newUrl, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            config["server_name"] = newServer;
            config["base_url"] = newUrl;
            _botConfigStore.Save(config);

            AppendLog($"Server changed to {newServer} ({newUrl}).");
            await SetSelectedServerInDropdownAsync(newServer, newUrl);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save selected server: {ex.Message}");
        }
    }

    private async Task RefreshServerDropdownAsync()
    {
        var options = LoadBotOptions();
        var servers = await FetchServerOptionsAsync(options);
        if (!servers.Any(item => string.Equals(item.BaseUrl, options.BaseUrl, StringComparison.OrdinalIgnoreCase)))
        {
            servers.Add(new ServerOption
            {
                Name = options.ServerName,
                BaseUrl = options.BaseUrl,
            });
        }

        _suppressServerSelectionChange = true;
        try
        {
            ServerComboBox.ItemsSource = servers
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _suppressServerSelectionChange = false;
        }

        await SetSelectedServerInDropdownAsync(options.ServerName, options.BaseUrl);
    }

    private Task SetSelectedServerInDropdownAsync(string serverName, string baseUrl)
    {
        var items = ServerComboBox.ItemsSource as IEnumerable<ServerOption>;
        if (items is null)
        {
            return Task.CompletedTask;
        }

        var normalizedUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        var selected = items.FirstOrDefault(item =>
                string.Equals(item.BaseUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(item =>
                string.Equals(item.Name, serverName, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            return Task.CompletedTask;
        }

        _suppressServerSelectionChange = true;
        try
        {
            ServerComboBox.SelectedItem = selected;
        }
        finally
        {
            _suppressServerSelectionChange = false;
        }

        return Task.CompletedTask;
    }

    private async Task<List<ServerOption>> FetchServerOptionsAsync(BotOptions options)
    {
        try
        {
            var servers = await _serverDiscoveryService.FetchServersAsync();
            if (servers.Count > 0)
            {
                return servers;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Server discovery failed, using fallback: {ex.Message}");
        }

        return
        [
            new ServerOption
            {
                Name = options.ServerName,
                BaseUrl = options.BaseUrl,
            },
        ];
    }

    private void ToggleUiBusy(bool busy)
    {
        AccountsButton.IsEnabled = !busy;
        LoginButton.IsEnabled = !busy;
        LogoutButton.IsEnabled = !busy;
        CheckStatusButton.IsEnabled = !busy;
        RunOnceButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        ScanAllVillagesButton.IsEnabled = !busy;
        OpenVerificationBrowserButton.IsEnabled = !busy;
        HelpButton.IsEnabled = !busy;
        ReloadButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;

        if (_loopTask is null || _loopTask.IsCompleted)
        {
            StartLoopButton.IsEnabled = !busy;
            StopLoopButton.IsEnabled = false;
        }
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            if (LogTextBox.Text.Length > 0)
            {
                LogTextBox.AppendText(Environment.NewLine);
            }

            LogTextBox.AppendText(line);
            LogTextBox.ScrollToEnd();
            StatusTextBlock.Text = message;
        });
    }

    private async Task EnsureChromiumInstalledAsync(bool forceInstall = false)
    {
        if (!forceInstall)
        {
            if (_chromiumEnsured || ChromiumAlreadyInstalled())
            {
                _chromiumEnsured = true;
                return;
            }
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "playwright.ps1");
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException("Could not find playwright.ps1 in desktop output folder. Build app first.");
        }

        AppendLog(forceInstall ? "Installing Chromium (forced)..." : "Chromium missing. Installing...");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" install chromium",
            WorkingDirectory = _projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (!string.IsNullOrWhiteSpace(output))
        {
            AppendLog(output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            AppendLog(error.Trim());
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Chromium install failed with exit code {process.ExitCode}.");
        }

        _chromiumEnsured = true;
        AppendLog("Chromium install complete.");
    }

    private static bool ChromiumAlreadyInstalled()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return false;
            }

            var playwrightRoot = Path.Combine(localAppData, "ms-playwright");
            if (!Directory.Exists(playwrightRoot))
            {
                return false;
            }

            var executables = Directory.GetFiles(playwrightRoot, "chrome.exe", SearchOption.AllDirectories);
            return executables.Any(path =>
                path.Contains("chromium-", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("chrome-win", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static BotOptions CloneOptions(BotOptions source, bool? headlessOverride = null)
    {
        return new BotOptions
        {
            ServerName = source.ServerName,
            BaseUrl = source.BaseUrl,
            LoginPath = source.LoginPath,
            VillageOverviewPath = source.VillageOverviewPath,
            Headless = headlessOverride ?? source.Headless,
            TimeoutMs = source.TimeoutMs,
            ManualLoginTimeoutSeconds = source.ManualLoginTimeoutSeconds,
            LoopIntervalSeconds = source.LoopIntervalSeconds,
            LoopTasks = source.LoopTasks,
            GithubReleasesUrl = source.GithubReleasesUrl,
            HumanLikeEnabled = source.HumanLikeEnabled,
            HumanLikeSpeed = source.HumanLikeSpeed,
            ResourceUpgradeSlotId = source.ResourceUpgradeSlotId,
            ResourceUpgradeTargetLevel = source.ResourceUpgradeTargetLevel,
            ResourceUpgradeMaxAttempts = source.ResourceUpgradeMaxAttempts,
            BuildingUpgradeSlotId = source.BuildingUpgradeSlotId,
            BuildingUpgradeTargetLevel = source.BuildingUpgradeTargetLevel,
            BuildingUpgradeMaxAttempts = source.BuildingUpgradeMaxAttempts,
            BuildingConstructSlotId = source.BuildingConstructSlotId,
            BuildingConstructGid = source.BuildingConstructGid,
            BuildingConstructName = source.BuildingConstructName,
        };
    }
}
