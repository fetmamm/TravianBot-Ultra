using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow : Window
{
    private const string ManageAccountsOptionName = "__manage_accounts__";
    private readonly string _projectRoot;
    private readonly string _botConfigPath;
    private readonly string _envPath;
    private readonly string _queuePath;
    private readonly string _serverCatalogPath;
    private readonly BotConfigStore _botConfigStore;
    private readonly IAccountProvider _accountProvider;
    private readonly EnvAccountStore _accountStore;
    private readonly AccountAnalysisStore _accountAnalysisStore;
    private readonly ServerDiscoveryService _serverDiscoveryService;
    private readonly ServerCatalogStore _serverCatalogStore;
    private readonly IDesktopBotService _botService;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly DispatcherTimer _inboxRefreshTimer;
    private readonly DispatcherTimer _buildQueueCountdownTimer;
    private readonly ObservableCollection<string> _terminalEntries = [];
    private readonly ObservableCollection<string> _alarmEntries = [];
    private readonly ObservableCollection<ResourceFieldRow> _woodFields = [];
    private readonly ObservableCollection<ResourceFieldRow> _clayFields = [];
    private readonly ObservableCollection<ResourceFieldRow> _ironFields = [];
    private readonly ObservableCollection<ResourceFieldRow> _croplandFields = [];
    private readonly ObservableCollection<BuildingSlotRow> _buildingRows = [];
    private readonly ObservableCollection<BuildingCatalogOption> _buildingCatalogOptions = [];
    private readonly ObservableCollection<BuildingSlotRow> _demolishableBuildings = [];
    private readonly Dictionary<int, DateTimeOffset> _resourceClickCooldownBySlot = new();
    private readonly Dictionary<int, (int Target, DateTimeOffset At)> _resourceLastQueuedTargetBySlot = new();
    private readonly HashSet<string> _analysisPromptDismissed = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _operationCts;
    private Task? _loopTask;
    private bool _chromiumEnsured;
    private bool _suppressAccountSelectionChange;
    private TimeSpan _queueServerTimeOffset;
    private int _lastUnreadMessages;
    private int _lastUnreadReports;
    private long _operationCounter;
    private long _loopTickCounter;
    private readonly SemaphoreSlim _queueAutoRunGate = new(1, 1);
    private readonly CancellationTokenSource _queueAutoRunCts = new();
    private CancellationTokenSource? _autoQueueRunCts;
    private readonly SemaphoreSlim _inboxRefreshGate = new(1, 1);
    private Window? _logsPopupWindow;
    private volatile bool _autoQueueRunning;
    private volatile bool _uiBusy;
    private volatile bool _isAppClosing;
    private volatile bool _inboxAutoEnabled;
    private volatile bool _loopStopRequested;
    private volatile bool _queueStopRequested;
    private int _buildQueueRemainingSeconds = -1;
    private int _buildQueueActiveCount;
    private int _unacknowledgedAlarmCount;
    private DateTimeOffset _lastVerificationPopupAt = DateTimeOffset.MinValue;
    private VillageStatus? _lastBuildingStatus;

    public ObservableCollection<ResourceFieldRow> WoodFields => _woodFields;
    public ObservableCollection<ResourceFieldRow> ClayFields => _clayFields;
    public ObservableCollection<ResourceFieldRow> IronFields => _ironFields;
    public ObservableCollection<ResourceFieldRow> CroplandFields => _croplandFields;

    public MainWindow()
    {
        InitializeComponent();
        TryApplyWindowIcon();

        _projectRoot = ProjectRootLocator.FindProjectRoot();
        _botConfigPath = Path.Combine(_projectRoot, "config", "bot.json");
        _envPath = Path.Combine(_projectRoot, ".env");
        _queuePath = Path.Combine(_projectRoot, "config", "queue.json");
        _serverCatalogPath = Path.Combine(_projectRoot, "config", "servers.user.json");

        _botConfigStore = new BotConfigStore(_botConfigPath);
        _accountProvider = new EnvAccountProvider(_envPath);
        _accountStore = new EnvAccountStore(_envPath);
        _accountAnalysisStore = new AccountAnalysisStore(_projectRoot);
        _serverDiscoveryService = new ServerDiscoveryService();
        _serverCatalogStore = new ServerCatalogStore(_serverCatalogPath);
        var taskRunner = new BotTaskRunner(_accountProvider, new ProjectContext(_projectRoot));
        var queueStore = new JsonQueueStore(_queuePath);
        var queueScheduler = new PriorityFifoQueueScheduler();
        var queueExecutor = new QueueExecutor(taskRunner);
        _botService = new DesktopBotService(taskRunner, queueStore, queueScheduler, queueExecutor);
        if (ShouldClearQueueOnStartup())
        {
            _botService.ClearQueue();
        }

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClockText();
            HandleBrowserClosedSignal();
        };
        _clockTimer.Start();

        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyFeedbackTextBlock.Visibility = Visibility.Collapsed;
        };
        _inboxRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _inboxRefreshTimer.Tick += async (_, _) => await HandleInboxRefreshTickAsync();
        _buildQueueCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _buildQueueCountdownTimer.Tick += (_, _) => TickBuildQueueCountdown();
        _buildQueueCountdownTimer.Start();

        _queueServerTimeOffset = ResolveQueueServerTimeOffset();

        TerminalListBox.ItemsSource = _terminalEntries;
        AlarmListBox.ItemsSource = _alarmEntries;
        AlarmListBox.SelectionChanged += (_, _) => UpdateTerminalAlarmUi();
        TerminalAlarmTabControl.SelectionChanged += (_, _) => UpdateTerminalAlarmUi();

        VillageComboBox.ItemsSource = new[]
        {
            new VillageSelectionItem { Name = "-", Url = string.Empty },
        };
        VillageComboBox.SelectedIndex = 0;
        VillageComboBox.SelectionChanged += VillageComboBox_SelectionChanged;
        ResourceTargetLevelComboBox.ItemsSource = Enumerable.Range(1, 20).ToList();
        ResourceTargetLevelComboBox.SelectedItem = 10;
        BuildingCategoryComboBox.ItemsSource = new[] { "all", "infrastructure", "army_buildings", "resource_buildings" };
        BuildingCategoryComboBox.SelectedIndex = 0;
        BuildingsDataGrid.ItemsSource = _buildingRows;
        ConstructBuildingComboBox.ItemsSource = _buildingCatalogOptions;
        DemolishBuildingComboBox.ItemsSource = _demolishableBuildings;

        LoadConfigToUi();
        RefreshQueueUi();
        Closing += MainWindow_Closing;
        SetLoopIndicator(false);
        UpdateInboxButtons(0, 0);
        _inboxRefreshTimer.Start();
        AppendLog("Desktop app started.");
        UpdateTerminalAlarmUi();
        UpdateLoginButtonsVisual(false);
    }

    private void TryApplyWindowIcon()
    {
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/IMAGE_APP.ico", UriKind.Absolute));
        }
        catch
        {
            // Keep default icon if resource icon cannot be loaded.
        }
    }

    private BotOptions LoadBotOptions()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(_projectRoot)
            .AddJsonFile(_botConfigPath, optional: false, reloadOnChange: false)
            .Build();
        return BotOptionsFactory.FromConfiguration(configuration);
    }

    private void LoadConfigToUi()
    {
        _queueServerTimeOffset = ResolveQueueServerTimeOffset();
        UpdateClockText();
        RefreshAccountPicker();
        SyncServerFromActiveAccount();

        var options = ApplySelectedVillageToOptions(LoadBotOptions());

        try
        {
            var account = _accountProvider.LoadAccount();
            StatusTextBlock.Text = $"Loaded account '{account.Name}'.";
            AppendLog($"Loaded account '{account.Name}'.");
            UpdateAccountInfoLabel(account.Name);
            UpdateInboxButtons(0, 0);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Failed to load account.";
            AppendLog($"Account load failed: {ex.Message}");
            UpdateInboxButtons(0, 0);
        }

        _ = PromptAccountAnalysisIfNeededAsync(_accountStore.ActiveAccountName());
    }

    private void EnqueueQuickTask(string taskName, string description, Dictionary<string, string>? payload = null)
    {
        try
        {
            payload ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var selectedVillageName = GetSelectedVillageName();
            var selectedVillageUrl = GetSelectedVillageUrl();
            if (!string.IsNullOrWhiteSpace(selectedVillageName))
            {
                payload[BotOptionPayloadKeys.TargetVillageName] = selectedVillageName;
            }

            if (!string.IsNullOrWhiteSpace(selectedVillageUrl))
            {
                payload[BotOptionPayloadKeys.TargetVillageUrl] = selectedVillageUrl;
            }

            var item = _botService.Enqueue(taskName, payload, priority: 0, maxRetries: 3);
            RefreshQueueUi(selectId: item.Id);
            AppendLog($"Queued task: {taskName} (priority={item.Priority}, maxRetries={item.MaxRetries}).");
            _ = TriggerQueueAutoRunAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not queue task '{taskName}': {ex.Message}");
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("Login");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            BrowserInfoTextBlock.Text = "Browser: starting";

            await EnsureChromiumInstalledAsync();
            var alreadyLoggedIn = await _botService.IsLoggedInAsync(options, AppendLog, operationToken);
            if (alreadyLoggedIn)
            {
                AppendLog("Already logged in. Skipping login submit.");
            }
            else
            {
                AppendLog("Login started.");
                await _botService.ExecuteLoginAsync(
                    options,
                    AppendLog,
                    keepBrowserOpenAfterLogin: !options.Headless,
                    cancellationToken: operationToken);
                AppendLog("Login finished.");
            }

            BrowserInfoTextBlock.Text = "Browser: idle";
            StatusTextBlock.Text = "Login completed.";
            UpdateLoginButtonsVisual(true);
            _inboxAutoEnabled = true;
            await RefreshInboxIndicatorsAsync(logErrors: true, force: true);
            await LoadResourcesAfterUpgradeAsync(operationToken, resourceOnly: true);
            CompleteOperation(operationId, operationSw, "Login completed.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Login paused.";
            AppendLog("Login paused.");
        }
        catch (Exception ex)
        {
            BrowserInfoTextBlock.Text = "Browser: error";
            StatusTextBlock.Text = "Login failed.";
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("Logout");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            await EnsureChromiumInstalledAsync();
            await _botService.ExecuteLogoutAsync(options, AppendLog, operationToken);

            var account = _accountProvider.LoadAccount();
            var statePath = Path.Combine(_projectRoot, "playwright", ".auth", $"{account.Name}.json");
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
                AppendLog($"Removed saved browser session for account '{account.Name}'.");
            }

            StatusTextBlock.Text = "Logged out.";
            UpdateLoginButtonsVisual(false);
            _inboxAutoEnabled = false;
            UpdateInboxButtons(0, 0);
            CompleteOperation(operationId, operationSw, "Logout completed.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Logout paused.";
            AppendLog("Logout paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            StatusTextBlock.Text = "Logout failed.";
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void AccountsButton_Click(object sender, RoutedEventArgs e)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var defaultServers = await FetchDefaultServerOptionsAsync(options);
        var servers = FetchEffectiveServerOptions(defaultServers);
        var window = new AccountsWindow(_accountStore, _serverCatalogStore, options.ServerName, options.BaseUrl, servers, defaultServers)
        {
            Owner = this,
        };
        window.ShowDialog();
        LoadConfigToUi();
    }

    private void AccountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccountSelectionChange)
        {
            return;
        }

        if (AccountComboBox.SelectedItem is not AccountEntry selected)
        {
            return;
        }

        if (string.Equals(selected.Name, ManageAccountsOptionName, StringComparison.OrdinalIgnoreCase))
        {
            RefreshAccountPicker();
            AccountsButton_Click(sender, new RoutedEventArgs());
            return;
        }

        try
        {
            var current = _accountStore.ActiveAccountName();
            if (string.Equals(current, selected.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _accountStore.SetActive(selected.Name);
            AppendLog($"Active account changed to '{selected.Name}'.");
            StatusTextBlock.Text = $"Active account: {selected.Name}";
            SyncServerFromActiveAccount();
            LoadConfigToUi();
            _inboxAutoEnabled = false;
            UpdateInboxButtons(0, 0);
            _ = PromptAccountAnalysisIfNeededAsync(selected.Name);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not change active account: {ex.Message}");
            RefreshAccountPicker();
        }
    }

    private void CheckStatusButton_Click(object sender, RoutedEventArgs e) => EnqueueQuickTask("status", "Check village status");

    private void ScanAllVillagesButton_Click(object sender, RoutedEventArgs e) => EnqueueQuickTask("scan_all_villages", "Scan all villages");

    private async void LoadResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("LoadResources");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            await EnsureChromiumInstalledAsync();
            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: true);

            var rows = status.ResourceFields
                .Where(item => item.SlotId is not null)
                .OrderBy(item => item.SlotId)
                .Select(item => new ResourceFieldRow
                {
                    SlotId = item.SlotId ?? 0,
                    FieldType = item.FieldType,
                    Name = item.Name,
                    Level = item.Level,
                    Url = item.Url ?? string.Empty,
                    PendingTargetLevel = null,
                    IsMaxLevel = (item.Level ?? 0) >= 20,
                })
                .ToList();

            SetResourceRows(rows);
            ApplyVillageStatusToUi(status);
            var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
            ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
            _inboxAutoEnabled = true;
            UpdateInboxButtons(status.UnreadMessages, status.UnreadReports);
            AppendLog($"Resources loaded: {rows.Count} fields.");
            CompleteOperation(operationId, operationSw, $"Loaded {rows.Count} fields.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Load resources paused.";
            AppendLog("Load resources paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void UpgradeAllResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResources");
        if (ResourceTargetLevelComboBox.SelectedItem is not int targetLevel)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | No target level selected.");
            return;
        }

        try
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = targetLevel.ToString(),
            };
            EnqueueQuickTask("upgrade_all_resources_to_level", $"Upgrade all resources to level {targetLevel}", payload);
            AppendLog($"[{operationId}] OK 0.0s | Queued upgrade-all to level {targetLevel}. Auto-run started.");
        }
        catch (Exception ex)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | {FormatExceptionForLog(ex)}");
        }
    }

    private void OpenVerificationBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenVerificationBrowser();
    }

    private void StartLoopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoQueueRunning)
        {
            _queueStopRequested = true;
            AppendLog("Pause requested. Queue will stop after current action.");
            return;
        }

        if (_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            AppendLog("A function is currently running. It will complete before bot can pause/start.");
            return;
        }

        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            _loopStopRequested = true;
            AppendLog("Pause requested. Loop will stop after current action.");
            return;
        }

        var initialOptions = LoadBotOptions();
        _loopStopRequested = false;
        _queueStopRequested = false;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;

        StartLoopButton.Content = "Pause bot";
        StartLoopButton.IsEnabled = true;
        SetLoopIndicator(true);
        AppendLog($"Loop started. Interval={initialOptions.LoopIntervalSeconds}s");

        _loopTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_loopStopRequested)
                {
                    AppendLog("Loop stop requested. Exiting after current action.");
                    break;
                }

                var loopDelaySeconds = 60;
                var tickId = System.Threading.Interlocked.Increment(ref _loopTickCounter);
                var tickSw = Stopwatch.StartNew();
                var tickOutcome = "idle";
                try
                {
                    var options = ApplySelectedVillageToOptions(LoadBotOptions());
                    loopDelaySeconds = options.LoopIntervalSeconds;
                    AppendLog($"[LOOP {tickId}] START interval={loopDelaySeconds}s, headless={options.Headless}");
                    await EnsureChromiumInstalledAsync();
                        var next = _botService.SelectNextQueueItem();
                        if (next is not null)
                        {
                        var terminalCountBefore = await Dispatcher.InvokeAsync(() => _terminalEntries.Count);
                        tickOutcome = $"queue:{next.TaskName}";
                        AppendLog($"[LOOP {tickId}] PICK queue item id={next.Id}, task={next.TaskName}, retries={next.Retries}/{next.MaxRetries}");
                        _botService.MarkQueueItemRunning(next.Id);
                        RefreshQueueUiOnUiThread(next.Id);

                        try
                        {
                            await _botService.ExecuteQueueItemAsync(options, next, AppendLog, token);
                            _botService.MarkQueueItemSucceeded(next.Id);
                            if (IsResourceUpgradeTask(next.TaskName))
                            {
                                var fastUpdated = await TryApplyFastResourceLevelUpdateAsync(next.TaskName, terminalCountBefore);
                                if (!fastUpdated)
                                {
                    await LoadResourcesAfterUpgradeAsync(token, resourceOnly: true);
                                }
                            }
                            AppendLog($"Queue item succeeded: {next.TaskName}");
                        }
                        catch (Exception ex)
                        {
                            if (TryExtractQueueWaitDelay(ex.Message, out var queueWaitDelay))
                            {
                                var deferred = _botService.MarkQueueItemDeferred(next.Id, queueWaitDelay);
                                if (deferred)
                                {
                                    AppendLog($"Queue item deferred: {next.TaskName}. Next try in {queueWaitDelay.TotalSeconds:F0}s.");
                                }
                                else
                                {
                                    _botService.MarkQueueItemExecutionFailed(next.Id);
                                    AppendLog($"Queue item failed: {next.TaskName}. {ex.Message}");
                                    RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                                }
                            }
                            else
                            {
                                _botService.MarkQueueItemExecutionFailed(next.Id);
                                AppendLog($"Queue item failed: {next.TaskName}. {ex.Message}");
                                RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                            }
                        }
                        finally
                        {
                            RefreshQueueUiOnUiThread(next.Id);
                        }
                    }
                    else
                    {
                        tickOutcome = "fallback";
                        await _botService.ExecuteFallbackTasksAsync(options, AppendLog, token);
                    }

                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}";
                    });
                    AppendLog($"[LOOP {tickId}] OK {tickSw.Elapsed.TotalSeconds:F1}s | {tickOutcome}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppendLog($"[LOOP {tickId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
                }

                try
                {
                    if (_loopStopRequested)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(loopDelaySeconds), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);

        _ = TrackLoopCompletionAsync(_loopTask);
    }

    private void StopBotButton_Click(object sender, RoutedEventArgs e)
    {
        _loopStopRequested = true;
        _queueStopRequested = true;
        AppendLog("Graceful stop requested. Current action will finish first.");
    }

    private void SidebarNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var tag = button.Tag?.ToString();
        switch (tag)
        {
            case "dashboard":
                MainTabControl.SelectedIndex = 0;
                break;
            case "resources":
                MainTabControl.SelectedIndex = 1;
                break;
            case "buildings":
                MainTabControl.SelectedIndex = 2;
                break;
            case "queue":
                MainTabControl.SelectedIndex = 3;
                break;
            case "logs":
                MainTabControl.SelectedIndex = 4;
                break;
            case "inbox":
                MainTabControl.SelectedIndex = 5;
                break;
            default:
                MainTabControl.SelectedIndex = 0;
                break;
        }
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Use Login first.\nCheck village status and Scan all villages add tasks to queue.\nStart/Stop controls loop execution.",
            "Help",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        LoadConfigToUi();
        AppendLog("Config reloaded from config/bot.json.");
        _ = RefreshInboxIndicatorsAsync(logErrors: false);
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

    private void QueueAddButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new AddQueueItemWindow(TbotUltra.Core.Tasks.TaskCatalog.AllowedTaskNames)
            {
                Owner = this,
            };
            if (window.ShowDialog() != true)
            {
                return;
            }

            var payload = window.Payload is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(window.Payload, StringComparer.OrdinalIgnoreCase);
            var selectedVillageName = GetSelectedVillageName();
            var selectedVillageUrl = GetSelectedVillageUrl();
            if (!string.IsNullOrWhiteSpace(selectedVillageName))
            {
                payload[BotOptionPayloadKeys.TargetVillageName] = selectedVillageName;
            }

            if (!string.IsNullOrWhiteSpace(selectedVillageUrl))
            {
                payload[BotOptionPayloadKeys.TargetVillageUrl] = selectedVillageUrl;
            }

            var item = _botService.Enqueue(window.TaskName, payload, window.Priority, window.MaxRetries);
            AppendLog($"Queue item added: {item.TaskName} (priority={item.Priority}).");
            RefreshQueueUi();
            _ = TriggerQueueAutoRunAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not add queue item: {ex.Message}");
        }
    }

    private void QueueRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        if (_botService.RemoveQueueItem(selected.Id))
        {
            AppendLog($"Queue item removed: {selected.TaskName}.");
            RefreshQueueUi();
            return;
        }

        AppendLog("Could not remove queue item.");
    }

    private void QueueMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        if (_botService.MoveQueueItemUp(selected.Id))
        {
            RefreshQueueUi(selectId: selected.Id);
            return;
        }

        AppendLog("Move up is only available within the same priority group.");
    }

    private void QueueMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        if (_botService.MoveQueueItemDown(selected.Id))
        {
            RefreshQueueUi(selectId: selected.Id);
            return;
        }

        AppendLog("Move down is only available within the same priority group.");
    }

    private void QueueRetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueueDataGrid.SelectedItem is not QueueItemRow selected)
        {
            AppendLog("Select a queue item first.");
            return;
        }

        if (_botService.RetryQueueItem(selected.Id))
        {
            AppendLog($"Queue item reset for retry: {selected.TaskName}.");
            RefreshQueueUi(selectId: selected.Id);
            return;
        }

        AppendLog("Retry is only available for failed or paused items.");
    }

    private void QueueClearButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _botService.ClearQueue();
            AppendLog("Queue cleared.");
            RefreshQueueUi();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue: {ex.Message}");
        }
    }

    private void QueueRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshQueueUi();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _isAppClosing = true;
            _inboxAutoEnabled = false;
            _clockTimer.Stop();
            _copyFeedbackTimer.Stop();
            _inboxRefreshTimer.Stop();
            _buildQueueCountdownTimer.Stop();
            _loopStopRequested = true;
            _queueStopRequested = true;
            _loopCts?.Cancel();
            _queueAutoRunCts.Cancel();

            // Do not block UI shutdown indefinitely; close shared browser best-effort.
            var shutdownTask = _botService.ShutdownAsync(AppendLog);
            if (!shutdownTask.Wait(TimeSpan.FromSeconds(3)))
            {
                AppendLog("Shutdown timeout while closing browser session. Continuing app exit.");
            }

            if (ShouldClearQueueOnShutdown())
            {
                _botService.ClearQueue();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue on shutdown: {ex.Message}");
        }
    }

    private void SyncServerFromActiveAccount()
    {
        try
        {
            var activeName = _accountStore.ActiveAccountName();
            var account = _accountStore
                .ListAccounts()
                .FirstOrDefault(item => string.Equals(item.Name, activeName, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                UpdateAccountInfoLabel(activeName);
                return;
            }

            UpdateAccountInfoLabel(account.Name);
            var targetUrl = account.ServerUrl?.Trim().TrimEnd('/') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                return;
            }

            var config = _botConfigStore.Load();
            var currentUrl = (config["base_url"]?.GetValue<string>() ?? string.Empty).TrimEnd('/');
            var currentName = config["server_name"]?.GetValue<string>() ?? string.Empty;
            var changed = false;

            if (!string.IsNullOrWhiteSpace(account.ServerName)
                && !string.Equals(currentName, account.ServerName, StringComparison.OrdinalIgnoreCase))
            {
                config["server_name"] = account.ServerName;
                changed = true;
            }

            if (!string.Equals(currentUrl, targetUrl, StringComparison.OrdinalIgnoreCase))
            {
                config["base_url"] = targetUrl;
                changed = true;
            }

            if (changed)
            {
                _botConfigStore.Save(config);
                AppendLog($"Server synced from active account '{account.Name}'.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not sync server from account: {ex.Message}");
        }
    }

    private async Task<List<ServerOption>> FetchDefaultServerOptionsAsync(BotOptions options)
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

    private List<ServerOption> FetchEffectiveServerOptions(IEnumerable<ServerOption> defaultServers)
    {
        try
        {
            var customServers = _serverCatalogStore.Load();
            if (customServers.Count > 0)
            {
                return customServers;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load user server list, using defaults: {ex.Message}");
        }

        return defaultServers
            .Select(item => new ServerOption
            {
                Name = item.Name,
                BaseUrl = item.BaseUrl,
            })
            .ToList();
    }

    private void RefreshQueueUi(Guid? selectId = null)
    {
        try
        {
            var ordered = _botService.GetQueueItemsForDisplay().ToList();
            _queueServerTimeOffset = ResolveQueueServerTimeOffset();
            var rows = ordered
                .Select(item => new QueueItemRow
                {
                    Id = item.Id,
                    TaskName = item.TaskName,
                    Priority = item.Priority,
                    Status = item.Status,
                    Retries = item.Retries,
                    MaxRetries = item.MaxRetries,
                    CreatedAt = item.CreatedAt,
                    NextAttemptAtServer = FormatQueueServerTime(item.NextAttemptAt),
                    CreatedAtServer = FormatQueueServerTime(item.CreatedAt),
                })
                .ToList();

            QueueDataGrid.ItemsSource = rows;

            if (selectId.HasValue)
            {
                var selected = rows.FirstOrDefault(item => item.Id == selectId.Value);
                if (selected is not null)
                {
                    QueueDataGrid.SelectedItem = selected;
                }
            }

            var offsetLabel = _queueServerTimeOffset.ToString(@"hh\:mm");
            var offsetPrefix = _queueServerTimeOffset < TimeSpan.Zero ? "-" : "+";
            var state = (_loopTask is not null && !_loopTask.IsCompleted)
                ? "Loop running"
                : _autoQueueRunning
                    ? "Function running"
                    : "Idle";
            QueueInfoTextBlock.Text = $"Queue items: {rows.Count} | State: {state} | Server time offset: UTC{offsetPrefix}{offsetLabel}";
            UpdateExecutionStateIndicator();
        }
        catch (Exception ex)
        {
            QueueInfoTextBlock.Text = $"Queue load failed: {ex.Message}";
            AppendLog($"Queue load failed: {ex.Message}");
            UpdateExecutionStateIndicator();
        }
    }

    private void RefreshQueueUiOnUiThread(Guid? selectId = null)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshQueueUi(selectId);
            return;
        }

        _ = Dispatcher.BeginInvoke(() => RefreshQueueUi(selectId));
    }

    private async Task PromptAccountAnalysisIfNeededAsync(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        if (_analysisPromptDismissed.Contains(accountName))
        {
            return;
        }

        if (IsAnalysisDone(accountName))
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            var result = MessageBox.Show(
                this,
                $"Account '{accountName}' has no saved full analysis.\n\nRun full analysis now?",
                "Account analysis",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                EnqueueQuickTask("account_full_analysis", $"Run full analysis for account {accountName}");
                RefreshAccountPicker();
                return;
            }

            _analysisPromptDismissed.Add(accountName);
            AppendLog($"Account analysis postponed for '{accountName}'.");
        });
    }

    private bool IsAnalysisDone(string accountName)
    {
        try
        {
            return _accountAnalysisStore.IsAnalyzed(accountName);
        }
        catch
        {
            return false;
        }
    }

    private async void LoadBuildingsButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("LoadBuildings");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var status = await _botService.ReadVillageStatusAsync(
                options,
                AppendLog,
                GetSelectedVillageName(),
                GetSelectedVillageUrl(),
                operationToken);

            _lastBuildingStatus = status;
            PopulateBuildingsTab(status);
            CompleteOperation(operationId, operationSw, $"Loaded {status.Buildings.Count} building slots.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Load buildings paused.";
            AppendLog("Load buildings paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void BuildingCategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_lastBuildingStatus is null)
        {
            return;
        }

        PopulateBuildingCatalogOptions(_lastBuildingStatus);
    }

    private void QueueConstructBuildingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBuildingStatus is null)
        {
            BuildingsInfoTextBlock.Text = "Load buildings first.";
            return;
        }

        if (!int.TryParse(ConstructSlotTextBox.Text.Trim(), out var slotId) || slotId < 19)
        {
            BuildingsInfoTextBlock.Text = "Construct slot must be an integer >= 19.";
            return;
        }

        if (ConstructBuildingComboBox.SelectedItem is not BuildingCatalogOption selectedBuilding)
        {
            BuildingsInfoTextBlock.Text = "Select a building to construct.";
            return;
        }

        var occupied = _lastBuildingStatus.Buildings.FirstOrDefault(item => item.SlotId == slotId && (item.Level ?? 0) > 0);
        if (occupied is not null)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is occupied by {occupied.Name} level {occupied.Level}.";
            return;
        }

        var sameBuildingLevels = _lastBuildingStatus.Buildings
            .Where(item => item.Gid == selectedBuilding.Gid && item.Level is not null)
            .Select(item => item.Level!.Value)
            .ToList();
        if (sameBuildingLevels.Count > 0 && BuildingCatalogService.IsSingleInstance(selectedBuilding.Gid))
        {
            var currentHighest = sameBuildingLevels.Max();
            var max = BuildingCatalogService.MaxLevelFor(selectedBuilding.Gid);
            if (currentHighest >= max)
            {
                BuildingsInfoTextBlock.Text = $"{selectedBuilding.Name} already exists at max level ({max}).";
                return;
            }
        }

        var missing = MissingRequirements(_lastBuildingStatus, selectedBuilding.RequirementEntries);
        if (missing.Count > 0)
        {
            BuildingsInfoTextBlock.Text = $"Missing requirements: {string.Join(", ", missing.Select(item => $"{item.Name} {item.Level}+"))}";
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingConstructSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingConstructGid] = selectedBuilding.Gid.ToString(),
            [BotOptionPayloadKeys.BuildingConstructName] = selectedBuilding.Name,
        };
        EnqueueQuickTask("construct_building", $"Construct {selectedBuilding.Name} in slot {slotId}", payload);
        BuildingsInfoTextBlock.Text = $"Queued construct: {selectedBuilding.Name} in slot {slotId}.";
    }

    private void QueueUpgradeBuildingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(UpgradeSlotTextBox.Text.Trim(), out var slotId) || slotId < 19)
        {
            BuildingsInfoTextBlock.Text = "Upgrade slot must be an integer >= 19.";
            return;
        }

        if (!int.TryParse(UpgradeTargetLevelTextBox.Text.Trim(), out var targetLevel) || targetLevel < 1)
        {
            BuildingsInfoTextBlock.Text = "Target level must be an integer >= 1.";
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = targetLevel.ToString(),
        };
        EnqueueQuickTask("upgrade_building_to_level", $"Upgrade slot {slotId} to level {targetLevel}", payload);
        BuildingsInfoTextBlock.Text = $"Queued upgrade for slot {slotId} to level {targetLevel}.";
    }

    private void QueueUpgradeBuildingMaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(UpgradeSlotTextBox.Text.Trim(), out var slotId) || slotId < 19)
        {
            BuildingsInfoTextBlock.Text = "Upgrade slot must be an integer >= 19.";
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
        };
        EnqueueQuickTask("upgrade_building_to_max", $"Upgrade slot {slotId} to max level", payload);
        BuildingsInfoTextBlock.Text = $"Queued max-upgrade for slot {slotId}.";
    }

    private void QueueDemolishButton_Click(object sender, RoutedEventArgs e)
    {
        if (DemolishBuildingComboBox.SelectedItem is not BuildingSlotRow selected)
        {
            BuildingsInfoTextBlock.Text = "Select a building to demolish.";
            return;
        }

        if (!int.TryParse(DemolishTargetLevelTextBox.Text.Trim(), out var targetLevel) || targetLevel < 0)
        {
            BuildingsInfoTextBlock.Text = "Demolish target level must be an integer >= 0.";
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetBuildingSlotOrName] = selected.SlotId.ToString(),
            [BotOptionPayloadKeys.TargetLevel] = targetLevel.ToString(),
        };
        EnqueueQuickTask("demolish_building_to_level", $"Demolish {selected.Name} to level {targetLevel}", payload);
        BuildingsInfoTextBlock.Text = $"Queued demolition for {selected.Name} (slot {selected.SlotId}) to level {targetLevel}.";
    }

    private void QueueHeroManageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HeroMinHpTextBox.Text.Trim(), out var minHp) || minHp < 1 || minHp > 100)
        {
            BuildingsInfoTextBlock.Text = "Hero minimum HP must be an integer 1-100.";
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroMinHpForAdventure] = minHp.ToString(),
            [BotOptionPayloadKeys.HeroAutoRevive] = HeroAutoReviveCheckBox.IsChecked == true ? "true" : "false",
            [BotOptionPayloadKeys.HeroStatPriority] = HeroStatPriorityTextBox.Text.Trim(),
        };
        EnqueueQuickTask("hero_manage", "Run hero management task", payload);
        BuildingsInfoTextBlock.Text = "Queued hero management task.";
    }

    private void PopulateBuildingsTab(VillageStatus status)
    {
        _buildingRows.Clear();
        _demolishableBuildings.Clear();

        var categoryByGid = BuildingCatalogService.GetCatalogForTribe(status.Tribe)
            .ToDictionary(item => item.Gid, item => item, EqualityComparer<int>.Default);

        foreach (var building in status.Buildings
                     .Where(item => item.SlotId is not null)
                     .OrderBy(item => item.SlotId))
        {
            var slotId = building.SlotId!.Value;
            var category = "infrastructure";
            var requirements = string.Empty;
            if (building.Gid is int gid && categoryByGid.TryGetValue(gid, out var catalog))
            {
                category = catalog.Category;
                requirements = catalog.Requirements.Count == 0
                    ? "-"
                    : string.Join(", ", catalog.Requirements.Select(item => $"{item.Name} {item.Level}+"));
            }

            var row = new BuildingSlotRow
            {
                SlotId = slotId,
                Name = building.Name,
                Level = building.Level,
                Gid = building.Gid,
                Category = category,
                Requirements = requirements,
            };
            _buildingRows.Add(row);

            if ((building.Level ?? 0) > 0)
            {
                _demolishableBuildings.Add(row);
            }
        }

        PopulateBuildingCatalogOptions(status);

        var usedSlots = status.Buildings
            .Where(item => item.SlotId is not null && (item.Level ?? 0) > 0)
            .Select(item => item.SlotId!.Value)
            .ToHashSet();
        var freeSlots = Enumerable.Range(19, 22).Where(slot => !usedSlots.Contains(slot)).ToList();
        BuildingsInfoTextBlock.Text = $"Buildings loaded. Occupied slots: {usedSlots.Count}, free slots: {freeSlots.Count}.";
    }

    private void PopulateBuildingCatalogOptions(VillageStatus status)
    {
        _buildingCatalogOptions.Clear();

        var categoryFilter = (BuildingCategoryComboBox.SelectedItem as string)?.Trim().ToLowerInvariant() ?? "all";
        var catalog = BuildingCatalogService.GetCatalogForTribe(status.Tribe);
        foreach (var item in catalog)
        {
            if (!string.Equals(categoryFilter, "all", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var option = new BuildingCatalogOption
            {
                Gid = item.Gid,
                Name = item.Name,
                Category = item.Category,
                IsSpecial = item.IsSpecial,
                RequirementEntries = item.Requirements,
                Requirements = item.Requirements.Count == 0
                    ? "-"
                    : string.Join(", ", item.Requirements.Select(req => $"{req.Name} {req.Level}+")),
            };
            _buildingCatalogOptions.Add(option);
        }

        if (_buildingCatalogOptions.Count > 0)
        {
            ConstructBuildingComboBox.SelectedIndex = 0;
        }
    }

    private static List<BuildingRequirementEntry> MissingRequirements(VillageStatus status, IReadOnlyList<BuildingRequirementEntry> requirements)
    {
        var missing = new List<BuildingRequirementEntry>();
        foreach (var requirement in requirements)
        {
            var level = status.Buildings
                .Where(item => item.Level is not null && item.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Level!.Value)
                .DefaultIfEmpty(0)
                .Max();
            if (level < requirement.Level)
            {
                missing.Add(requirement);
            }
        }

        return missing;
    }

    private bool ShouldClearQueueOnStartup()
    {
        return ReadQueueClearSetting("queue_clear_on_startup", defaultValue: true);
    }

    private bool ShouldClearQueueOnShutdown()
    {
        return ReadQueueClearSetting("queue_clear_on_shutdown", defaultValue: true);
    }

    private bool ReadQueueClearSetting(string key, bool defaultValue)
    {
        try
        {
            var config = _botConfigStore.Load();
            return config[key]?.GetValue<bool>() ?? defaultValue;
        }
        catch (Exception ex)
        {
            AppendLog($"Could not read {key}: {ex.Message}");
            return defaultValue;
        }
    }

    private TimeSpan ResolveQueueServerTimeOffset()
    {
        try
        {
            var config = _botConfigStore.Load();
            var configuredHours = config["server_time_utc_offset_hours"]?.GetValue<double?>();
            if (configuredHours.HasValue)
            {
                return TimeSpan.FromHours(configuredHours.Value);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not read server_time_utc_offset_hours: {ex.Message}");
        }

        return TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
    }

    private DateTimeOffset GetServerNow()
    {
        return DateTimeOffset.UtcNow.ToOffset(_queueServerTimeOffset);
    }

    private void UpdateClockText()
    {
        ClockTextBlock.Text = $"Time: {GetServerNow():yyyy-MM-dd HH:mm:ss} server";
    }

    private string FormatQueueServerTime(DateTimeOffset utcTimestamp)
    {
        var utc = utcTimestamp.ToUniversalTime();
        var serverTime = utc.ToOffset(_queueServerTimeOffset);
        return serverTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void ToggleUiBusy(bool busy)
    {
        _uiBusy = busy;
        AccountComboBox.IsEnabled = !busy;
        LoginButton.IsEnabled = !busy;
        LogoutButton.IsEnabled = !busy;
        CheckStatusButton.IsEnabled = !busy;
        ScanAllVillagesButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        QueueAddButton.IsEnabled = !busy;
        QueueRemoveButton.IsEnabled = !busy;
        QueueMoveUpButton.IsEnabled = !busy;
        QueueMoveDownButton.IsEnabled = !busy;
        QueueRetryButton.IsEnabled = !busy;
        QueueClearButton.IsEnabled = !busy;
        QueueRefreshButton.IsEnabled = !busy;
        LoadResourcesButton.IsEnabled = !busy;
        ResourceTargetLevelComboBox.IsEnabled = !busy;
        UpgradeAllResourcesButton.IsEnabled = !busy;
        MarkMessagesReadButton.IsEnabled = !busy;
        MarkReportsReadButton.IsEnabled = !busy;
        StopBotButton.IsEnabled = true;
        StartLoopButton.IsEnabled = true;
        StartLoopButton.Content = (busy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted)) ? "Pause bot" : "Start bot";
    }

    private void RefreshAccountPicker()
    {
        try
        {
            var accounts = _accountStore.ListAccounts()
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            accounts.Add(new AccountEntry
            {
                Name = ManageAccountsOptionName,
                Username = string.Empty,
                Password = string.Empty,
                ServerName = string.Empty,
                ServerUrl = string.Empty,
            });
            var active = _accountStore.ActiveAccountName();

            _suppressAccountSelectionChange = true;
            try
            {
                AccountComboBox.ItemsSource = accounts;
                var selected = accounts.FirstOrDefault(item =>
                                   string.Equals(item.Name, active, StringComparison.OrdinalIgnoreCase))
                               ?? accounts.FirstOrDefault(item =>
                                   !string.Equals(item.Name, ManageAccountsOptionName, StringComparison.OrdinalIgnoreCase));
                AccountComboBox.SelectedItem = selected;
            }
            finally
            {
                _suppressAccountSelectionChange = false;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not refresh account list: {ex.Message}");
        }
    }

    private async Task TriggerQueueAutoRunAsync()
    {
        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            return;
        }

        if (_queueStopRequested)
        {
            return;
        }

        try
        {
            if (!await _queueAutoRunGate.WaitAsync(0, _queueAutoRunCts.Token))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            _autoQueueRunCts = CancellationTokenSource.CreateLinkedTokenSource(_queueAutoRunCts.Token);
            var autoToken = _autoQueueRunCts.Token;
            try
            {
                _autoQueueRunning = true;
                UpdateExecutionStateIndicatorOnUiThread();
                await ExecuteQueuedItemsNowAsync(autoToken);
            }
            finally
            {
                _autoQueueRunning = false;
                _autoQueueRunCts?.Dispose();
                _autoQueueRunCts = null;
                UpdateExecutionStateIndicatorOnUiThread();
                _queueAutoRunGate.Release();
            }
        });
    }

    private async Task ExecuteQueuedItemsNowAsync(CancellationToken cancellationToken)
    {
        var runId = System.Threading.Interlocked.Increment(ref _operationCounter);
        AppendLog($"[AUTOQ {runId}] START");
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_queueStopRequested)
            {
                AppendLog($"[AUTOQ {runId}] STOPPED (graceful stop requested).");
                return;
            }

            if (_loopTask is not null && !_loopTask.IsCompleted)
            {
                AppendLog($"[AUTOQ {runId}] STOPPED (loop is running).");
                return;
            }

            QueueItem? next;
            try
            {
                next = _botService.SelectNextQueueItem();
            }
            catch (Exception ex)
            {
                AppendLog($"[AUTOQ {runId}] FAIL selecting queue item: {FormatExceptionForLog(ex)}");
                return;
            }

            if (next is null)
            {
                AppendLog($"[AUTOQ {runId}] DONE (queue empty).");
                return;
            }

            var tickSw = Stopwatch.StartNew();
            try
            {
                var terminalCountBefore = await Dispatcher.InvokeAsync(() => _terminalEntries.Count);
                _botService.MarkQueueItemRunning(next.Id);
                RefreshQueueUiOnUiThread(next.Id);
                await EnsureChromiumInstalledAsync();
                var options = ApplySelectedVillageToOptions(LoadBotOptions());
                AppendLog($"[AUTOQ {runId}] RUN task={next.TaskName}, id={next.Id}");
                await _botService.ExecuteQueueItemAsync(options, next, AppendLog, cancellationToken);
                _botService.MarkQueueItemSucceeded(next.Id);
                if (string.Equals(next.TaskName, "account_full_analysis", StringComparison.OrdinalIgnoreCase))
                {
                    _analysisPromptDismissed.Remove(_accountStore.ActiveAccountName());
                    await Dispatcher.InvokeAsync(RefreshAccountPicker);
                }
                if (IsResourceUpgradeTask(next.TaskName))
                {
                    var fastUpdated = await TryApplyFastResourceLevelUpdateAsync(next.TaskName, terminalCountBefore);
                    if (!fastUpdated)
                    {
                        await LoadResourcesAfterUpgradeAsync(cancellationToken, resourceOnly: true);
                    }
                }
                AppendLog($"[AUTOQ {runId}] OK {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ex is InvalidOperationException ioe
                    && ioe.Message.Contains("different thread owns it", StringComparison.OrdinalIgnoreCase))
                {
                    _botService.MarkQueueItemExecutionFailed(next.Id);
                    _queueStopRequested = true;
                    AppendLog($"[AUTOQ {runId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | UI thread access error detected. Auto-queue paused to prevent spam.");
                    return;
                }

                if (TryExtractQueueWaitDelay(ex.Message, out var queueWaitDelay))
                {
                    var deferred = _botService.MarkQueueItemDeferred(next.Id, queueWaitDelay);
                    if (deferred)
                    {
                        AppendLog($"[AUTOQ {runId}] DEFER {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s");
                    }
                    else
                    {
                        _botService.MarkQueueItemExecutionFailed(next.Id);
                        AppendLog($"[AUTOQ {runId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | {FormatExceptionForLog(ex)}");
                        RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                    }
                }
                else
                {
                    _botService.MarkQueueItemExecutionFailed(next.Id);
                    AppendLog($"[AUTOQ {runId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | {FormatExceptionForLog(ex)}");
                    RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                }
            }
            finally
            {
                RefreshQueueUiOnUiThread(next.Id);
            }
        }
    }

    private void AppendLog(string message)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            var normalized = message?.Replace("\r\n", "\n").Replace('\r', '\n') ?? string.Empty;
            var parts = normalized
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0)
            {
                parts = [string.Empty];
            }

            foreach (var part in parts)
            {
                var line = $"[{GetServerNow():yyyy-MM-dd HH:mm:ss}] {part}";
                _terminalEntries.Insert(0, line);

                if (IsAlarmMessage(part))
                {
                    _alarmEntries.Insert(0, line);
                    _unacknowledgedAlarmCount += 1;
                }

                if (part.Contains("Manual verification cleared", StringComparison.OrdinalIgnoreCase))
                {
                    _unacknowledgedAlarmCount = 0;
                }

                if (part.Contains("manual verification appeared", StringComparison.OrdinalIgnoreCase)
                    || part.Contains("captcha/manual", StringComparison.OrdinalIgnoreCase))
                {
                    ShowManualVerificationPopup();
                }
            }

            TrimToMaxEntries(_terminalEntries, 1000);
            TrimToMaxEntries(_alarmEntries, 200);

            StatusTextBlock.Text = message;
            UpdateTerminalAlarmUi();
        });
    }

    private void ShowManualVerificationPopup()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastVerificationPopupAt).TotalSeconds < 10)
        {
            return;
        }

        _lastVerificationPopupAt = now;
        var result = MessageBox.Show(
            this,
            "Manual verification is required. If your browser is already open, solve it there. Do you want to open/restart the verification browser now?",
            "Manual verification",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            OpenVerificationBrowser();
        }
    }

    private static void TrimToMaxEntries(ObservableCollection<string> entries, int max)
    {
        while (entries.Count > max)
        {
            entries.RemoveAt(entries.Count - 1);
        }
    }

    private static bool IsAlarmMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        if (value.Contains("] fail"))
        {
            return true;
        }

        return value.Contains("failed")
            || value.Contains("error")
            || value.Contains("exception")
            || value.Contains("timeout")
            || value.Contains("captcha")
            || value.Contains("verification")
            || value.Contains("invalid")
            || value.Contains("not logged in")
            || value.Contains("could not")
            || value.Contains("manual");
    }

    private async void MarkMessagesReadButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("MarkMessagesRead");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            await _botService.MarkMessagesAsReadAsync(
                options,
                AppendLog,
                GetSelectedVillageName(),
                GetSelectedVillageUrl(),
                operationToken);
            await RefreshInboxIndicatorsAsync(logErrors: true, force: true);
            CompleteOperation(operationId, operationSw, "Messages marked as read.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Mark as read paused.";
            AppendLog("Mark as read paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void MarkReportsReadButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("MarkReportsRead");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            await _botService.MarkReportsAsReadAsync(
                options,
                AppendLog,
                GetSelectedVillageName(),
                GetSelectedVillageUrl(),
                operationToken);
            await RefreshInboxIndicatorsAsync(logErrors: true, force: true);
            CompleteOperation(operationId, operationSw, "Reports marked as read.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Mark reports as read paused.";
            AppendLog("Mark reports as read paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async Task RefreshInboxIndicatorsAsync(bool logErrors, bool force = false)
    {
        if (_isAppClosing)
        {
            return;
        }

        if (!await _inboxRefreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!_inboxAutoEnabled)
            {
                return;
            }

            if (!force && (_uiBusy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted)))
            {
                return;
            }

            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var status = await _botService.ReadInboxStatusAsync(options, _ => { }, CancellationToken.None);
            UpdateInboxButtons(status.UnreadMessages, status.UnreadReports);
        }
        catch (Exception ex)
        {
            UpdateInboxButtons(0, 0);
            if (logErrors)
            {
                AppendLog($"Could not refresh unread messages/reports: {ex.Message}");
            }
        }
        finally
        {
            _inboxRefreshGate.Release();
        }
    }

    private async Task HandleInboxRefreshTickAsync()
    {
        await RefreshInboxIndicatorsAsync(logErrors: false);
    }

    private void UpdateInboxButtons(int unreadMessages, int unreadReports)
    {
        _lastUnreadMessages = unreadMessages;
        _lastUnreadReports = unreadReports;
        MessageUnreadTextBlock.Text = $"Unread: {unreadMessages}";
        ReportsUnreadTextBlock.Text = $"Unread: {unreadReports}";
        InboxNavButton.ToolTip = $"Messages {unreadMessages} | Reports {unreadReports}";
    }

    private void CloseTerminalAlarmPopupButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 0;
    }

    private void AcknowledgeAlarmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_unacknowledgedAlarmCount == 0)
        {
            return;
        }

        _unacknowledgedAlarmCount = 0;
        StatusTextBlock.Text = "Alerts acknowledged.";
        UpdateTerminalAlarmUi();
    }

    private void ClearCurrentLogButton_Click(object sender, RoutedEventArgs e)
    {
        var alarmsSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        if (alarmsSelected)
        {
            _alarmEntries.Clear();
            _unacknowledgedAlarmCount = 0;
        }
        else
        {
            _terminalEntries.Clear();
        }

        UpdateTerminalAlarmUi();
    }

    private void CopyCurrentTabButton_Click(object sender, RoutedEventArgs e)
    {
        var alertsTabSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        var source = alertsTabSelected ? _alarmEntries : _terminalEntries;
        if (source.Count == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, source));
        StatusTextBlock.Text = alertsTabSelected
            ? "Alerts copied to clipboard."
            : "Terminal log copied to clipboard.";

        CopyFeedbackTextBlock.Text = "Copied";
        CopyFeedbackTextBlock.Visibility = Visibility.Visible;
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void UpdateTerminalAlarmUi()
    {
        var hasAlarms = _unacknowledgedAlarmCount > 0;
        var hasAlarmEntries = _alarmEntries.Count > 0;
        var alarmTabSelected = TerminalAlarmTabControl.SelectedIndex == 1;
        AcknowledgeAlarmButton.IsEnabled = hasAlarms;
        CopyCurrentTabButton.IsEnabled = alarmTabSelected ? hasAlarmEntries : _terminalEntries.Count > 0;
        CopyCurrentTabButton.ToolTip = alarmTabSelected ? "Copy alerts" : "Copy terminal";
        ClearCurrentLogButton.IsEnabled = alarmTabSelected ? hasAlarmEntries : _terminalEntries.Count > 0;

        if (hasAlarms)
        {
            LogsNavButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            LogsNavButton.Foreground = Brushes.White;
            LogsNavButton.ToolTip = $"Logs ({_unacknowledgedAlarmCount} alarms)";
        }
        else
        {
            LogsNavButton.Background = new SolidColorBrush(Color.FromRgb(243, 244, 246));
            LogsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
            LogsNavButton.ToolTip = "Logs";
        }
    }

    private void PopoutLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_logsPopupWindow is not null)
        {
            _logsPopupWindow.Activate();
            return;
        }

        var popupTab = new TabControl();
        var popupLogList = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(2, 6, 23)),
            Foreground = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            SelectionMode = SelectionMode.Single,
            ItemsSource = _terminalEntries,
        };
        popupLogList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        popupLogList.ItemTemplate = new DataTemplate
        {
            VisualTree = new FrameworkElementFactory(typeof(TextBlock)),
        };
        popupLogList.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding("."));
        popupLogList.ItemTemplate.VisualTree.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var popupAlarmList = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 13, 13)),
            Foreground = new SolidColorBrush(Color.FromRgb(252, 165, 165)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            SelectionMode = SelectionMode.Single,
            ItemsSource = _alarmEntries,
        };
        popupAlarmList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        popupAlarmList.ItemTemplate = new DataTemplate
        {
            VisualTree = new FrameworkElementFactory(typeof(TextBlock)),
        };
        popupAlarmList.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding("."));
        popupAlarmList.ItemTemplate.VisualTree.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);

        popupTab.Items.Add(new TabItem { Header = "Log", Content = popupLogList });
        popupTab.Items.Add(new TabItem { Header = "Alarms", Content = popupAlarmList });

        var clearButton = new Button { Content = "Clear", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        clearButton.Click += (_, _) =>
        {
            if (popupTab.SelectedIndex == 1)
            {
                _alarmEntries.Clear();
            }
            else
            {
                _terminalEntries.Clear();
            }

            UpdateTerminalAlarmUi();
        };

        var acknowledgeButton = new Button { Content = "Acknowledge alarms", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        acknowledgeButton.Click += (_, _) =>
        {
            _unacknowledgedAlarmCount = 0;
            UpdateTerminalAlarmUi();
        };

        var copyButton = new Button { Content = "Copy", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        copyButton.Click += (_, _) =>
        {
            var lines = popupTab.SelectedIndex == 1 ? _alarmEntries : _terminalEntries;
            if (lines.Count == 0)
            {
                return;
            }

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        };

        var closeButton = new Button { Content = "Close", Padding = new Thickness(10, 4, 10, 4), Height = 30 };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        footer.Children.Add(acknowledgeButton);
        footer.Children.Add(copyButton);
        footer.Children.Add(clearButton);
        footer.Children.Add(closeButton);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(popupTab);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        _logsPopupWindow = new Window
        {
            Title = "Logs",
            Width = 760,
            Height = 440,
            MinWidth = 620,
            MinHeight = 360,
            Content = root,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + Width + 10,
            Top = Top + 30,
        };

        _logsPopupWindow.Closed += (_, _) => _logsPopupWindow = null;
        closeButton.Click += (_, _) => _logsPopupWindow?.Close();
        _logsPopupWindow.Show();
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

    private async Task LoadResourcesAfterUpgradeAsync(CancellationToken cancellationToken = default, bool resourceOnly = false)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly);

        var rows = status.ResourceFields
            .Where(item => item.SlotId is not null)
            .OrderBy(item => item.SlotId)
            .Select(item => new ResourceFieldRow
            {
                SlotId = item.SlotId ?? 0,
                FieldType = item.FieldType,
                Name = item.Name,
                Level = item.Level,
                Url = item.Url ?? string.Empty,
                PendingTargetLevel = null,
                IsMaxLevel = (item.Level ?? 0) >= 20,
            })
            .ToList();
        SetResourceRows(rows);
        ApplyVillageStatusToUi(status);
        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
    }

    private async Task<VillageStatus> ReadVillageStatusWithRetryAsync(BotOptions options, CancellationToken cancellationToken, bool resourceOnly = false)
    {
        var status = await ReadVillageStatusAsync(options, cancellationToken, resourceOnly);
        var requiresRetry = status.ResourceFields.Count < 18;
        if (!requiresRetry)
        {
            return status;
        }

        if (status.ResourceFields.Count < 18)
        {
            AppendLog($"Resource scan returned {status.ResourceFields.Count} fields. Retrying once...");
        }

        await Task.Delay(350, cancellationToken);
        return await ReadVillageStatusAsync(options, cancellationToken, resourceOnly);
    }

    private Task<VillageStatus> ReadVillageStatusAsync(BotOptions options, CancellationToken cancellationToken, bool resourceOnly)
    {
        if (resourceOnly)
        {
            return _botService.ReadVillageResourceStatusAsync(
                options,
                AppendLog,
                GetSelectedVillageName(),
                GetSelectedVillageUrl(),
                cancellationToken);
        }

        return _botService.ReadVillageStatusAsync(
            options,
            AppendLog,
            GetSelectedVillageName(),
            GetSelectedVillageUrl(),
            cancellationToken);
    }

    private void SetResourceRows(IReadOnlyList<ResourceFieldRow> rows)
    {
        ResourcesDataGrid.ItemsSource = rows.ToList();
        RepopulateResourceGroups(rows);
    }

    private void SetPendingResourceLevel(int slotId, int targetLevel)
    {
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var updated = sourceRows
            .Select(row => row.SlotId == slotId
                ? new ResourceFieldRow
                {
                    SlotId = row.SlotId,
                    FieldType = row.FieldType,
                    Name = row.Name,
                    Level = row.Level,
                    Url = row.Url,
                    PendingTargetLevel = targetLevel,
                    IsMaxLevel = row.IsMaxLevel,
                }
                : row)
            .ToList();
        SetResourceRows(updated);
    }

    private void MarkResourceAsMax(int slotId)
    {
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var updated = sourceRows
            .Select(row => row.SlotId == slotId
                ? new ResourceFieldRow
                {
                    SlotId = row.SlotId,
                    FieldType = row.FieldType,
                    Name = row.Name,
                    Level = row.Level,
                    Url = row.Url,
                    PendingTargetLevel = null,
                    IsMaxLevel = true,
                }
                : row)
            .ToList();
        SetResourceRows(updated);
    }

    private void RepopulateResourceGroups(IEnumerable<ResourceFieldRow> rows)
    {
        _woodFields.Clear();
        _clayFields.Clear();
        _ironFields.Clear();
        _croplandFields.Clear();

        foreach (var row in rows.OrderBy(item => item.SlotId))
        {
            GetBucket(row).Add(row);
        }
    }

    private ObservableCollection<ResourceFieldRow> GetBucket(ResourceFieldRow row)
    {
        var fieldType = row.FieldType?.Trim() ?? string.Empty;
        if (fieldType.Contains("wood", StringComparison.OrdinalIgnoreCase))
        {
            return _woodFields;
        }

        if (fieldType.Contains("clay", StringComparison.OrdinalIgnoreCase))
        {
            return _clayFields;
        }

        if (fieldType.Contains("iron", StringComparison.OrdinalIgnoreCase))
        {
            return _ironFields;
        }

        if (fieldType.Contains("crop", StringComparison.OrdinalIgnoreCase))
        {
            return _croplandFields;
        }

        return row.SlotId switch
        {
            1 or 5 or 6 or 10 or 16 => _woodFields,
            2 or 4 or 7 or 14 or 17 => _clayFields,
            3 or 8 or 9 or 11 or 15 => _ironFields,
            12 or 13 or 18 => _croplandFields,
            _ => _croplandFields,
        };
    }

    private void ResourceLevelBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ResourceFieldRow row })
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_resourceClickCooldownBySlot.TryGetValue(row.SlotId, out var lastClickAt)
            && (now - lastClickAt).TotalMilliseconds < 120)
        {
            return;
        }

        _resourceClickCooldownBySlot[row.SlotId] = now;

        if (row.IsMaxLevel || (row.Level ?? 0) >= 20)
        {
            MarkResourceAsMax(row.SlotId);
            MessageBox.Show(this, "Max level reached", "Resources", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var current = row.Level ?? 0;
        var pendingLevel = row.PendingTargetLevel ?? current;
        var baseLevel = Math.Max(current, pendingLevel);
        var target = Math.Clamp(baseLevel + 1, 1, 20);
        if (_resourceLastQueuedTargetBySlot.TryGetValue(row.SlotId, out var lastQueued)
            && lastQueued.Target == target
            && (now - lastQueued.At).TotalMilliseconds < 1200)
        {
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = row.SlotId.ToString(),
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = target.ToString(),
        };

        EnqueueQuickTask("upgrade_resource_to_level", $"Upgrade {row.Name} to level {target}", payload);
        _resourceLastQueuedTargetBySlot[row.SlotId] = (target, now);
        SetPendingResourceLevel(row.SlotId, target);
        ResourcesInfoTextBlock.Text = $"Queued {row.Name} to level {target}.";
        AppendLog($"Queued single resource upgrade: slot {row.SlotId} -> level {target}.");
    }

    private static bool IsResourceUpgradeTask(string taskName)
    {
        return string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
               || string.Equals(taskName, "upgrade_resource_to_max", StringComparison.OrdinalIgnoreCase)
               || string.Equals(taskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryApplyFastResourceLevelUpdateAsync(string taskName, int terminalCountBefore)
    {
        if (!IsResourceUpgradeTask(taskName))
        {
            return false;
        }

        var newLines = await Dispatcher.InvokeAsync(() =>
        {
            var nowCount = _terminalEntries.Count;
            var added = Math.Max(0, nowCount - terminalCountBefore);
            return _terminalEntries.Take(added).ToList();
        });

        if (newLines.Count == 0)
        {
            return false;
        }

        var updates = new Dictionary<int, int>();
        var maxedSlots = new HashSet<int>();
        foreach (var line in newLines)
        {
            var levelUp = Regex.Match(line, @"Resource slot\s+(?<slot>\d+)\s+level increased from\s+\d+\s+to\s+(?<to>\d+)", RegexOptions.IgnoreCase);
            if (levelUp.Success)
            {
                var slot = int.Parse(levelUp.Groups["slot"].Value);
                var toLevel = int.Parse(levelUp.Groups["to"].Value);
                updates[slot] = toLevel;
                continue;
            }

            var reached = Regex.Match(line, @"Resource slot\s+(?<slot>\d+)\s+is level\s+(?<lvl>\d+)\.", RegexOptions.IgnoreCase);
            if (reached.Success)
            {
                var slot = int.Parse(reached.Groups["slot"].Value);
                var level = int.Parse(reached.Groups["lvl"].Value);
                updates[slot] = level;
                continue;
            }

            var maxed = Regex.Match(line, @"Resource slot\s+(?<slot>\d+)\s+reached max level\s+(?<lvl>\d+)", RegexOptions.IgnoreCase);
            if (maxed.Success)
            {
                var slot = int.Parse(maxed.Groups["slot"].Value);
                var level = int.Parse(maxed.Groups["lvl"].Value);
                updates[slot] = level;
                maxedSlots.Add(slot);
            }
        }

        if (updates.Count == 0)
        {
            return false;
        }

        return await Dispatcher.InvokeAsync(() =>
        {
            if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
            {
                return false;
            }

            var rows = sourceRows.ToList();
            var changed = false;
            var updatedRows = rows.Select(row =>
            {
                if (!updates.TryGetValue(row.SlotId, out var nextLevel))
                {
                    return row;
                }

                if (row.Level is int existing && existing >= nextLevel)
                {
                    return row;
                }

                changed = true;
                return new ResourceFieldRow
                {
                    SlotId = row.SlotId,
                    FieldType = row.FieldType,
                    Name = row.Name,
                    Level = nextLevel,
                    Url = row.Url,
                    PendingTargetLevel = null,
                    IsMaxLevel = nextLevel >= 20 || row.IsMaxLevel || maxedSlots.Contains(row.SlotId),
                };
            }).ToList();

            if (!changed)
            {
                return false;
            }

            SetResourceRows(updatedRows);
            ResourcesInfoTextBlock.Text = $"Resource UI fast-updated for {updates.Count} slot(s).";
            if (maxedSlots.Count > 0)
            {
                MessageBox.Show(this, "Max level reached", "Resources", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return true;
        });
    }

    private string BeginOperation(string operationName)
    {
        var id = System.Threading.Interlocked.Increment(ref _operationCounter);
        var operationId = $"OP{id:D4}";
        AppendLog($"[{operationId}] START {operationName}");
        return operationId;
    }

    private void CompleteOperation(string operationId, Stopwatch sw, string summary)
    {
        AppendLog($"[{operationId}] OK {sw.Elapsed.TotalSeconds:F1}s | {summary}");
    }

    private void FailOperation(string operationId, Stopwatch sw, Exception ex)
    {
        AppendLog($"[{operationId}] FAIL {sw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
        if (ex.InnerException is not null)
        {
            AppendLog($"[{operationId}] INNER | {FormatExceptionForLog(ex.InnerException)}");
        }
    }

    private static string FormatExceptionForLog(Exception ex)
    {
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    private static bool TryExtractQueueWaitDelay(string message, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = Regex.Match(message, @"queue_wait_seconds=(?<seconds>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["seconds"].Value, out var seconds))
        {
            return false;
        }

        var effectiveSeconds = Math.Max(1, seconds + 1);
        delay = TimeSpan.FromSeconds(effectiveSeconds);
        return true;
    }

    private void RaiseAlarmIfQueueItemPermanentlyFailed(QueueItem queueItem, string errorMessage)
    {
        try
        {
            var latest = _botService
                .GetQueueItemsForDisplay()
                .FirstOrDefault(item => item.Id == queueItem.Id);
            if (latest is null || latest.Status != QueueStatus.Failed)
            {
                return;
            }

            AppendLog($"ALARM: task '{latest.TaskName}' failed after {latest.Retries}/{latest.MaxRetries} retries. Last error: {errorMessage}");
        }
        catch (Exception ex)
        {
            AppendLog($"ALARM: failed to evaluate retry state for task '{queueItem.TaskName}': {ex.Message}");
        }
    }

    private void SetLoopIndicator(bool running)
    {
        _ = running;
        UpdateExecutionStateIndicator();
    }

    private void UpdateExecutionStateIndicator()
    {
        var loopRunning = _loopTask is not null && !_loopTask.IsCompleted;
        var hasPausedQueueItems = false;
        try
        {
            hasPausedQueueItems = _botService
                .GetQueueItemsForDisplay()
                .Any(item => item.Status == QueueStatus.Paused);
        }
        catch
        {
            // Ignore indicator read errors.
        }

        if (loopRunning)
        {
            LoopStateTextBlock.Text = "State: loop running";
            LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            StartLoopButton.Content = "Pause bot";
            return;
        }

        if (_autoQueueRunning)
        {
            LoopStateTextBlock.Text = "State: function running";
            LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            StartLoopButton.Content = "Pause bot";
            return;
        }

        if (_uiBusy)
        {
            LoopStateTextBlock.Text = "State: function running";
            LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            StartLoopButton.Content = "Pause bot";
            return;
        }

        if (hasPausedQueueItems)
        {
            LoopStateTextBlock.Text = "State: paused";
            LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            StartLoopButton.Content = "Start bot";
            return;
        }

        LoopStateTextBlock.Text = "State: idle";
        LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        StartLoopButton.Content = "Start bot";
    }

    private void UpdateExecutionStateIndicatorOnUiThread()
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateExecutionStateIndicator();
            return;
        }

        _ = Dispatcher.BeginInvoke(() => UpdateExecutionStateIndicator());
    }

    private async Task TrackLoopCompletionAsync(Task loopTask)
    {
        try
        {
            await loopTask;
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                StartLoopButton.Content = "Start bot";
                StartLoopButton.IsEnabled = true;
                SetLoopIndicator(false);
                AppendLog("Loop stopped.");
            });
        }
    }

    private void UpdateAccountInfoLabel(string accountName)
    {
        ActiveAccountInfoTextBlock.Text = $"Account: {accountName} | Server: {ExtractServerSpeedLabel()}";
    }

    private string ExtractServerSpeedLabel()
    {
        try
        {
            var serverName = LoadBotOptions().ServerName ?? string.Empty;
            var match = Regex.Match(serverName, @"(\d+)\s*[xX]");
            return match.Success ? $"{match.Groups[1].Value}x" : "-";
        }
        catch
        {
            return "-";
        }
    }

    private void ApplyVillageStatusToUi(VillageStatus status)
    {
        VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
        TribeInfoTextBlock.Text = $"Tribe: {status.Tribe}";
        LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}";
        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        var goldText = status.Gold?.ToString() ?? "-";
        var silverText = status.Silver?.ToString() ?? "-";
        SummaryTextBlock.Text = $"Village: {status.ActiveVillage} | Capital: {capitalText} | Gold: {goldText} | Silver: {silverText}";

        _buildQueueActiveCount = status.ActiveBuildCount;
        _buildQueueRemainingSeconds = status.BuildQueueRemainingSeconds ?? -1;
        UpdateBuildQueueStatusText();
        RefreshVillagePicker(status);
    }

    private static string BuildResourceForecastSummary(VillageStatus status)
    {
        if (status.ResourceStorageForecasts is null || status.ResourceStorageForecasts.Count == 0)
        {
            return "Storage forecast unavailable.";
        }

        var parts = new List<string>();
        foreach (var forecast in status.ResourceStorageForecasts)
        {
            var key = forecast.ResourceKey switch
            {
                "wood" => "Wood",
                "clay" => "Clay",
                "iron" => "Iron",
                "crop" => "Crop",
                _ => forecast.ResourceKey,
            };
            var percentText = forecast.PercentOfCapacity is double percent
                ? $"{percent:F0}%"
                : "-";
            var etaText = forecast.SecondsToFull is int seconds
                ? FormatCountdown(seconds)
                : "-";
            parts.Add($"{key} {percentText} (full in {etaText})");
        }

        var warehouse = status.WarehouseCapacity?.ToString() ?? "-";
        var granary = status.GranaryCapacity?.ToString() ?? "-";
        return $"Warehouse={warehouse}, Granary={granary}. {string.Join(" | ", parts)}";
    }

    private void RefreshVillagePicker(VillageStatus status)
    {
        var currentSelectedName = GetSelectedVillageName();
        var villages = status.Villages
            .Select(v => new VillageSelectionItem
            {
                Name = string.IsNullOrWhiteSpace(v.Name) ? "-" : v.Name,
                Url = v.Url ?? string.Empty,
            })
            .ToList();

        if (villages.Count == 0)
        {
            villages.Add(new VillageSelectionItem { Name = "-", Url = string.Empty });
        }

        VillageComboBox.ItemsSource = villages;
        var selected = villages.FirstOrDefault(v =>
            string.Equals(v.Name, currentSelectedName, StringComparison.OrdinalIgnoreCase))
            ?? villages.FirstOrDefault(v =>
                string.Equals(v.Name, status.ActiveVillage, StringComparison.OrdinalIgnoreCase))
            ?? villages[0];
        VillageComboBox.SelectedItem = selected;
    }

    private void UpdateBuildQueueStatusText()
    {
        if (_buildQueueActiveCount <= 0)
        {
            BuildQueueStatusTextBlock.Text = "Build queue: idle";
            return;
        }

        if (_buildQueueRemainingSeconds >= 0)
        {
            BuildQueueStatusTextBlock.Text = $"Build queue: active={_buildQueueActiveCount}, remaining={FormatCountdown(_buildQueueRemainingSeconds)}";
            return;
        }

        BuildQueueStatusTextBlock.Text = $"Build queue: active={_buildQueueActiveCount}, remaining=-";
    }

    private void TickBuildQueueCountdown()
    {
        if (_buildQueueRemainingSeconds > 0)
        {
            _buildQueueRemainingSeconds -= 1;
        }

        if (_buildQueueRemainingSeconds == 0 && _buildQueueActiveCount > 0)
        {
            _buildQueueActiveCount = Math.Max(0, _buildQueueActiveCount - 1);
        }

        UpdateBuildQueueStatusText();
    }

    private static string FormatCountdown(int seconds)
    {
        var value = Math.Max(0, seconds);
        var time = TimeSpan.FromSeconds(value);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        }

        return $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private void UpdateLoginButtonsVisual(bool isLoggedIn)
    {
        if (isLoggedIn)
        {
            LoginButton.Background = new SolidColorBrush(Color.FromRgb(229, 231, 235));
            LoginButton.BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219));
            LoginButton.Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
            LogoutButton.Background = new SolidColorBrush(Color.FromRgb(3, 8, 38));
            LogoutButton.BorderBrush = new SolidColorBrush(Color.FromRgb(3, 8, 38));
            LogoutButton.Foreground = Brushes.White;
            return;
        }

        LoginButton.Background = new SolidColorBrush(Color.FromRgb(3, 8, 38));
        LoginButton.BorderBrush = new SolidColorBrush(Color.FromRgb(3, 8, 38));
        LoginButton.Foreground = Brushes.White;
        LogoutButton.Background = new SolidColorBrush(Color.FromRgb(229, 231, 235));
        LogoutButton.BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219));
        LogoutButton.Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
    }

    private void VillageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VillageComboBox.SelectedItem is not VillageSelectionItem selected)
        {
            return;
        }

        StatusTextBlock.Text = $"Selected village: {selected.Name}";
    }

    private void HandleBrowserClosedSignal()
    {
        if (!_botService.ConsumeBrowserClosedByUserSignal())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastVerificationPopupAt).TotalSeconds < 5)
        {
            return;
        }

        _lastVerificationPopupAt = now;
        var result = MessageBox.Show(
            this,
            "Chromium browser was closed. Do you want to restart it now?",
            "Browser closed",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            OpenVerificationBrowser();
        }
    }

    private async void OpenVerificationBrowser()
    {
        var operationId = BeginOperation("OpenVerificationBrowser");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = BotOptionsFactory.CloneWithOverrides(LoadBotOptions(), headlessOverride: false);
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            await EnsureChromiumInstalledAsync();
            await _botService.ExecuteLoginAsync(
                options,
                AppendLog,
                keepBrowserOpenAfterLogin: true,
                cancellationToken: operationToken);
            UpdateLoginButtonsVisual(true);
            CompleteOperation(operationId, operationSw, "Verification browser opened.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void SupportButton_Click(object sender, RoutedEventArgs e)
    {
        var support = new SupportWindow(_projectRoot, _terminalEntries.ToList())
        {
            Owner = this,
        };
        support.ShowDialog();
    }

    private string? GetSelectedVillageName()
    {
        var selection = GetSelectedVillageSelectionSnapshot();
        return selection.Name;
    }

    private string? GetSelectedVillageUrl()
    {
        var selection = GetSelectedVillageSelectionSnapshot();
        return selection.Url;
    }

    private BotOptions ApplySelectedVillageToOptions(BotOptions source)
    {
        var selection = GetSelectedVillageSelectionSnapshot();
        var selectedName = selection.Name;
        var selectedUrl = selection.Url;
        if (string.IsNullOrWhiteSpace(selectedName) && string.IsNullOrWhiteSpace(selectedUrl))
        {
            return source;
        }

        return BotOptionsPayloadApplier.Apply(source, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetVillageName] = selectedName ?? string.Empty,
            [BotOptionPayloadKeys.TargetVillageUrl] = selectedUrl ?? string.Empty,
        });
    }

    private (string? Name, string? Url) GetSelectedVillageSelectionSnapshot()
    {
        if (Dispatcher.CheckAccess())
        {
            return ReadSelectedVillageSelectionCore();
        }

        return Dispatcher.Invoke(ReadSelectedVillageSelectionCore);
    }

    private (string? Name, string? Url) ReadSelectedVillageSelectionCore()
    {
        if (VillageComboBox.SelectedItem is not VillageSelectionItem selected)
        {
            return (null, null);
        }

        string? name = null;
        if (!string.IsNullOrWhiteSpace(selected.Name))
        {
            var trimmed = selected.Name.Trim();
            if (!string.Equals(trimmed, "-", StringComparison.Ordinal)
                && !string.Equals(trimmed, "Unknown village", StringComparison.OrdinalIgnoreCase))
            {
                name = trimmed;
            }
        }

        var url = string.IsNullOrWhiteSpace(selected.Url) ? null : selected.Url.Trim();
        return (name, url);
    }
}

