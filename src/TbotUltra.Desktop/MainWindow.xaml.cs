using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private const int ResourceFieldMaxLevel = 40;
    private const int NonCapitalResourceMaxLevel = 10;
    private const int MaxFarmListsShown = 120;
    private const int MaxLogLinesPerFlush = 220;
    private const string RuntimeManualTaskPrefix = "desktop_runtime_manual";

    private sealed class ManualExecutionState
    {
        public string OperationId { get; init; } = string.Empty;
        public string OperationName { get; init; } = string.Empty;
        public Guid QueueItemId { get; init; }
        public ManualExecutionOutcome Outcome { get; set; }
    }

    private sealed record NatarListRow(
        int Index,
        string VillageName,
        int X,
        int Y);

    private enum ManualExecutionOutcome
    {
        None = 0,
        Succeeded = 1,
        Failed = 2,
        Canceled = 3,
    }

    private readonly string _projectRoot;
    private readonly string _versionPath;
    private readonly string _botConfigPath;
    private readonly string _envPath;
    private readonly string _queuePath;
    private readonly string _serverCatalogPath;
    private readonly string _sessionLogPath;
    private readonly BotConfigStore _botConfigStore;
    private readonly IAccountProvider _accountProvider;
    private readonly EnvAccountStore _accountStore;
    private readonly AccountAnalysisStore _accountAnalysisStore;
    private readonly NatarFarmCacheStore _natarFarmCacheStore;
    private readonly ManualFarmingPreferenceStore _manualFarmingPreferenceStore;
    private readonly ServerDiscoveryService _serverDiscoveryService;
    private readonly ServerCatalogStore _serverCatalogStore;
    private readonly IDesktopBotService _botService;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly DispatcherTimer _inboxRefreshTimer;
    private readonly DispatcherTimer _buildQueueCountdownTimer;
    private readonly ObservableCollection<string> _terminalEntries = [];
    private readonly ObservableCollection<string> _alarmEntries = [];
    private readonly ObservableCollection<LoopTaskOption> _automationLoopTasks = [];
    private readonly ObservableCollection<HeroAttributePriorityItem> _heroAttributePriorityItems = [];
    private readonly ObservableCollection<FarmListStatusRow> _farmLists = [];
    private readonly ObservableCollection<ResourceFieldRow> _woodFields = [];
    private readonly ObservableCollection<ResourceFieldRow> _clayFields = [];
    private readonly ObservableCollection<ResourceFieldRow> _ironFields = [];
    private readonly ObservableCollection<ResourceFieldRow> _croplandFields = [];
    private readonly ObservableCollection<BuildingSlotRow> _buildingRows = [];
    private readonly ObservableCollection<BuildingCatalogOption> _buildingCatalogOptions = [];
    private readonly ObservableCollection<BuildingSlotRow> _demolishableBuildings = [];
    private readonly Dictionary<int, DateTimeOffset> _resourceClickCooldownBySlot = new();
    private readonly Dictionary<int, (int Target, DateTimeOffset At)> _resourceLastQueuedTargetBySlot = new();
    private readonly Dictionary<int, int> _resourcePendingTargetBySlot = new();
    private readonly Dictionary<int, DateTimeOffset> _buildingClickCooldownBySlot = new();
    private readonly Dictionary<int, (int Target, DateTimeOffset At)> _buildingLastQueuedTargetBySlot = new();
    private readonly Dictionary<int, (string Name, DateTimeOffset At)> _buildingLastQueuedConstructBySlot = new();
    private readonly HashSet<string> _analysisPromptDismissed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<int, (double Left, double Top)> BuildingSlotLayoutById = CreateBuildingSlotLayout();

    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _heroAdventureCts;
    private DispatcherTimer? _heroCountdownTimer;
    private int _heroCountdownRemainingSeconds;
    private CancellationTokenSource? _villageSwitchCts;
    private Task? _loopTask;
    private bool _chromiumEnsured;
    private bool _suppressAccountSelectionChange;
    private bool _suppressVillageSelectionChange;
    private TimeSpan _queueServerTimeOffset;
    private int _lastUnreadMessages;
    private int _lastUnreadReports;
    private long _operationCounter;
    private long _loopTickCounter;
    private readonly SemaphoreSlim _queueAutoRunGate = new(1, 1);
    private readonly CancellationTokenSource _queueAutoRunCts = new();
    private CancellationTokenSource? _autoQueueRunCts;
    private readonly SemaphoreSlim _inboxRefreshGate = new(1, 1);
    private readonly DispatcherTimer _queueUiRefreshTimer;
    private Window? _logsPopupWindow;
    private Window? _queuePopupWindow;
    private ListBox? _logsPopupLogList;
    private ListBox? _logsPopupAlarmList;
    private Button? _activeSidebarButton;
    private Guid? _pendingQueueUiSelectId;
    private volatile bool _autoQueueRunning;
    private volatile bool _uiBusy;
    private volatile bool _isAppClosing;
    private volatile bool _inboxAutoEnabled;
    private volatile bool _loopStopRequested;
    private volatile bool _queueStopRequested;
    private volatile bool _isLoggedIn;
    private volatile bool _browserSessionLikelyOpen;
    private bool _farmingFeaturesAvailable = true;
    private int _activeVillageResourceMaxLevel = NonCapitalResourceMaxLevel;
    private int _buildQueueRemainingSeconds = -1;
    private int _buildQueueActiveCount;
    private bool _buildQueueReachedZeroPendingCompletion;
    private int _unacknowledgedAlarmCount;
    private bool _manualVerificationAlarmActive;
    private int _captchaSessionSeenCount;
    private int _captchaSessionSolvedCount;
    private bool _captchaSessionActive;
    private DateTimeOffset _lastVerificationPopupAt = DateTimeOffset.MinValue;
    private DateTimeOffset _inlineWaitUntilUtc = DateTimeOffset.MinValue;
    private int _manualFarmSessionExecutionCount;
    private string? _activeAutomationTaskName;
    private string? _activeFunctionDisplayName;
    private string? _pendingManualOperationId;
    private readonly Dictionary<string, string> _operationNamesById = new(StringComparer.OrdinalIgnoreCase);
    private ManualExecutionState? _activeManualExecution;
    private bool _logDragSelecting;
    private int _logDragAnchorIndex = -1;
    private ListBox? _logDragSourceList;
    private Point _automationLoopDragStart;
    private LoopTaskOption? _automationLoopDragSource;
    private Point _heroPriorityDragStart;
    private HeroAttributePriorityItem? _heroPriorityDragSource;
    private bool _suppressAutomationLoopConfigWrite;
    private bool _suppressFarmListUiRefresh;
    private bool _farmingOperationBusy;
    private bool _natarsProfileAnalyzed;
    private VillageStatus? _lastBuildingStatus;
    private readonly object _pendingLogSync = new();
    private readonly Queue<string> _pendingLogMessages = new();
    private readonly object _sessionLogWriteSync = new();
    private bool _logFlushQueued;

    private static readonly (string TaskName, string Title, string Description)[] AutomationLoopTaskCatalog =
    [
        ("status", "Check Village Status", "Reads current village status, resources and queue."),
        ("scan_all_villages", "Scan All Villages", "Scans and logs status for all villages."),
        ("account_snapshot", "Account Snapshot", "Reads tribe, active village and village count."),
        ("upgrade_resource_to_level", "Upgrade Resource To Level", "Upgrades configured resource slot to target level."),
        ("upgrade_all_resources_to_level", "Upgrade All Resources", "Upgrades all resource fields toward configured level."),
        ("upgrade_building_to_level", "Upgrade Building To Level", "Upgrades configured building slot to target level."),
        ("upgrade_building_to_max", "Upgrade Building To Max", "Upgrades configured building slot to max level."),
        ("construct_building", "Construct Building", "Builds configured building in configured slot."),
        ("load_buildings_snapshot", "Load Building Snapshot", "Reads and stores current building snapshot."),
        ("account_full_analysis", "Account Full Analysis", "Runs full account analysis and updates cache."),
        ("demolish_building_to_level", "Demolish Building", "Demolishes configured building to target level."),
        ("hero_manage", "Hero Manage", "Revives hero, allocates points and sends adventures."),
        ("hero_send_adventure", "Hero Adventures", "Sends hero on the first available adventure if at home."),
    ];

    public ObservableCollection<ResourceFieldRow> WoodFields => _woodFields;
    public ObservableCollection<ResourceFieldRow> ClayFields => _clayFields;
    public ObservableCollection<ResourceFieldRow> IronFields => _ironFields;
    public ObservableCollection<ResourceFieldRow> CroplandFields => _croplandFields;
    public ObservableCollection<BuildingSlotRow> BuildingSlots => _buildingRows;

    public MainWindow()
    {
        InitializeComponent();
        TryApplyWindowIcon();

        _projectRoot = ProjectRootLocator.FindProjectRoot();
        _versionPath = Path.Combine(_projectRoot, "VERSION");
        _botConfigPath = Path.Combine(_projectRoot, "config", "bot.json");
        _envPath = Path.Combine(_projectRoot, ".env");
        _queuePath = Path.Combine(_projectRoot, "config", "queue.json");
        _serverCatalogPath = Path.Combine(_projectRoot, "config", "servers.user.json");
        _sessionLogPath = Path.Combine(_projectRoot, "logs", $"TbotUltra_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        InitializeSessionLogFile();

        _botConfigStore = new BotConfigStore(_botConfigPath);
        _accountProvider = new EnvAccountProvider(_envPath);
        _accountStore = new EnvAccountStore(_envPath);
        _accountAnalysisStore = new AccountAnalysisStore(_projectRoot);
        _natarFarmCacheStore = new NatarFarmCacheStore(_projectRoot);
        _manualFarmingPreferenceStore = new ManualFarmingPreferenceStore(_projectRoot);
        _serverDiscoveryService = new ServerDiscoveryService();
        _serverCatalogStore = new ServerCatalogStore(_serverCatalogPath);
        var projectContext = new ProjectContext(_projectRoot);
        var captchaAutoSolver = new CaptchaAutoSolver(projectContext);
        var taskRunner = new BotTaskRunner(_accountProvider, projectContext, captchaAutoSolver);
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
            try
            {
                UpdateClockText();
                HandleBrowserClosedSignal();
                TickFarmListCountdowns();
                UpdateExecutionStateIndicator();
                UpdateManualFarmingRunningState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Clock tick failed: {ex}");
            }
        };
        _clockTimer.Start();

        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyFeedbackTextBlock.Visibility = Visibility.Collapsed;
        };
        _inboxRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _inboxRefreshTimer.Tick += async (_, _) => await HandleInboxRefreshTickAsync();
        _buildQueueCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _buildQueueCountdownTimer.Tick += (_, _) => TickBuildQueueCountdown();
        _buildQueueCountdownTimer.Start();
        _queueUiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _queueUiRefreshTimer.Tick += (_, _) =>
        {
            _queueUiRefreshTimer.Stop();
            var selectId = _pendingQueueUiSelectId;
            _pendingQueueUiSelectId = null;
            RefreshQueueUi(selectId);
        };

        _queueServerTimeOffset = ResolveQueueServerTimeOffset();

        TerminalListBox.ItemsSource = _terminalEntries;
        AlarmListBox.ItemsSource = _alarmEntries;
        UpdateCaptchaStatsUi();
        AutomationLoopListBox.ItemsSource = _automationLoopTasks;
        HeroAttributePriorityItemsControl.ItemsSource = _heroAttributePriorityItems;
        FarmListsItemsControl.ItemsSource = _farmLists;
        _farmLists.CollectionChanged += (_, _) =>
        {
            if (_suppressFarmListUiRefresh)
            {
                return;
            }

            UpdateFarmingUiState();
        };
        AlarmListBox.SelectionChanged += (_, _) => UpdateTerminalAlarmUi();
        TerminalListBox.SelectionChanged += (_, _) => UpdateTerminalAlarmUi();
        TerminalListBox.PreviewMouseLeftButtonDown += LogListBox_PreviewMouseLeftButtonDown;
        TerminalListBox.PreviewMouseMove += LogListBox_PreviewMouseMove;
        TerminalListBox.PreviewMouseLeftButtonUp += LogListBox_PreviewMouseLeftButtonUp;
        AlarmListBox.PreviewMouseLeftButtonDown += LogListBox_PreviewMouseLeftButtonDown;
        AlarmListBox.PreviewMouseMove += LogListBox_PreviewMouseMove;
        AlarmListBox.PreviewMouseLeftButtonUp += LogListBox_PreviewMouseLeftButtonUp;
        TerminalAlarmTabControl.SelectionChanged += (_, _) => UpdateTerminalAlarmUi();
        PreviewMouseDown += MainWindow_PreviewMouseDown;

        VillageComboBox.ItemsSource = new[]
        {
            new VillageSelectionItem { Name = "-", Url = string.Empty },
        };
        VillageComboBox.SelectedIndex = 0;
        VillageComboBox.SelectionChanged += VillageComboBox_SelectionChanged;
        ResourceTargetLevelComboBox.ItemsSource = Enumerable.Range(1, 40).ToList();
        ResourceTargetLevelComboBox.SelectedItem = 10;
        BuildingCategoryComboBox.ItemsSource = new[] { "all", "infrastructure", "army_buildings", "resource_buildings" };
        BuildingCategoryComboBox.SelectedIndex = 0;
        ConstructBuildingComboBox.ItemsSource = _buildingCatalogOptions;
        DemolishBuildingComboBox.ItemsSource = _demolishableBuildings;

        LoadConfigToUi();
        LoadVersionToUi();
        RefreshQueueUi();
        Closing += MainWindow_Closing;
        SetLoopIndicator(false);
        UpdateInboxButtons(0, 0);
        _inboxRefreshTimer.Start();
        AppendLog("Desktop app started.");
        AppendLog($"Session log file: {_sessionLogPath}");
        UpdateTerminalAlarmUi();
        UpdateLoginButtonsVisual(false);
        UpdateSidebarSelection(DashboardNavButton);
        UpdateFarmingUiState();
        UpdateManualFarmingExecutionCounter();
        SetNatarsProfileAnalyzed(false);
        LoadHeroPriorityToUi(null);
    }

    private void TryApplyWindowIcon()
    {
        // Keep the application icon from the exe when the .ico resource is not WPF-decodable.
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
        LoadAutomationLoopTasks(options);
        HeroMinHpTextBox.Text = Math.Clamp(options.HeroMinHpForAdventure, 1, 100).ToString();
        HeroAutoReviveCheckBox.IsChecked = options.HeroAutoRevive;
        HeroAutoAssignPointsCheckBox.IsChecked = options.HeroAutoAssignPoints;
        LoadHeroPriorityToUi(options.HeroStatPriority);

        try
        {
            var account = _accountProvider.LoadAccount();
            StatusTextBlock.Text = $"Loaded account '{account.Name}'.";
            AppendLog($"Loaded account '{account.Name}'.");
            UpdateAccountInfoLabel(account.Name);
            UpdateInboxButtons(0, 0);
            UpdateGoldClubInfoFromStoredAnalysis();
            RefreshNatarsProfileAnalyzedFromCache();
        }
        catch (Exception ex)
        {
            var hasConfiguredAccounts = _accountStore.ListAccounts().Count > 0;
            if (hasConfiguredAccounts)
            {
                StatusTextBlock.Text = "Failed to load account.";
                AppendLog($"Account load failed: {ex.Message}");
            }
            else
            {
                StatusTextBlock.Text = "No account configured yet. Open Manage to create one.";
            }

            UpdateInboxButtons(0, 0);
            UpdateGoldClubInfo(null);
            SetNatarsProfileAnalyzed(false);
        }

    }

    private void UpdateGoldClubInfo(bool? enabled)
    {
        GoldClubInfoTextBlock.Text = enabled == true
            ? "Goldclub: Yes"
            : "Goldclub: -";
        UpdateAccountInfoLabel(_accountStore.ActiveAccountName());
    }

    private void LoadHeroPriorityToUi(string? configuredPriority)
    {
        var order = ParseHeroPriorityForUi(configuredPriority);
        var existingPoints = _heroAttributePriorityItems.ToDictionary(item => item.Key, item => item.PointsText, StringComparer.OrdinalIgnoreCase);
        _heroAttributePriorityItems.Clear();

        for (var i = 0; i < order.Count; i++)
        {
            _heroAttributePriorityItems.Add(new HeroAttributePriorityItem
            {
                Key = order[i],
                Title = GetHeroAttributeTitle(order[i]),
                Order = i + 1,
                PointsText = existingPoints.GetValueOrDefault(order[i], "-"),
            });
        }
    }

    private void UpdateHeroPriorityOrders()
    {
        for (var i = 0; i < _heroAttributePriorityItems.Count; i++)
        {
            _heroAttributePriorityItems[i].Order = i + 1;
        }
    }

    private void HeroAttributePriorityItemsControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _heroPriorityDragStart = e.GetPosition(HeroAttributePriorityItemsControl);
        _heroPriorityDragSource = FindHeroAttributePriorityItem(e.OriginalSource as DependencyObject);
    }

    private void HeroAttributePriorityItemsControl_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _heroPriorityDragSource is null)
        {
            return;
        }

        var position = e.GetPosition(HeroAttributePriorityItemsControl);
        var delta = position - _heroPriorityDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(HeroAttributePriorityItemsControl, _heroPriorityDragSource, DragDropEffects.Move);
    }

    private void HeroAttributePriorityItemsControl_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(HeroAttributePriorityItem)))
        {
            return;
        }

        if (e.Data.GetData(typeof(HeroAttributePriorityItem)) is not HeroAttributePriorityItem sourceItem)
        {
            return;
        }

        var targetItem = FindHeroAttributePriorityItem(e.OriginalSource as DependencyObject);
        var fromIndex = _heroAttributePriorityItems.IndexOf(sourceItem);
        if (fromIndex < 0)
        {
            return;
        }

        var toIndex = targetItem is null
            ? _heroAttributePriorityItems.Count - 1
            : _heroAttributePriorityItems.IndexOf(targetItem);
        if (toIndex < 0 || fromIndex == toIndex)
        {
            return;
        }

        _heroAttributePriorityItems.Move(fromIndex, toIndex);
        UpdateHeroPriorityOrders();
    }

    private HeroAttributePriorityItem? FindHeroAttributePriorityItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: HeroAttributePriorityItem item })
            {
                return item;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void ApplyHeroAttributeSnapshotToUi(HeroAttributeSnapshot snapshot)
    {
        foreach (var item in _heroAttributePriorityItems)
        {
            var points = item.Key switch
            {
                "fighting_strength" => snapshot.FightingStrength,
                "offence_bonus" => snapshot.OffenceBonus,
                "defence_bonus" => snapshot.DefenceBonus,
                "resources" => snapshot.Resources,
                _ => 0,
            };
            item.PointsText = points.ToString();
        }

        HeroAttributesStatusTextBlock.Text = $"Free points: {snapshot.FreePoints}";
    }

    private string BuildHeroPriorityPayload()
    {
        return string.Join(",", _heroAttributePriorityItems.Select(item => item.Key));
    }

    private static List<string> ParseHeroPriorityForUi(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fighting_strength"] = "fighting_strength",
            ["fighting strength"] = "fighting_strength",
            ["fight"] = "fighting_strength",
            ["strength"] = "fighting_strength",
            ["offence_bonus"] = "offence_bonus",
            ["offence bonus"] = "offence_bonus",
            ["offense_bonus"] = "offence_bonus",
            ["offense bonus"] = "offence_bonus",
            ["offence"] = "offence_bonus",
            ["offense"] = "offence_bonus",
            ["off"] = "offence_bonus",
            ["attack"] = "offence_bonus",
            ["defence_bonus"] = "defence_bonus",
            ["defence bonus"] = "defence_bonus",
            ["defense_bonus"] = "defence_bonus",
            ["defense bonus"] = "defence_bonus",
            ["defence"] = "defence_bonus",
            ["defense"] = "defence_bonus",
            ["def"] = "defence_bonus",
            ["resources"] = "resources",
            ["resource"] = "resources",
            ["production"] = "resources",
        };

        var parsed = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => map.GetValueOrDefault(item, string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallback in new[] { "fighting_strength", "offence_bonus", "defence_bonus", "resources" })
        {
            if (!parsed.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                parsed.Add(fallback);
            }
        }

        return parsed;
    }

    private static string GetHeroAttributeTitle(string key) => key switch
    {
        "fighting_strength" => "Fighting strength",
        "offence_bonus" => "Offence bonus",
        "defence_bonus" => "Defence bonus",
        "resources" => "Resources",
        _ => key,
    };

    private void UpdateGoldClubInfoFromStoredAnalysis()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null
                && analysis.GoldClubEnabled)
            {
                UpdateGoldClubInfo(true);
                return;
            }
        }
        catch
        {
            // Ignore temporary read failures and fall back to unknown.
        }

        UpdateGoldClubInfo(null);
    }

    private void RefreshNatarsProfileAnalyzedFromCache()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            var serverUrl = GetActiveAccountServerUrl();
            var selectionMode = LoadBotOptions().NatarVillageSelection;
            var analyzed = !string.IsNullOrWhiteSpace(accountName)
                && !string.IsNullOrWhiteSpace(serverUrl)
                && _natarFarmCacheStore.IsAnalyzed(accountName, serverUrl, selectionMode);
            SetNatarsProfileAnalyzed(analyzed);
        }
        catch
        {
            SetNatarsProfileAnalyzed(false);
        }
    }

    private NatarFarmCacheSnapshot? TryLoadActiveNatarFarmSnapshot()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            var serverUrl = GetActiveAccountServerUrl();
            var selectionMode = LoadBotOptions().NatarVillageSelection;
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(serverUrl))
            {
                return null;
            }

            return _natarFarmCacheStore.TryLoad(accountName, out var snapshot, serverUrl, selectionMode)
                ? snapshot
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void LoadAutomationLoopTasks(BotOptions options)
    {
        var configured = (options.LoopTasks ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var known = AutomationLoopTaskCatalog.ToDictionary(item => item.TaskName, item => item, StringComparer.OrdinalIgnoreCase);
        var orderedNames = configured
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _suppressAutomationLoopConfigWrite = true;
        try
        {
            _automationLoopTasks.Clear();
            foreach (var taskName in orderedNames)
            {
                if (known.TryGetValue(taskName, out var catalogItem))
                {
                    _automationLoopTasks.Add(new LoopTaskOption
                    {
                        TaskName = catalogItem.TaskName,
                        Title = catalogItem.Title,
                        Description = catalogItem.Description,
                        IsEnabled = configured.Contains(taskName, StringComparer.OrdinalIgnoreCase),
                    });
                }
                else
                {
                    _automationLoopTasks.Add(new LoopTaskOption
                    {
                        TaskName = taskName,
                        Title = HumanizeTaskName(taskName),
                        Description = "Custom loop task from bot.json.",
                        IsEnabled = true,
                    });
                }
            }

            UpdateAutomationLoopOrders();
            UpdateAutomationLoopSummaryText();
            UpdateAutomationLoopRunningIndicators();
        }
        finally
        {
            _suppressAutomationLoopConfigWrite = false;
        }
    }

    private void UpdateAutomationLoopOrders()
    {
        for (var i = 0; i < _automationLoopTasks.Count; i++)
        {
            _automationLoopTasks[i].Order = i + 1;
        }
    }

    private void UpdateAutomationLoopSummaryText()
    {
        var enabledCount = _automationLoopTasks.Count(item => item.IsEnabled);
        AutomationLoopSummaryTextBlock.Text = enabledCount <= 0
            ? "No loop task enabled. Enable at least one task for continuous loop."
            : $"Loop cycles through {enabledCount} enabled task(s). Drag cards to change execution order.";
        UpdateAutomationLoopColumns();
    }

    private void UpdateAutomationLoopColumns()
    {
        if (AutomationLoopListBox is null)
        {
            return;
        }

        var columns = _automationLoopTasks.Count <= 7 ? 1 : 2;
        var factory = new FrameworkElementFactory(typeof(VerticalFirstUniformGrid));
        factory.SetValue(VerticalFirstUniformGrid.ColumnsProperty, columns);
        AutomationLoopListBox.ItemsPanel = new ItemsPanelTemplate(factory);
    }

    private void SetActiveAutomationTask(string? taskName)
    {
        void Apply()
        {
            _activeAutomationTaskName = string.IsNullOrWhiteSpace(taskName)
                ? null
                : taskName;
            UpdateAutomationLoopRunningIndicators();
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        _ = Dispatcher.BeginInvoke((Action)Apply);
    }

    private void SetActiveFunctionExecution(string? displayName)
    {
        void Apply()
        {
            _activeFunctionDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? null
                : displayName;
            UpdateExecutionStateIndicator();
        }

        if (Dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        _ = Dispatcher.BeginInvoke((Action)Apply);
    }

    private bool IsFunctionExecutionRunning(bool hasRunningQueueItems)
    {
        return hasRunningQueueItems
            || _autoQueueRunning
            || _uiBusy
            || !string.IsNullOrWhiteSpace(_activeFunctionDisplayName);
    }

    private void UpdateAutomationLoopRunningIndicators()
    {
        var isRunning = (_loopTask is not null && !_loopTask.IsCompleted) || _autoQueueRunning;
        var hasPausedQueueItems = false;
        string? queueRunningTaskName = null;
        try
        {
            var queueItems = _botService.GetQueueItemsForDisplay();
            hasPausedQueueItems = queueItems.Any(item => item.Status == QueueStatus.Paused);
            queueRunningTaskName = queueItems.FirstOrDefault(item => item.Status == QueueStatus.Running)?.TaskName;
        }
        catch
        {
            // Ignore temporary queue read failures.
        }

        var runningTaskName = !string.IsNullOrWhiteSpace(queueRunningTaskName)
            ? queueRunningTaskName
            : _activeAutomationTaskName;
        if (string.IsNullOrWhiteSpace(runningTaskName) && isRunning)
        {
            runningTaskName = _automationLoopTasks.FirstOrDefault(item => item.IsEnabled)?.TaskName;
        }

        foreach (var item in _automationLoopTasks)
        {
            item.IsRunning = !string.IsNullOrWhiteSpace(runningTaskName)
                && string.Equals(item.TaskName, runningTaskName, StringComparison.OrdinalIgnoreCase);
        }

        if (isRunning)
        {
            AutomationLoopRunStateDot.Fill = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            AutomationLoopRunStateTextBlock.Text = "Running";
            AutomationLoopRunStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            return;
        }

        if (hasPausedQueueItems)
        {
            AutomationLoopRunStateDot.Fill = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            AutomationLoopRunStateTextBlock.Text = "Paused";
            AutomationLoopRunStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            return;
        }

        AutomationLoopRunStateDot.Fill = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        AutomationLoopRunStateTextBlock.Text = "Idle";
        AutomationLoopRunStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
    }

    private void PersistAutomationLoopTasksToConfig()
    {
        if (_suppressAutomationLoopConfigWrite)
        {
            return;
        }

        try
        {
            var enabledTaskNames = _automationLoopTasks
                .Where(item => item.IsEnabled)
                .Select(item => item.TaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            var config = _botConfigStore.Load();
            config["loop_tasks"] = new JsonArray(enabledTaskNames.Select(name => JsonValue.Create(name)!).ToArray());
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save loop task order: {ex.Message}");
        }
    }

    private static string HumanizeTaskName(string taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return "-";
        }

        return string.Join(
            " ",
            taskName
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length == 1
                    ? char.ToUpperInvariant(part[0]).ToString()
                    : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private void AutomationLoopToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: LoopTaskOption option } toggle)
        {
            return;
        }

        option.IsEnabled = toggle.IsChecked == true;
        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void AutomationLoopListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _automationLoopDragStart = e.GetPosition(AutomationLoopListBox);
        _automationLoopDragSource = FindAutomationLoopTask(e.OriginalSource as DependencyObject);
    }

    private void AutomationLoopListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _automationLoopDragSource is null)
        {
            return;
        }

        var position = e.GetPosition(AutomationLoopListBox);
        var delta = position - _automationLoopDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(AutomationLoopListBox, _automationLoopDragSource, DragDropEffects.Move);
    }

    private void AutomationLoopListBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(LoopTaskOption))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void AutomationLoopListBox_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(LoopTaskOption)))
        {
            return;
        }

        if (e.Data.GetData(typeof(LoopTaskOption)) is not LoopTaskOption sourceOption)
        {
            return;
        }

        var targetOption = FindAutomationLoopTask(e.OriginalSource as DependencyObject);
        var fromIndex = _automationLoopTasks.IndexOf(sourceOption);
        if (fromIndex < 0)
        {
            return;
        }

        var toIndex = targetOption is null
            ? _automationLoopTasks.Count - 1
            : _automationLoopTasks.IndexOf(targetOption);
        if (toIndex < 0)
        {
            toIndex = _automationLoopTasks.Count - 1;
        }

        if (fromIndex == toIndex)
        {
            return;
        }

        _automationLoopTasks.Move(fromIndex, toIndex);
        UpdateAutomationLoopOrders();
        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private LoopTaskOption? FindAutomationLoopTask(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: LoopTaskOption option })
            {
                return option;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void UpdateFarmingUiState()
    {
        if (!_farmingFeaturesAvailable || FarmingStatusTextBlock is null)
        {
            return;
        }

        if (_farmLists.Count <= 0)
        {
            FarmingStatusTextBlock.Text = "No farm lists loaded. Click Analyze Farmlists.";
            return;
        }

        var readyCount = _farmLists.Count(item => item.IsReady);
        FarmingStatusTextBlock.Text = $"Loaded {_farmLists.Count} farm list(s). Ready: {readyCount}.";
    }

    private void SetFarmingFeatureAvailability(bool enabled, string? reason = null)
    {
        _farmingFeaturesAvailable = enabled;
        SyncFarmingControlsEnabledState();

        if (!enabled)
        {
            if (FarmingStatusTextBlock is not null)
            {
                FarmingStatusTextBlock.Text = string.IsNullOrWhiteSpace(reason)
                    ? "Farming is unavailable for this account."
                    : reason;
            }
        }
        else
        {
            UpdateFarmingUiState();
        }
    }

    private void TickFarmListCountdowns()
    {
        if (_farmLists.Count <= 0)
        {
            return;
        }

        var changed = false;
        var snapshot = _farmLists.ToList();
        foreach (var list in snapshot)
        {
            changed |= list.TickOneSecond();
        }

        if (changed)
        {
            UpdateFarmingUiState();
        }
    }

    private async Task<bool> RefreshFarmListsFromServerAsync(BotOptions options, CancellationToken cancellationToken)
    {
        var goldClubEnabled = await _botService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, cancellationToken);
        UpdateGoldClubInfo(goldClubEnabled ? true : null);
        if (!goldClubEnabled)
        {
            _farmLists.Clear();
            SetFarmingFeatureAvailability(false, "Farming unavailable: Gold Club is not active on this account.");
            return false;
        }

        var lists = await _botService.ReadFarmListsOverviewAsync(options, AppendLog, cancellationToken) ?? [];
        _suppressFarmListUiRefresh = true;
        try
        {
            _farmLists.Clear();
            var mergedByName = new Dictionary<string, (int Active, int Total, int? RemainingSeconds)>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in lists)
            {
                if (list is null)
                {
                    continue;
                }

                var normalizedName = string.IsNullOrWhiteSpace(list.Name) ? "Farm list" : list.Name.Trim();
                if (!mergedByName.TryGetValue(normalizedName, out var existing))
                {
                    mergedByName[normalizedName] = (
                        Active: Math.Max(0, list.ActiveFarmCount),
                        Total: Math.Max(0, list.TotalFarmCount),
                        RemainingSeconds: list.RemainingSeconds is > 0 ? list.RemainingSeconds : null);
                    continue;
                }

                var incomingRemaining = list.RemainingSeconds is > 0 ? list.RemainingSeconds : null;
                mergedByName[normalizedName] = (
                    Active: Math.Max(existing.Active, Math.Max(0, list.ActiveFarmCount)),
                    Total: Math.Max(existing.Total, Math.Max(0, list.TotalFarmCount)),
                    RemainingSeconds: existing.RemainingSeconds is > 0
                        ? existing.RemainingSeconds
                        : incomingRemaining);
            }

            var displayedRows = 0;
            foreach (var pair in mergedByName.OrderBy(pair => pair.Key))
            {
                if (displayedRows >= MaxFarmListsShown)
                {
                    break;
                }

                _farmLists.Add(new FarmListStatusRow
                {
                    Name = pair.Key,
                    ActiveFarmCount = pair.Value.Active,
                    TotalFarmCount = pair.Value.Total,
                    IsEnabled = true,
                    RemainingSeconds = pair.Value.RemainingSeconds,
                });
                displayedRows++;
            }

            if (mergedByName.Count > MaxFarmListsShown)
            {
                AppendLog($"Farm list UI limited to {MaxFarmListsShown} rows (detected {mergedByName.Count}).");
            }
        }
        finally
        {
            _suppressFarmListUiRefresh = false;
        }

        SetFarmingFeatureAvailability(true);
        UpdateFarmingUiState();
        RefreshFarmListsItemsControl();
        return true;
    }

    private async void AnalyzeFarmListsButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("Analyze Farmlists");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingOperationBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var available = await RefreshFarmListsFromServerAsync(options, operationToken);
            CompleteOperation(operationId, operationSw, available
                ? $"Loaded {_farmLists.Count} farm list(s)."
                : "Gold Club is not active.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Analyze farmlists paused.");
        }
        catch (Exception ex)
        {
            if (FarmingStatusTextBlock is not null)
            {
                FarmingStatusTextBlock.Text = "Analyze failed. Previous farm list state kept.";
            }
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            SetFarmingOperationBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void AddFarmsToListButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Add Farms to List is unavailable while Gold Club farming is disabled.");
            return;
        }

        var operationId = BeginOperation("Add Farms To List");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingOperationBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var available = await RefreshFarmListsFromServerAsync(options, operationToken);
            if (!available)
            {
                CompleteOperation(operationId, operationSw, "Gold Club is not active.");
                return;
            }

            if (_farmLists.Count <= 0)
            {
                AppDialog.Show(this, "No farm lists found on farmpage.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Information);
                CompleteOperation(operationId, operationSw, "No farm lists found.");
                return;
            }

            var optionsForDialog = _farmLists
                .Select(item => new FarmListSelectionOption
                {
                    Name = item.Name,
                    ActiveFarmCount = item.ActiveFarmCount,
                    TotalFarmCount = item.TotalFarmCount,
                })
                .ToList();

            var natarFarmCount = await _botService.EnsureNatarFarmCacheAndReturnToFarmListAsync(options, AppendLog, false, operationToken);
            if (natarFarmCount <= 0)
            {
                SetNatarsProfileAnalyzed(false);
                AppDialog.Show(this, "No villages named 'Natar farm village' were found.", "Add farms", MessageBoxButton.OK, MessageBoxImage.Information);
                CompleteOperation(operationId, operationSw, "No matching Natar farms found.");
                return;
            }

            SetNatarsProfileAnalyzed(true);

            var dialog = new AddFarmsToListWindow(optionsForDialog, ResolveCurrentTribeForFarming(), natarFarmCount)
            {
                Owner = this,
            };
            var addRequested = dialog.ShowDialog() == true && dialog.SelectedOption is not null;
            if (!addRequested)
            {
                CompleteOperation(operationId, operationSw, "Add farms canceled.");
                return;
            }

            var selected = dialog.SelectedOption!;
            AppendLog($"Add farms selected list: {selected.Name} ({selected.CountText}, {selected.CapacityText}).");
            var result = await _botService.AddFarmsFromNatarsAsync(
                options,
                selected.Name,
                dialog.SelectedTroopType,
                dialog.TroopCount,
                dialog.RequestedFarmCount,
                AppendLog,
                operationToken);
            var selectedRow = _farmLists.FirstOrDefault(item => string.Equals(item.Name, selected.Name, StringComparison.OrdinalIgnoreCase));
            if (selectedRow is not null && result.AddedCount > 0)
            {
                selectedRow.ActiveFarmCount = Math.Min(selectedRow.TotalFarmCount, selectedRow.ActiveFarmCount + result.AddedCount);
                UpdateFarmingUiState();
            }

            if (result.AlreadyInListCount > 0)
            {
                AppendLog($"Duplicate farms detected: {result.AlreadyInListCount} result(s) with 'This village is already in the selected farm list.'.");
            }

            await RefreshFarmListsFromServerAsync(options, operationToken);
            AppDialog.Show(
                this,
                $"Added: {result.AddedCount}, Already in list: {result.AlreadyInListCount}, Failed: {result.FailedCount}.",
                "Add farms",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            CompleteOperation(operationId, operationSw, $"Add farms done. Added={result.AddedCount}, Existing={result.AlreadyInListCount}, Failed={result.FailedCount}.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Add farms paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            SetFarmingOperationBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void AnalyzeNatarsProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Analyze natars profile is unavailable while Gold Club farming is disabled.");
            return;
        }

        var operationId = BeginOperation("Analyze Natars Profile");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingOperationBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var natarFarmCount = await _botService.EnsureNatarFarmCacheAndReturnToFarmListAsync(options, AppendLog, true, operationToken);
            var analyzed = natarFarmCount > 0 || _natarsProfileAnalyzed;
            SetNatarsProfileAnalyzed(analyzed);
            if (natarFarmCount > 0)
            {
                CompleteOperation(operationId, operationSw, $"Natars analyzed. Farms found: {natarFarmCount}.");
            }
            else if (analyzed)
            {
                CompleteOperation(operationId, operationSw, "No new Natar farms found. Existing cached analysis kept.");
            }
            else
            {
                CompleteOperation(operationId, operationSw, "No matching Natar farms found.");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Analyze natars profile paused.");
        }
        catch (Exception ex)
        {
            RefreshNatarsProfileAnalyzedFromCache();
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            SetFarmingOperationBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void ShowNatarsListButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_natarsProfileAnalyzed)
        {
            return;
        }

        var snapshot = TryLoadActiveNatarFarmSnapshot();
        var missingVillageNames = snapshot is not null
            && snapshot.Coordinates.Count > 0
            && snapshot.Coordinates.All(item => string.IsNullOrWhiteSpace(item.VillageName));
        if (missingVillageNames)
        {
            try
            {
                var options = ApplySelectedVillageToOptions(LoadBotOptions());
                await EnsureChromiumInstalledAsync();
                await _botService.EnsureNatarFarmCacheAndReturnToFarmListAsync(options, AppendLog, true, CancellationToken.None);
                snapshot = TryLoadActiveNatarFarmSnapshot();
            }
            catch (Exception ex)
            {
                AppendLog($"Could not refresh Natar villages before showing list: {ex.Message}");
            }
        }

        if (snapshot is null || snapshot.Coordinates.Count <= 0)
        {
            SetNatarsProfileAnalyzed(false);
            AppDialog.Show(this, "No analyzed Natars list is available for the active account.", "Natars list", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rows = snapshot.Coordinates
            .Select((item, index) => new NatarListRow(
                index + 1,
                string.IsNullOrWhiteSpace(item.VillageName) ? "-" : item.VillageName,
                item.X,
                item.Y))
            .ToList();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            CanUserResizeRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            Margin = new Thickness(0, 0, 0, 10),
            ItemsSource = rows,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(NatarListRow.Index)), Width = new DataGridLength(70) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Village", Binding = new Binding(nameof(NatarListRow.VillageName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "X", Binding = new Binding(nameof(NatarListRow.X)), Width = new DataGridLength(90) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Y", Binding = new Binding(nameof(NatarListRow.Y)), Width = new DataGridLength(90) });

        var summaryText = new TextBlock
        {
            Text = $"Entries: {rows.Count:N0} | Mode: {(string.Equals(snapshot.SelectionMode, "all_villages", StringComparison.OrdinalIgnoreCase) ? "All villages" : "Farm villages")}",
            Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var closeButton = new Button
        {
            Content = "Close",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var popup = new Window
        {
            Title = "Natars list",
            Owner = this,
            Width = 520,
            Height = 620,
            MinWidth = 420,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    closeButton,
                    summaryText,
                    grid,
                },
            },
        };

        DockPanel.SetDock(closeButton, Dock.Bottom);
        DockPanel.SetDock(summaryText, Dock.Top);

        closeButton.Click += (_, _) => popup.Close();
        popup.ShowDialog();
    }

    private void StartManualFarmingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_farmingOperationBusy)
        {
            return;
        }

        _ = StartManualFarmingAsync();
    }

    private async Task StartManualFarmingAsync()
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Start manual farming is unavailable while Gold Club farming is disabled.");
            return;
        }

        var currentOptions = LoadBotOptions();
        var activeAccountName = _accountStore.ActiveAccountName();
        var preferences = _manualFarmingPreferenceStore.Load(activeAccountName);
        var dialog = new ManualFarmingWindow(
            ResolveCurrentTribeForFarming(),
            currentOptions.NatarVillageSelection,
            preferences.TroopCount,
            preferences.VariancePercent)
        {
            Owner = this,
            PreferenceChanged = (troopCount, variancePercent) =>
            {
                _manualFarmingPreferenceStore.Save(activeAccountName, new ManualFarmingPreference(troopCount, variancePercent));
            },
        };

        if (dialog.ShowDialog() != true)
        {
            AppendLog("Manual farming canceled.");
            return;
        }

        var operationId = BeginOperation("Start Manual Farming");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingOperationBusy(true);
        try
        {
            var options = ApplyManualFarmingSelectionToOptions(
                ApplySelectedVillageToOptions(currentOptions),
                dialog.NatarVillageSelection);
            await EnsureChromiumInstalledAsync();
            var runIndex = 0;
            while (true)
            {
                operationToken.ThrowIfCancellationRequested();
                runIndex++;
                AppendLog($"Manual farming loop {runIndex} started.");

                var result = await _botService.StartManualFarmingFromNatarsAsync(
                    options,
                    dialog.SelectedTroopType,
                    dialog.TroopCount,
                    dialog.TroopVariancePercent,
                    dialog.IsRaid,
                    AppendLog,
                    operationToken);
                SetNatarsProfileAnalyzed(result.TotalTargets > 0);

                if (result.StoppedByNoTroopsAlarm)
                {
                    AppDialog.Show(
                        this,
                        $"Manual farming stopped by alarm after loop {runIndex}. Sent: {result.SentCount}, Skipped: {result.SkippedCount}, Failed: {result.FailedCount}, Targets: {result.TotalTargets}.",
                        "Manual farming",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    CompleteOperation(operationId, operationSw, $"Manual farming stopped by troop alarm on loop {runIndex}.");
                    break;
                }

                AppendLog($"Manual farming loop {runIndex} done. Sent={result.SentCount}, Skipped={result.SkippedCount}, Failed={result.FailedCount}, Targets={result.TotalTargets}. Restarting...");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Manual farming canceled.");
            CompleteOperation(operationId, operationSw, "Manual farming stopped by user.");
        }
        catch (Exception ex)
        {
            RefreshNatarsProfileAnalyzedFromCache();
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            SetFarmingOperationBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private string ResolveCurrentTribeForFarming()
    {
        var tribeFromUi = TribeInfoTextBlock.Text?.Replace("Tribe:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (!string.IsNullOrWhiteSpace(tribeFromUi) && !string.Equals(tribeFromUi, "-", StringComparison.OrdinalIgnoreCase))
        {
            return tribeFromUi;
        }

        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null
                && !string.IsNullOrWhiteSpace(analysis.Tribe))
            {
                return analysis.Tribe;
            }
        }
        catch
        {
            // Ignore lookup errors and use fallback tribe.
        }

        return "Unknown";
    }

    private void CreateFarmListButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_farmingFeaturesAvailable)
        {
            AppendLog("Create Farmlist is unavailable while Gold Club farming is disabled.");
            return;
        }

        AppendLog("Create Farmlist clicked. Wiring to farm page action is not connected yet.");
    }

    private async void FarmListSendNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FarmListStatusRow list })
        {
            return;
        }

        if (!list.CanSendNow)
        {
            return;
        }

        var operationId = BeginOperation("Farm Send Now");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingOperationBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            var timerSeconds = await _botService.SendFarmListNowAsync(options, list.Name, AppendLog, operationToken);
            list.RemainingSeconds = timerSeconds is > 0 ? timerSeconds : null;
            UpdateFarmingUiState();
            CompleteOperation(operationId, operationSw, $"Sent '{list.Name}'.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Farm list send paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            SetFarmingOperationBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private void LoadVersionToUi()
    {
        try
        {
            var version = File.Exists(_versionPath)
                ? File.ReadAllText(_versionPath).Trim()
                : "dev";
            if (string.IsNullOrWhiteSpace(version))
            {
                version = "dev";
            }

            VersionTextBlock.Text = $"Version: {version}";
        }
        catch
        {
            VersionTextBlock.Text = "Version: -";
        }
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

            QueueItem? item;
            if (string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
                && TryReadResourceUpgradePayload(payload, out var slotId, out var targetLevel))
            {
                item = EnqueueResourceUpgradeTaskCoalesced(
                    payload,
                    slotId,
                    targetLevel,
                    out var effectiveTargetLevel,
                    out var enqueued,
                    out var removedCount);
                SetPendingResourceLevel(slotId, effectiveTargetLevel);
                if (enqueued && item is not null)
                {
                    var removedSuffix = removedCount > 0 ? $", removed {removedCount} stale item(s)" : string.Empty;
                    AppendLog($"Queued task: {taskName} slot={slotId} target={effectiveTargetLevel} (priority={item.Priority}, maxRetries={item.MaxRetries}{removedSuffix}).");
                }
                else
                {
                    AppendLog($"Skipped duplicate queue item: {taskName} slot={slotId} target={effectiveTargetLevel} (already queued/running).");
                }
            }
            else
            {
                item = _botService.Enqueue(taskName, payload, priority: 0, maxRetries: 3);
                AppendLog($"Queued task: {taskName} (priority={item.Priority}, maxRetries={item.MaxRetries}).");
            }

            RequestQueueUiRefresh(selectId: item?.Id);
            TriggerQueueAutoRunFromEnqueue();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not queue task '{taskName}': {ex.Message}");
        }
    }

    private static bool TryReadResourceUpgradePayload(IReadOnlyDictionary<string, string> payload, out int slotId, out int targetLevel)
    {
        slotId = 0;
        targetLevel = 0;
        if (!payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out slotId))
        {
            return false;
        }

        if (!payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var targetRaw)
            || !int.TryParse(targetRaw, out targetLevel))
        {
            return false;
        }

        if (slotId < 1 || slotId > 18 || targetLevel <= 0)
        {
            return false;
        }

        targetLevel = Math.Clamp(targetLevel, 1, ResourceFieldMaxLevel);
        return true;
    }

    private static bool IsActiveResourceQueueStatus(QueueStatus status)
    {
        return status is QueueStatus.Pending or QueueStatus.Paused or QueueStatus.Running;
    }

    private QueueItem? EnqueueResourceUpgradeTaskCoalesced(
        Dictionary<string, string> payload,
        int slotId,
        int requestedTargetLevel,
        out int effectiveTargetLevel,
        out bool enqueued,
        out int removedCount)
    {
        var relatedItems = _botService.GetQueueItemsForDisplay()
            .Where(item => string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase))
            .Where(item => IsActiveResourceQueueStatus(item.Status))
            .Select(item =>
            {
                var parsed = TryReadResourceUpgradePayload(item.Payload, out var parsedSlotId, out var parsedTargetLevel);
                return new
                {
                    Item = item,
                    Parsed = parsed,
                    SlotId = parsedSlotId,
                    TargetLevel = parsedTargetLevel,
                };
            })
            .Where(item => item.Parsed && item.SlotId == slotId)
            .ToList();

        var highestExistingTarget = relatedItems.Count == 0
            ? 0
            : relatedItems.Max(item => item.TargetLevel);
        effectiveTargetLevel = Math.Max(requestedTargetLevel, highestExistingTarget);

        if (highestExistingTarget >= requestedTargetLevel)
        {
            enqueued = false;
            removedCount = 0;
            return relatedItems
                .OrderByDescending(item => item.TargetLevel)
                .ThenBy(item => item.Item.CreatedAt)
                .Select(item => item.Item)
                .FirstOrDefault();
        }

        removedCount = 0;
        foreach (var related in relatedItems.Where(item => item.Item.Status is QueueStatus.Pending or QueueStatus.Paused))
        {
            if (_botService.RemoveQueueItem(related.Item.Id))
            {
                removedCount += 1;
            }
        }

        payload[BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = effectiveTargetLevel.ToString();
        var created = _botService.Enqueue("upgrade_resource_to_level", payload, priority: 0, maxRetries: 3);
        enqueued = true;
        return created;
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
            AppendLog("Login started.");
            await _botService.ExecuteLoginAsync(
                options,
                AppendLog,
                keepBrowserOpenAfterLogin: !options.Headless,
                cancellationToken: operationToken);
            AppendLog("Login finished.");

            BrowserInfoTextBlock.Text = "Browser: idle";
            StatusTextBlock.Text = "Login completed.";
            UpdateLoginButtonsVisual(true);
            _isLoggedIn = true;
            _browserSessionLikelyOpen = !options.Headless;
            _inboxAutoEnabled = true;
            RefreshNatarsProfileAnalyzedFromCache();
            await RefreshInboxIndicatorsAsync(logErrors: true, force: true);
            await LoadCurrentVillageViewsAfterLoginAsync(options, operationToken);
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
            StatusTextBlock.Text = TryGetFriendlyLoginError(ex) ?? "Login failed.";
            _browserSessionLikelyOpen = false;
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
            _isLoggedIn = false;
            _browserSessionLikelyOpen = false;
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

    private async void AnalyzeProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("Analyze Profile");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] INFO analyzing profile for server {options.ServerName}");
            await EnsureChromiumInstalledAsync();
            var snapshot = await _botService.AnalyzeProfileAsync(options, AppendLog, operationToken);

            TribeInfoTextBlock.Text = $"Tribe: {snapshot.Tribe}";
            VillagesInfoTextBlock.Text = $"Villages: {snapshot.VillageCount}";
            try
            {
                var goldClubEnabled = await _botService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, operationToken);
                UpdateGoldClubInfo(goldClubEnabled ? true : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendLog($"Could not refresh Gold Club status: {ex.Message}");
                UpdateGoldClubInfoFromStoredAnalysis();
            }

            var currentSelectedName = GetSelectedVillageName();
            var villages = snapshot.Villages
                .Select(v => new VillageSelectionItem
                {
                    Name = string.IsNullOrWhiteSpace(v.Name) ? "-" : v.Name,
                    Url = v.Url ?? string.Empty,
                    IsCapital = v.IsCapital == true,
                    CoordX = v.CoordX,
                    CoordY = v.CoordY,
                    Population = v.Population,
                    CropFields = v.CropFields,
                })
                .ToList();

            if (villages.Count == 0)
            {
                villages.Add(new VillageSelectionItem { Name = "-", Url = string.Empty });
            }

            _suppressVillageSelectionChange = true;
            try
            {
                VillageComboBox.ItemsSource = villages;
                var selected = villages.FirstOrDefault(v =>
                    string.Equals(v.Name, currentSelectedName, StringComparison.OrdinalIgnoreCase))
                    ?? villages.FirstOrDefault(v =>
                        string.Equals(v.Name, snapshot.ActiveVillage, StringComparison.OrdinalIgnoreCase))
                    ?? villages[0];
                VillageComboBox.SelectedItem = selected;
            }
            finally
            {
                _suppressVillageSelectionChange = false;
            }

            var capitalVillage = snapshot.Villages.FirstOrDefault(v => v.IsCapital == true);
            var capitalName = capitalVillage?.Name ?? "Unknown";
            AppendLog($"Profile analyzed: Tribe={snapshot.Tribe}, Capital={capitalName}, Villages={snapshot.VillageCount}");
            StatusTextBlock.Text = $"Profile analyzed. Capital: {capitalName}";
            CompleteOperation(operationId, operationSw, "Profile analyzed.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Profile analysis paused.";
            AppendLog("Profile analysis paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            StatusTextBlock.Text = "Profile analysis failed.";
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
        var previouslyActiveAccount = _accountStore.ActiveAccountName();
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var defaultServers = await FetchDefaultServerOptionsAsync(options);
        var servers = FetchEffectiveServerOptions(defaultServers);
        var window = new AccountsWindow(_accountStore, _serverCatalogStore, options.ServerName, options.BaseUrl, servers, defaultServers)
        {
            Owner = this,
        };
        var result = window.ShowDialog();
        var runAnalysisRequested = result == true && window.RequestedRunAnalysisForActiveAccount;
        var activeAccountAfterDialog = _accountStore.ActiveAccountName();
        if (!string.Equals(previouslyActiveAccount, activeAccountAfterDialog, StringComparison.OrdinalIgnoreCase))
        {
            ResetVillageSelectionUi();
        }

        LoadConfigToUi();
        if (runAnalysisRequested)
        {
            await EnsureLoggedInForAnalysisAsync();
            var activeAccount = _accountStore.ActiveAccountName();
            EnqueueQuickTask("account_full_analysis", $"Run full analysis for account {activeAccount}");
            StatusTextBlock.Text = $"Queued full analysis for '{activeAccount}'.";
        }
    }

    private async void RunActiveAccountAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        var activeAccount = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(activeAccount))
        {
            StatusTextBlock.Text = "No active account selected.";
            return;
        }

        await EnsureLoggedInForAnalysisAsync();
        EnqueueQuickTask("account_full_analysis", $"Run full analysis for account {activeAccount}");
        StatusTextBlock.Text = $"Queued full analysis for '{activeAccount}'.";
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
            ResetVillageSelectionUi();
            SyncServerFromActiveAccount();
            LoadConfigToUi();
            _inboxAutoEnabled = false;
            UpdateInboxButtons(0, 0);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not change active account: {ex.Message}");
            RefreshAccountPicker();
        }
    }

    private void ResetVillageSelectionUi()
    {
        _suppressVillageSelectionChange = true;
        try
        {
            VillageComboBox.ItemsSource = new[]
            {
                new VillageSelectionItem { Name = "-", Url = string.Empty },
            };
            VillageComboBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressVillageSelectionChange = false;
        }
    }

    private async void ResetProgramButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = AppDialog.Show(
            "This will reset internal state, stop running operations, and clear queue data.\n\nThe program will remain open.\n\nContinue?",
            "Reset program",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await ResetProgramInternalAsync();
    }

    private async Task ResetProgramInternalAsync()
    {
        try
        {
            _loopStopRequested = true;
            _queueStopRequested = true;
            _operationCts?.Cancel();
            _autoQueueRunCts?.Cancel();
            _loopCts?.Cancel();
            _villageSwitchCts?.Cancel();

            var stopDeadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < stopDeadline)
            {
                if (!_uiBusy && !_autoQueueRunning && (_loopTask is null || _loopTask.IsCompleted))
                {
                    break;
                }

                await Task.Delay(120);
            }

            _botService.ClearQueue();
            _resourceClickCooldownBySlot.Clear();
            _resourceLastQueuedTargetBySlot.Clear();
            _resourcePendingTargetBySlot.Clear();
            _buildingClickCooldownBySlot.Clear();
            _buildingLastQueuedTargetBySlot.Clear();
            _buildingLastQueuedConstructBySlot.Clear();
            _buildQueueActiveCount = 0;
            _buildQueueRemainingSeconds = -1;
            _activeVillageResourceMaxLevel = NonCapitalResourceMaxLevel;
            _lastBuildingStatus = null;
            _analysisPromptDismissed.Clear();

            SetResourceRows([]);
            _buildingRows.Clear();
            _demolishableBuildings.Clear();
            BuildQueueStatusTextBlock.Text = "Build queue: idle";

            RefreshQueueUi();
            UpdateExecutionStateIndicator();
            StatusTextBlock.Text = "Program reset completed.";
            AppendLog("Program reset completed. Internal state, running actions, and queue were reset.");
        }
        catch (Exception ex)
        {
            AppendLog($"Program reset failed: {ex.Message}");
        }
    }

    private async void LoadResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("LoadResources");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            await EnsureChromiumInstalledAsync();
            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: true, forceCurrentVillage: true);
            var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
            var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
            _resourcePendingTargetBySlot.Clear();

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
                    IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
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

    private async void UpgradeAllResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResources");
        if (ResourceTargetLevelComboBox.SelectedItem is not int targetLevel)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | No target level selected.");
            return;
        }

        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = LoadBotOptions();
            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: true, forceCurrentVillage: true);
            var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
            var requestedTargetLevel = Math.Min(targetLevel, resourceMaxLevel);
            _resourcePendingTargetBySlot.Clear();
            var rows = status.ResourceFields
                .Where(item => item.SlotId is not null && item.Level is not null)
                .Select(item => new ResourceFieldRow
                {
                    SlotId = item.SlotId ?? 0,
                    FieldType = item.FieldType,
                    Name = item.Name,
                    Level = item.Level,
                    Url = item.Url ?? string.Empty,
                    PendingTargetLevel = null,
                    IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
                })
                .ToList();

            SetResourceRows(rows);
            ApplyVillageStatusToUi(status);

            var orderedUpgrades = rows
                .Where(row => (row.Level ?? 0) < requestedTargetLevel)
                .OrderBy(row => row.Level ?? 0)
                .ThenBy(row => row.SlotId)
                .ToList();

            if (orderedUpgrades.Count == 0)
            {
                AppendLog($"[{operationId}] OK 0.0s | All resource fields are already at or above level {requestedTargetLevel}.");
                return;
            }

            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = requestedTargetLevel.ToString(),
            };

            var item = _botService.Enqueue("upgrade_all_resources_to_level", payload, priority: 0, maxRetries: 3);
            RequestQueueUiRefresh(selectId: item.Id);
            TriggerQueueAutoRunFromEnqueue();
            AppendLog($"[{operationId}] OK 0.0s | Queued upgrade-all toward level {requestedTargetLevel}. The worker will upgrade the lowest resource field first.");
        }
        catch (OperationCanceledException)
        {
            ClearPendingResourceLevelsFromUi();
            StatusTextBlock.Text = "Upgrade all resources paused.";
            AppendLog("Upgrade all resources paused.");
        }
        catch (Exception ex)
        {
            AppendLog($"[{operationId}] FAIL 0.0s | {FormatExceptionForLog(ex)}");
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
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
        if (!_isLoggedIn)
        {
            return;
        }

        if (ContinuousRunToggleButton.IsChecked != true)
        {
            if (_autoQueueRunning || _uiBusy)
            {
                _queueStopRequested = true;
                _operationCts?.Cancel();
                _autoQueueRunCts?.Cancel();
                AppendLog("Pause requested. Function cancellation sent.");
                return;
            }

            _queueStopRequested = false;
            ResumePausedQueueItems();
            _ = TriggerQueueAutoRunAsync();
            AppendLog("Function queue start requested.");
            return;
        }

        if (_autoQueueRunning)
        {
            _queueStopRequested = true;
            _autoQueueRunCts?.Cancel();
            _operationCts?.Cancel();
            AppendLog("Pause requested. Queue cancellation sent.");
            return;
        }

        if (_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            _operationCts?.Cancel();
            AppendLog("Pause requested. Function cancellation sent.");
            return;
        }

        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            _loopStopRequested = true;
            _loopCts?.Cancel();
            AppendLog("Pause requested. Loop cancellation sent.");
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
                        SetActiveAutomationTask(next.TaskName);
                        SetActiveFunctionExecution(string.IsNullOrWhiteSpace(next.DisplayName) ? next.TaskName : next.DisplayName);
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
                            else if (string.Equals(next.TaskName, "load_buildings_snapshot", StringComparison.OrdinalIgnoreCase))
                            {
                                await LoadBuildingsSnapshotIntoUiAsync(token);
                            }
                            AppendLog($"Queue item succeeded: {next.TaskName}");
                        }
                        catch (OperationCanceledException)
                        {
                            _botService.MarkQueueItemDeferred(next.Id, TimeSpan.Zero);
                            AppendLog($"Queue item paused: {next.TaskName}");
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
                            SetActiveAutomationTask(null);
                            SetActiveFunctionExecution(null);
                            RefreshQueueUiOnUiThread(next.Id);
                        }
                    }
                    else
                    {
                        SetActiveAutomationTask(null);
                        SetActiveFunctionExecution("Loop tasks");
                        tickOutcome = "fallback";
                        try
                        {
                            await _botService.ExecuteFallbackTasksAsync(options, AppendLog, token);
                        }
                        finally
                        {
                            SetActiveFunctionExecution(null);
                        }
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
        _queueStopRequested = true;
        _operationCts?.Cancel();
        _autoQueueRunCts?.Cancel();
        if (ContinuousRunToggleButton.IsChecked == true)
        {
            _loopStopRequested = true;
            _loopCts?.Cancel();
        }

        ClearPendingResourceLevelsFromUi();
        AppendLog("Stop requested. Cancellation sent to running actions.");
    }

    private void ContinuousRunToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_autoQueueRunning && !_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            return;
        }

        _queueStopRequested = true;
        _loopStopRequested = true;
        _operationCts?.Cancel();
        _autoQueueRunCts?.Cancel();
        _loopCts?.Cancel();
        ClearPendingResourceLevelsFromUi();
        SetLoopIndicator(false);
        StartLoopButton.Content = "Start bot";
        StartLoopButton.IsEnabled = true;
        AppendLog("Continuous run disabled. Running actions were stopped.");
    }

    private void SidebarNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            var targetTab = button.Tag?.ToString() switch
            {
                "dashboard" => DashboardTabItem,
                "resources" => ResourcesTabItem,
                "buildings" => BuildingsTabItem,
                "hero" => HeroTabItem,
                "farming" => FarmingTabItem,
                "queue" => QueueTabItem,
                "logs" => LogsTabItem,
                "inbox" => InboxTabItem,
                _ => DashboardTabItem,
            };

            if (targetTab is not null)
            {
                MainTabControl.SelectedItem = targetTab;
                if (ReferenceEquals(targetTab, FarmingTabItem))
                {
                    RefreshFarmListsItemsControl();
                    SyncFarmingControlsEnabledState();
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Sidebar navigation failed: {ex.Message}");
            MainTabControl.SelectedItem = DashboardTabItem;
        }

        UpdateSidebarSelection(button);
    }

    private void DashboardFunctionListButton_Click(object sender, RoutedEventArgs e)
    {
        var catalogByTask = AutomationLoopTaskCatalog
            .ToDictionary(item => item.TaskName, item => item, StringComparer.OrdinalIgnoreCase);
        var currentByTask = _automationLoopTasks
            .ToDictionary(item => item.TaskName, item => item, StringComparer.OrdinalIgnoreCase);

        var orderedTaskNames = new List<string>();
        orderedTaskNames.AddRange(_automationLoopTasks.Select(item => item.TaskName));
        orderedTaskNames.AddRange(AutomationLoopTaskCatalog.Select(item => item.TaskName));
        orderedTaskNames = orderedTaskNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var options = orderedTaskNames
            .Select(taskName => new DashboardFunctionOption
            {
                Key = taskName,
                Label = currentByTask.TryGetValue(taskName, out var current)
                    ? current.Title
                    : catalogByTask.TryGetValue(taskName, out var catalog)
                        ? catalog.Title
                        : HumanizeTaskName(taskName),
                IsVisible = currentByTask.TryGetValue(taskName, out var selected) && selected.IsEnabled,
            })
            .ToList();

        var dialog = new DashboardFunctionListWindow(options)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var selectedTaskNames = dialog.SelectedVisibility
            .Where(item => item.Value)
            .Select(item => item.Key)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _suppressAutomationLoopConfigWrite = true;
        try
        {
            _automationLoopTasks.Clear();
            foreach (var taskName in orderedTaskNames.Where(selectedTaskNames.Contains))
            {
                if (currentByTask.TryGetValue(taskName, out var existing))
                {
                    _automationLoopTasks.Add(new LoopTaskOption
                    {
                        TaskName = existing.TaskName,
                        Title = existing.Title,
                        Description = existing.Description,
                        IsEnabled = true,
                    });
                    continue;
                }

                if (catalogByTask.TryGetValue(taskName, out var catalog))
                {
                    _automationLoopTasks.Add(new LoopTaskOption
                    {
                        TaskName = catalog.TaskName,
                        Title = catalog.Title,
                        Description = catalog.Description,
                        IsEnabled = true,
                    });
                    continue;
                }

                _automationLoopTasks.Add(new LoopTaskOption
                {
                    TaskName = taskName,
                    Title = HumanizeTaskName(taskName),
                    Description = "Custom loop task from bot.json.",
                    IsEnabled = true,
                });
            }
        }
        finally
        {
            _suppressAutomationLoopConfigWrite = false;
        }

        UpdateAutomationLoopOrders();
        UpdateAutomationLoopSummaryText();
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void UpdateSidebarSelection(Button selectedButton)
    {
        var navButtons = new[]
        {
            DashboardNavButton,
            ResourcesNavButton,
            BuildingsNavButton,
            HeroNavButton,
            FarmingNavButton,
            QueueNavButton,
            LogsNavButton,
            InboxNavButton,
        };

        foreach (var nav in navButtons)
        {
            nav.BorderThickness = new Thickness(1);
            nav.BorderBrush = new SolidColorBrush(Color.FromRgb(243, 244, 246));
        }

        _activeSidebarButton = selectedButton;
        selectedButton.BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42));
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        AppDialog.Show(this,
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
            TriggerQueueAutoRunFromEnqueue();
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
            _loopStopRequested = true;
            _queueStopRequested = true;
            _operationCts?.Cancel();
            _autoQueueRunCts?.Cancel();
            _loopCts?.Cancel();
            _botService.ClearQueue();
            ClearPendingResourceLevelsFromUi();
            RefreshQueueUi();
            AppendLog("Queue cleared and running actions stopped.");
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

    private void QueuePopoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queuePopupWindow is not null)
        {
            _queuePopupWindow.Activate();
            return;
        }

        var activeGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            ItemsSource = QueueDataGrid.ItemsSource,
        };
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Task", Binding = new Binding("DisplayName"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Retries", Binding = new Binding("Retries"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Next try", Binding = new Binding("NextAttemptAtServer"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });

        var historyGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            BorderBrush = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            BorderThickness = new Thickness(1),
            ItemsSource = QueueHistoryDataGrid.ItemsSource,
        };
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Completed task", Binding = new Binding("DisplayName"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Created", Binding = new Binding("CreatedAtServer"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });

        var closeButton = new Button
        {
            Content = "Close",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(activeGrid);
        Grid.SetRow(historyGrid, 1);
        root.Children.Add(historyGrid);
        Grid.SetRow(closeButton, 2);
        root.Children.Add(closeButton);

        _queuePopupWindow = new Window
        {
            Title = "Queue",
            Width = 700,
            Height = 400,
            MinWidth = 580,
            MinHeight = 320,
            Content = root,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + Width + 10,
            Top = Top + 30,
        };
        closeButton.Click += (_, _) => _queuePopupWindow?.Close();
        _queuePopupWindow.Closed += (_, _) => _queuePopupWindow = null;
        _queuePopupWindow.Show();
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
            _villageSwitchCts?.Cancel();
            _queueAutoRunCts.Cancel();
            ClosePopupWindows();

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
            _villageSwitchCts?.Dispose();
            _villageSwitchCts = null;
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue on shutdown: {ex.Message}");
        }
    }

    private void ClosePopupWindows()
    {
        try
        {
            _logsPopupWindow?.Close();
            _queuePopupWindow?.Close();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not close popup windows: {ex.Message}");
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
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.TaskName : item.DisplayName,
                    TaskName = item.TaskName,
                    Priority = item.Priority,
                    Status = item.Status,
                    Retries = item.Retries,
                    MaxRetries = item.MaxRetries,
                    IsRuntimeOnly = item.IsRuntimeOnly,
                    CreatedAt = item.CreatedAt,
                    NextAttemptAtServer = FormatQueueServerTime(item.NextAttemptAt),
                    CreatedAtServer = FormatQueueServerTime(item.CreatedAt),
                })
                .ToList();
            var activeRows = rows
                .Where(row =>
                    row.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused
                    || (row.Status == QueueStatus.Failed && !row.IsRuntimeOnly))
                .ToList();
            var historyRows = rows
                .Where(row =>
                    row.Status == QueueStatus.Succeeded
                    || row.Status == QueueStatus.Canceled
                    || (row.Status == QueueStatus.Failed && row.IsRuntimeOnly))
                .ToList();
            var nowUtc = DateTimeOffset.UtcNow;
            var hasRunningQueueItems = ordered.Any(item => item.Status == QueueStatus.Running);
            var hasDeferredQueueItems = ordered.Any(item =>
                item.Status == QueueStatus.Pending &&
                item.NextAttemptAt > nowUtc);
            var hasPausedQueueItems = ordered.Any(item => item.Status == QueueStatus.Paused);
            var hasInlineWait = _inlineWaitUntilUtc > nowUtc;

            QueueDataGrid.ItemsSource = activeRows;
            QueueHistoryDataGrid.ItemsSource = historyRows;
            SyncPendingResourceTargetsInUi();

            if (selectId.HasValue)
            {
                var selected = activeRows.FirstOrDefault(item => item.Id == selectId.Value);
                if (selected is not null)
                {
                    QueueDataGrid.SelectedItem = selected;
                }
            }

            var offsetLabel = _queueServerTimeOffset.ToString(@"hh\:mm");
            var offsetPrefix = _queueServerTimeOffset < TimeSpan.Zero ? "-" : "+";
            var state = (hasDeferredQueueItems && !hasRunningQueueItems && !_uiBusy) || hasInlineWait
                ? "Waiting"
                : IsFunctionExecutionRunning(hasRunningQueueItems)
                    ? "Function running"
                    : (_loopTask is not null && !_loopTask.IsCompleted)
                        ? "Loop running"
                        : hasPausedQueueItems
                            ? "Paused"
                            : "Idle";
            QueueInfoTextBlock.Text = $"Queue active: {activeRows.Count} | Queue done: {historyRows.Count} | State: {state} | Server time offset: UTC{offsetPrefix}{offsetLabel}";
            if (_queuePopupWindow?.Content is Grid queuePopupRoot && queuePopupRoot.Children.Count >= 2)
            {
                if (queuePopupRoot.Children[0] is DataGrid popupActiveGrid)
                {
                    popupActiveGrid.ItemsSource = activeRows;
                }

                if (queuePopupRoot.Children[1] is DataGrid popupHistoryGrid)
                {
                    popupHistoryGrid.ItemsSource = historyRows;
                }
            }
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
        RequestQueueUiRefresh(selectId);
    }

    private void RequestQueueUiRefresh(Guid? selectId = null, bool immediate = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => RequestQueueUiRefresh(selectId, immediate));
            return;
        }

        if (selectId.HasValue)
        {
            _pendingQueueUiSelectId = selectId;
        }

        if (immediate)
        {
            _queueUiRefreshTimer.Stop();
            var immediateSelectId = _pendingQueueUiSelectId;
            _pendingQueueUiSelectId = null;
            RefreshQueueUi(immediateSelectId);
            return;
        }

        _queueUiRefreshTimer.Stop();
        _queueUiRefreshTimer.Start();
    }

    private async Task PromptAccountAnalysisIfNeededAsync(string accountName)
    {
        if (!_isLoggedIn)
        {
            return;
        }

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
            var result = AppDialog.Show(
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

    private async Task EnsureLoggedInForAnalysisAsync()
    {
        if (_isLoggedIn)
        {
            return;
        }

        LoginButton_Click(this, new RoutedEventArgs());
        var startedAt = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - startedAt).TotalSeconds < 45)
        {
            if (_isLoggedIn)
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException("Could not complete login before running account analysis.");
    }

    private bool IsAnalysisDone(string accountName)
    {
        try
        {
            return _accountAnalysisStore.IsAnalyzed(accountName, GetActiveAccountServerUrl());
        }
        catch
        {
            return false;
        }
    }

    private void LoadBuildingsButton_Click(object sender, RoutedEventArgs e)
    {
        EnqueueQuickTask("load_buildings_snapshot", "Load buildings snapshot");
        BuildingsInfoTextBlock.Text = "Queued buildings load.";
    }

    private void BuildingCategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_lastBuildingStatus is null)
        {
            return;
        }

        PopulateBuildingCatalogOptions(_lastBuildingStatus);
    }

    private void BuildingSlotCircleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastBuildingStatus is null)
        {
            BuildingsInfoTextBlock.Text = "Load buildings first.";
            return;
        }

        if (sender is not FrameworkElement { Tag: BuildingSlotRow row })
        {
            return;
        }

        if (row.IsOccupied)
        {
            QueueSingleBuildingUpgradeFromSlot(row.SlotId);
            return;
        }

        ConstructSlotTextBox.Text = row.SlotId.ToString();
        ShowConstructChoicesForSlot(row.SlotId, sender as FrameworkElement ?? this);
    }

    private void QueueSingleBuildingUpgradeFromSlot(int slotId)
    {
        var row = _buildingRows.FirstOrDefault(item => item.SlotId == slotId);
        if (row is null || !row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty. Choose a building to construct.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_buildingClickCooldownBySlot.TryGetValue(slotId, out var lastClickAt)
            && (now - lastClickAt).TotalMilliseconds < 120)
        {
            return;
        }

        _buildingClickCooldownBySlot[slotId] = now;
        var currentLevel = row.Level ?? 0;
        if (row.HasPendingUpgrade)
        {
            BuildingsInfoTextBlock.Text = $"{row.Name} already has a queued upgrade.";
            return;
        }

        var maxLevel = row.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        if (currentLevel >= maxLevel)
        {
            BuildingsInfoTextBlock.Text = $"{row.Name} in slot {slotId} is already max level ({maxLevel}).";
            return;
        }

        var pendingLevel = row.PendingTargetLevel ?? currentLevel;
        var baseLevel = Math.Max(currentLevel, pendingLevel);
        var targetLevel = Math.Clamp(baseLevel + 1, 1, maxLevel);

        if (_buildingLastQueuedTargetBySlot.TryGetValue(slotId, out var lastQueued)
            && lastQueued.Target == targetLevel
            && (now - lastQueued.At).TotalMilliseconds < 2500)
        {
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = targetLevel.ToString(),
        };
        EnqueueQuickTask("upgrade_building_to_level", $"Upgrade slot {slotId} to level {targetLevel}", payload);
        _buildingLastQueuedTargetBySlot[slotId] = (targetLevel, now);
        SetPendingBuildingUpgrade(slotId, targetLevel);
        UpgradeSlotTextBox.Text = slotId.ToString();
        UpgradeTargetLevelTextBox.Text = targetLevel.ToString();
        BuildingsInfoTextBlock.Text = $"Queued {row.Name} in slot {slotId} to level {targetLevel}.";
        AppendLog($"Queued single building upgrade: slot {slotId} -> level {targetLevel}.");
    }

    private void ShowConstructChoicesForSlot(int slotId, FrameworkElement anchor)
    {
        if (_lastBuildingStatus is null)
        {
            BuildingsInfoTextBlock.Text = "Load buildings first.";
            return;
        }

        var options = GetConstructableOptionsForSlot(slotId);
        if (options.Count == 0)
        {
            BuildingsInfoTextBlock.Text = $"No constructable buildings available for slot {slotId} right now.";
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
        };

        foreach (var option in options)
        {
            var optionCopy = option;
            var menuItem = new MenuItem
            {
                Header = optionCopy.DisplayLabel,
                ToolTip = optionCopy.Requirements == "-"
                    ? "No requirements."
                    : $"Requires: {optionCopy.Requirements}",
            };
            menuItem.Click += (_, _) => TryQueueConstructBuilding(slotId, optionCopy);
            menu.Items.Add(menuItem);
        }

        menu.IsOpen = true;
    }

    private IReadOnlyList<BuildingCatalogOption> GetConstructableOptionsForSlot(int slotId)
    {
        if (_lastBuildingStatus is null)
        {
            return [];
        }

        return _buildingCatalogOptions
            .Where(option => CanQueueConstructBuilding(slotId, option, out _))
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool CanQueueConstructBuilding(int slotId, BuildingCatalogOption selectedBuilding, out string reason)
    {
        reason = string.Empty;
        if (_lastBuildingStatus is null)
        {
            reason = "Load buildings first.";
            return false;
        }

        var occupied = _lastBuildingStatus.Buildings.FirstOrDefault(item => item.SlotId == slotId && (item.Level ?? 0) > 0);
        if (occupied is not null)
        {
            reason = $"Slot {slotId} is occupied by {occupied.Name} level {occupied.Level}.";
            return false;
        }

        var existingSameGidLevels = _lastBuildingStatus.Buildings
            .Where(item => item.Gid == selectedBuilding.Gid && (item.Level ?? 0) > 0)
            .Select(item => item.Level ?? 0)
            .ToList();
        var duplicateAllowed = selectedBuilding.Gid is 23 or 38 or 39;
        var wallGid = selectedBuilding.Gid is 31 or 32 or 33 or 42 or 43;
        if (selectedBuilding.Gid is 10 or 11)
        {
            if (existingSameGidLevels.Count > 0)
            {
                var currentHighest = existingSameGidLevels.Max();
                if (currentHighest < 40)
                {
                    reason = $"{selectedBuilding.Name} can only be duplicated after an existing one reaches level 40.";
                    return false;
                }
            }
        }
        else if (existingSameGidLevels.Count > 0 && !duplicateAllowed && !wallGid)
        {
            reason = $"{selectedBuilding.Name} already exists in this village.";
            return false;
        }

        var missing = MissingRequirements(_lastBuildingStatus, selectedBuilding.RequirementEntries);
        if (missing.Count > 0)
        {
            reason = $"Missing requirements: {string.Join(", ", missing.Select(item => $"{item.Name} {item.Level}+"))}";
            return false;
        }

        return true;
    }

    private bool TryQueueConstructBuilding(int slotId, BuildingCatalogOption selectedBuilding)
    {
        if (!CanQueueConstructBuilding(slotId, selectedBuilding, out var reason))
        {
            BuildingsInfoTextBlock.Text = reason;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (_buildingLastQueuedConstructBySlot.TryGetValue(slotId, out var lastQueued)
            && string.Equals(lastQueued.Name, selectedBuilding.Name, StringComparison.OrdinalIgnoreCase)
            && (now - lastQueued.At).TotalMilliseconds < 2500)
        {
            return false;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingConstructSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingConstructGid] = selectedBuilding.Gid.ToString(),
            [BotOptionPayloadKeys.BuildingConstructName] = selectedBuilding.Name,
        };
        EnqueueQuickTask("construct_building", $"Construct {selectedBuilding.Name} in slot {slotId}", payload);

        _buildingLastQueuedConstructBySlot[slotId] = (selectedBuilding.Name, now);
        SetPendingBuildingConstruct(slotId, selectedBuilding.Name);
        ConstructSlotTextBox.Text = slotId.ToString();
        ConstructBuildingComboBox.SelectedItem = _buildingCatalogOptions.FirstOrDefault(item => item.Gid == selectedBuilding.Gid);
        BuildingsInfoTextBlock.Text = $"Queued construct: {selectedBuilding.Name} in slot {slotId}.";
        AppendLog($"Queued building construct: slot {slotId} -> {selectedBuilding.Name}.");
        return true;
    }

    private void SetPendingBuildingUpgrade(int slotId, int targetLevel)
    {
        var index = -1;
        for (var i = 0; i < _buildingRows.Count; i++)
        {
            if (_buildingRows[i].SlotId == slotId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var row = _buildingRows[index];
        _buildingRows[index] = new BuildingSlotRow
        {
            SlotId = row.SlotId,
            Name = row.Name,
            Level = row.Level,
            Gid = row.Gid,
            Category = row.Category,
            Requirements = row.Requirements,
            PendingTargetLevel = targetLevel,
            PendingConstructName = string.Empty,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
        };
    }

    private void SetPendingBuildingConstruct(int slotId, string buildingName)
    {
        var index = -1;
        for (var i = 0; i < _buildingRows.Count; i++)
        {
            if (_buildingRows[i].SlotId == slotId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var row = _buildingRows[index];
        _buildingRows[index] = new BuildingSlotRow
        {
            SlotId = row.SlotId,
            Name = row.Name,
            Level = row.Level,
            Gid = row.Gid,
            Category = row.Category,
            Requirements = row.Requirements,
            PendingTargetLevel = row.PendingTargetLevel,
            PendingConstructName = buildingName,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
        };
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

        _ = TryQueueConstructBuilding(slotId, selectedBuilding);
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
        _buildingLastQueuedTargetBySlot[slotId] = (targetLevel, DateTimeOffset.UtcNow);
        SetPendingBuildingUpgrade(slotId, targetLevel);
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

    private void HeroPriorityMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HeroAttributePriorityItem item })
        {
            return;
        }

        var index = _heroAttributePriorityItems.IndexOf(item);
        if (index <= 0)
        {
            return;
        }

        _heroAttributePriorityItems.Move(index, index - 1);
        UpdateHeroPriorityOrders();
    }

    private void HeroPriorityMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HeroAttributePriorityItem item })
        {
            return;
        }

        var index = _heroAttributePriorityItems.IndexOf(item);
        if (index < 0 || index >= _heroAttributePriorityItems.Count - 1)
        {
            return;
        }

        _heroAttributePriorityItems.Move(index, index + 1);
        UpdateHeroPriorityOrders();
    }

    private async void RefreshHeroStatsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshHeroStatsButton.IsEnabled = false;
        var operationId = BeginOperation("Refresh hero stats");
        var operationSw = Stopwatch.StartNew();

        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, CancellationToken.None);
            ApplyHeroAttributeSnapshotToUi(snapshot);
            CompleteOperation(operationId, operationSw, $"Hero stats refreshed. Free points: {snapshot.FreePoints}.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            HeroAttributesStatusTextBlock.Text = $"Hero stats refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshHeroStatsButton.IsEnabled = true;
        }
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
            [BotOptionPayloadKeys.HeroAutoAssignPoints] = HeroAutoAssignPointsCheckBox.IsChecked == true ? "true" : "false",
            [BotOptionPayloadKeys.HeroStatPriority] = BuildHeroPriorityPayload(),
        };
        EnqueueQuickTask("hero_manage", "Run hero management task", payload);
        BuildingsInfoTextBlock.Text = "Queued hero management task.";
    }

    private async void SendHeroOnAdventureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_heroAdventureCts is { } running && !running.IsCancellationRequested)
        {
            running.Cancel();
            HeroAdventureStatusTextBlock.Text = "Stopping continuous adventures...";
            return;
        }

        var cts = new CancellationTokenSource();
        _heroAdventureCts = cts;
        var continuous = ContinuousAdventuresCheckBox.IsChecked == true;
        SendHeroOnAdventureButton.Content = continuous ? "Stop adventures" : "Sending...";
        SendHeroOnAdventureButton.IsEnabled = continuous;
        RefreshAdventuresButton.IsEnabled = false;
        var operationId = BeginOperation(continuous ? "Hero adventure (continuous)" : "Hero adventure");
        var operationSw = Stopwatch.StartNew();
        ToggleUiBusy(true);

        try
        {
            await EnsureChromiumInstalledAsync();
            var iteration = 0;
            var recoveryAttempts = 0;
            const int maxRecoveryAttempts = 4;

            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();
                iteration++;
                var options = ApplySelectedVillageToOptions(LoadBotOptions());
                StopHeroCountdown();
                _inlineWaitUntilUtc = DateTimeOffset.MinValue;
                ToggleUiBusy(true);
                UpdateExecutionStateIndicator();
                HeroAdventureStatusTextBlock.Text = continuous
                    ? $"Function running... (dispatching iteration {iteration})"
                    : "Function running... (dispatching adventure)";

                var result = await _botService.SendHeroOnAdventureAsync(options, AppendLog, cts.Token);

                var displayedCount = result.Dispatched
                    ? Math.Max(0, result.AdventureCount - 1)
                    : result.AdventureCount;
                HeroAdventureCountTextBlock.Text = displayedCount.ToString();
                AppendLog(result.Message);

                // Hero was dead and Revive was clicked: re-check status immediately.
                if (result.WasRevived)
                {
                    recoveryAttempts++;
                    if (recoveryAttempts > maxRecoveryAttempts)
                    {
                        HeroAdventureStatusTextBlock.Text = "Stopping: hero recovery did not complete after several attempts.";
                        break;
                    }

                    HeroAdventureStatusTextBlock.Text = "Hero revived. Re-checking status...";
                    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                    continue;
                }

                // Hero is on the way home: wait for ETA, then retry.
                if (result.IsOnTheWayHome)
                {
                    recoveryAttempts++;
                    if (recoveryAttempts > maxRecoveryAttempts)
                    {
                        HeroAdventureStatusTextBlock.Text = "Stopping: hero is still on the way after multiple checks.";
                        break;
                    }

                    var etaSeconds = (result.SecondsUntilReturn ?? 60) + 1;
                    StartHeroCountdown(etaSeconds, result.AdventureCount, "Hero on the way home");
                    BeginInlineWait(etaSeconds);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(etaSeconds), cts.Token);
                    }
                    finally
                    {
                        StopHeroCountdown();
                        EndInlineWait();
                    }
                    continue;
                }

                recoveryAttempts = 0;

                if (!result.Dispatched)
                {
                    HeroAdventureStatusTextBlock.Text = continuous
                        ? $"Stopping continuous adventures: {result.Message}"
                        : result.Message;
                    break;
                }

                var adventuresLeft = Math.Max(0, result.AdventureCount - 1);
                var waitSeconds = (result.SecondsUntilReturn ?? 0) + 1;
                if (waitSeconds < 1)
                {
                    waitSeconds = 1;
                }

                // Always start the countdown after a successful dispatch — gives the user
                // visual feedback even when not running continuously.
                StartHeroCountdown(waitSeconds, adventuresLeft, "Hero away");

                if (!continuous || adventuresLeft <= 0)
                {
                    if (continuous)
                    {
                        HeroAdventureStatusTextBlock.Text = "Continuous adventures complete: no adventures left.";
                    }
                    BeginInlineWait(waitSeconds);
                    break;
                }

                BeginInlineWait(waitSeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cts.Token);
                }
                finally
                {
                    StopHeroCountdown();
                    EndInlineWait();
                }
            }

            CompleteOperation(operationId, operationSw, "Hero adventures done.");
        }
        catch (OperationCanceledException)
        {
            HeroAdventureStatusTextBlock.Text = "Continuous adventures stopped.";
            AppendLog("Hero adventure run cancelled.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            HeroAdventureStatusTextBlock.Text = $"Hero adventure failed: {ex.Message}";
        }
        finally
        {
            // Don't stop the countdown here — leave the post-dispatch countdown visible until it
            // ticks down to 0 on its own. Tear down the inline wait only if we did not start one.
            _heroAdventureCts = null;
            SendHeroOnAdventureButton.Content = "Send hero on adventure";
            SendHeroOnAdventureButton.IsEnabled = true;
            RefreshAdventuresButton.IsEnabled = true;
            ToggleUiBusy(false);
            UpdateExecutionStateIndicator();
        }
    }

    private void BeginInlineWait(int seconds)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds));
        if (until > _inlineWaitUntilUtc)
        {
            _inlineWaitUntilUtc = until;
        }
        ToggleUiBusy(false);
        UpdateExecutionStateIndicator();
    }

    private void EndInlineWait()
    {
        _inlineWaitUntilUtc = DateTimeOffset.MinValue;
        UpdateExecutionStateIndicator();
    }

    private async void RefreshAdventuresButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAdventuresButton.IsEnabled = false;
        var operationId = BeginOperation("Refresh adventures");
        var operationSw = Stopwatch.StartNew();
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var count = await _botService.RefreshAdventureCountAsync(options, AppendLog, CancellationToken.None);
            if (count is null)
            {
                HeroAdventureCountTextBlock.Text = "?";
                HeroAdventureStatusTextBlock.Text = "Adventures not found on current page.";
            }
            else
            {
                HeroAdventureCountTextBlock.Text = count.Value.ToString();
                HeroAdventureStatusTextBlock.Text = $"Adventures available: {count.Value}.";
            }

            CompleteOperation(operationId, operationSw, $"Refresh adventures: {(count?.ToString() ?? "not found")}.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            HeroAdventureStatusTextBlock.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshAdventuresButton.IsEnabled = true;
        }
    }

    private string _heroCountdownLabel = "Hero away";

    private void StartHeroCountdown(int seconds, int adventuresLeft, string label)
    {
        StopHeroCountdown();
        _heroCountdownRemainingSeconds = Math.Max(0, seconds);
        _heroCountdownLabel = label;
        UpdateHeroCountdownText(adventuresLeft);

        _heroCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heroCountdownTimer.Tick += (_, _) =>
        {
            if (_heroCountdownRemainingSeconds > 0)
            {
                _heroCountdownRemainingSeconds -= 1;
            }
            UpdateHeroCountdownText(adventuresLeft);

            if (_heroCountdownRemainingSeconds <= 0)
            {
                StopHeroCountdown();
            }
        };
        _heroCountdownTimer.Start();
    }

    private void StopHeroCountdown()
    {
        if (_heroCountdownTimer is null)
        {
            return;
        }

        _heroCountdownTimer.Stop();
        _heroCountdownTimer = null;
    }

    private void UpdateHeroCountdownText(int adventuresLeft)
    {
        var formatted = FormatHeroDuration(_heroCountdownRemainingSeconds);
        HeroAdventureStatusTextBlock.Text =
            $"{_heroCountdownLabel}. Returns in {formatted}. Adventures left: {adventuresLeft}.";
    }

    private static string FormatHeroDuration(int seconds)
    {
        var clamped = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(clamped);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private string GetBuildingsSnapshotPathForActiveAccount()
    {
        var account = _accountStore.ActiveAccountName();
        var safeAccount = string.IsNullOrWhiteSpace(account) ? "main" : account.Trim().ToLowerInvariant();
        return Path.Combine(_projectRoot, "temp_build_out", "buildings-snapshots", $"{safeAccount}.json");
    }

    private async Task LoadBuildingsSnapshotIntoUiAsync(CancellationToken cancellationToken)
    {
        var snapshotPath = GetBuildingsSnapshotPathForActiveAccount();
        if (!File.Exists(snapshotPath))
        {
            AppendLog("Buildings snapshot not found.");
            return;
        }

        var json = await File.ReadAllTextAsync(snapshotPath, cancellationToken);
        var snapshot = JsonSerializer.Deserialize<BuildingSnapshotDto>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        if (snapshot is null)
        {
            AppendLog("Buildings snapshot could not be parsed.");
            return;
        }

        var status = new VillageStatus(
            ActiveVillage: snapshot.ActiveVillage ?? string.Empty,
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: (snapshot.Buildings ?? [])
                .Select(item => new Building(item.SlotId, item.Name ?? "Unknown", item.Level, item.Url, item.Gid))
                .ToList(),
            BuildQueue: [],
            Tribe: snapshot.Tribe ?? "Unknown",
            VillageCount: 0);

        _lastBuildingStatus = status;
        await Dispatcher.InvokeAsync(() =>
        {
            PopulateBuildingsTab(status);
            BuildingsInfoTextBlock.Text = $"Loaded {status.Buildings.Count} building slots from queue snapshot.";
        });
    }

    private sealed record BuildingSnapshotDto(
        string? Account,
        string? ActiveVillage,
        string? Tribe,
        List<BuildingSnapshotItemDto>? Buildings);

    private sealed record BuildingSnapshotItemDto(
        int? SlotId,
        string? Name,
        int? Level,
        string? Url,
        int? Gid);

    private void PopulateBuildingsTab(VillageStatus status)
    {
        _buildingRows.Clear();
        _demolishableBuildings.Clear();

        var categoryByGid = BuildingCatalogService.GetCatalogForTribe(status.Tribe)
            .ToDictionary(item => item.Gid, item => item, EqualityComparer<int>.Default);

        var buildingBySlot = status.Buildings
            .Where(item => item.SlotId is not null)
            .GroupBy(item => item.SlotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.Level ?? 0)
                    .First());

        var occupiedCount = 0;
        foreach (var slotId in Enumerable.Range(19, 22))
        {
            buildingBySlot.TryGetValue(slotId, out var building);
            var hasIdentifiedBuildingName = building is not null
                && !string.IsNullOrWhiteSpace(building.Name)
                && !string.Equals(building.Name, "Unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
                && !building.Name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase);
            var occupied = building is not null
                && ((building.Level ?? 0) > 0
                    || (building.Gid ?? 0) > 0
                    || hasIdentifiedBuildingName);

            var category = occupied ? "infrastructure" : "-";
            var requirements = occupied ? string.Empty : "-";
            if (occupied && building!.Gid is int gid && categoryByGid.TryGetValue(gid, out var catalog))
            {
                category = catalog.Category;
                requirements = catalog.Requirements.Count == 0
                    ? "-"
                    : string.Join(", ", catalog.Requirements.Select(item => $"{item.Name} {item.Level}+"));
            }

            int? pendingTarget = _buildingLastQueuedTargetBySlot.TryGetValue(slotId, out var lastTarget)
                ? lastTarget.Target
                : null;
            var pendingConstruct = _buildingLastQueuedConstructBySlot.TryGetValue(slotId, out var lastConstruct)
                ? lastConstruct.Name
                : string.Empty;

            if (!BuildingSlotLayoutById.TryGetValue(slotId, out var layout))
            {
                layout = (0d, 0d);
            }

            if (occupied)
            {
                occupiedCount += 1;
                if (pendingTarget is int queuedTarget && queuedTarget <= (building!.Level ?? 0))
                {
                    pendingTarget = null;
                }

                pendingConstruct = string.Empty;
            }
            else
            {
                pendingTarget = null;
            }

            var row = new BuildingSlotRow
            {
                SlotId = slotId,
                Name = occupied ? building!.Name : "Empty",
                Level = occupied ? building!.Level : null,
                Gid = occupied ? building!.Gid : null,
                Category = category,
                Requirements = requirements,
                PendingTargetLevel = pendingTarget,
                PendingConstructName = pendingConstruct,
                MapLeft = layout.Left,
                MapTop = layout.Top,
            };
            _buildingRows.Add(row);

            if (occupied)
            {
                _demolishableBuildings.Add(row);
            }
        }

        PopulateBuildingCatalogOptions(status);
        BuildingsInfoTextBlock.Text = $"Buildings loaded. Occupied slots: {occupiedCount}, free slots: {22 - occupiedCount}.";
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

    private static IReadOnlyDictionary<int, (double Left, double Top)> CreateBuildingSlotLayout()
    {
        const double canvasWidth = 760d;
        const double canvasHeight = 430d;
        const double slotCardWidth = 92d;
        const double centerX = (canvasWidth - slotCardWidth) / 2d;
        const double centerY = (canvasHeight - slotCardWidth) / 2d;
        const double radiusX = 300d;
        const double radiusY = 155d;

        var map = new Dictionary<int, (double Left, double Top)>();
        var slots = Enumerable.Range(19, 22).ToArray();
        for (var index = 0; index < slots.Length; index++)
        {
            var angle = (-Math.PI / 2d) + (2d * Math.PI * index / slots.Length);
            var left = centerX + (Math.Cos(angle) * radiusX);
            var top = centerY + (Math.Sin(angle) * radiusY);
            map[slots[index]] = (Math.Round(left, 1), Math.Round(top, 1));
        }

        return map;
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
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ToggleUiBusy(busy));
            return;
        }

        if (busy)
        {
            EnsureManualExecutionTracking();
        }

        _uiBusy = busy;
        try
        {
            var defaultEnabled = !busy;
            SetEnabled(AccountComboBox, defaultEnabled);
            SetEnabled(LoginButton, defaultEnabled);
            SetEnabled(LogoutButton, defaultEnabled);
            SetEnabled(SettingsButton, defaultEnabled);
            SetEnabled(QueueAddButton, defaultEnabled);
            SetEnabled(QueueRemoveButton, defaultEnabled);
            SetEnabled(QueueMoveUpButton, defaultEnabled);
            SetEnabled(QueueMoveDownButton, defaultEnabled);
            SetEnabled(QueueRetryButton, defaultEnabled);
            SetEnabled(QueueClearButton, defaultEnabled);
            SetEnabled(QueueRefreshButton, defaultEnabled);
            SetEnabled(ResetProgramButton, true);
            SetEnabled(LoadResourcesButton, defaultEnabled);
            SetEnabled(ResourceTargetLevelComboBox, defaultEnabled);
            SetEnabled(UpgradeAllResourcesButton, defaultEnabled);
            SetEnabled(MarkMessagesReadButton, defaultEnabled);
            SetEnabled(MarkReportsReadButton, defaultEnabled);
            SetEnabled(StopBotButton, true);

            if (StartLoopButton is not null)
            {
                StartLoopButton.IsEnabled = _isLoggedIn;
                StartLoopButton.Content = (busy || _autoQueueRunning || !string.IsNullOrWhiteSpace(_activeFunctionDisplayName) || (_loopTask is not null && !_loopTask.IsCompleted))
                    ? "Pause bot"
                    : "Start bot";
            }

            SyncFarmingControlsEnabledState();
        }
        catch (Exception ex)
        {
            AppendLog($"ToggleUiBusy warning: {ex.Message}");
        }
        finally
        {
            if (!busy)
            {
                CompleteManualExecutionTrackingIfNeeded();
            }
        }
    }

    private void EnsureManualExecutionTracking()
    {
        if (_activeManualExecution is not null)
        {
            return;
        }

        var operationId = _pendingManualOperationId;
        var operationName = operationId is not null && _operationNamesById.TryGetValue(operationId, out var knownName)
            ? knownName
            : "Manual function";

        operationId ??= $"manual-{Guid.NewGuid():N}";
        var taskName = BuildRuntimeManualTaskName(operationName);
        var queueItem = _botService.EnqueueRuntime(taskName, operationName, payload: null, priority: 0, maxRetries: 0);
        _botService.MarkQueueItemRunning(queueItem.Id);

        _activeManualExecution = new ManualExecutionState
        {
            OperationId = operationId,
            OperationName = operationName,
            QueueItemId = queueItem.Id,
            Outcome = ManualExecutionOutcome.None,
        };

        _pendingManualOperationId = null;
        SetActiveFunctionExecution(operationName);
        RefreshQueueUiOnUiThread(queueItem.Id);
    }

    private void CompleteManualExecutionTrackingIfNeeded()
    {
        if (_activeManualExecution is null)
        {
            return;
        }

        var execution = _activeManualExecution;
        try
        {
            switch (execution.Outcome)
            {
                case ManualExecutionOutcome.Succeeded:
                    _botService.MarkQueueItemSucceeded(execution.QueueItemId);
                    break;
                case ManualExecutionOutcome.Failed:
                    _botService.MarkQueueItemExecutionFailed(execution.QueueItemId);
                    break;
                default:
                    _botService.MarkQueueItemCanceled(execution.QueueItemId);
                    break;
            }
        }
        finally
        {
            _activeManualExecution = null;
            _operationNamesById.Remove(execution.OperationId);
            SetActiveFunctionExecution(null);
            RefreshQueueUiOnUiThread(execution.QueueItemId);
        }
    }

    private void SetManualExecutionOutcome(string operationId, ManualExecutionOutcome outcome)
    {
        if (_activeManualExecution is null)
        {
            return;
        }

        if (!string.Equals(_activeManualExecution.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeManualExecution.Outcome = outcome;
    }

    private static string BuildRuntimeManualTaskName(string operationName)
    {
        var normalized = Regex.Replace(operationName ?? string.Empty, "[^a-zA-Z0-9]+", "_")
            .Trim('_')
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "manual";
        }

        return $"{RuntimeManualTaskPrefix}:{normalized}";
    }

    private static void SetEnabled(UIElement? element, bool enabled)
    {
        if (element is not null)
        {
            element.IsEnabled = enabled;
        }
    }

    private void SyncFarmingControlsEnabledState()
    {
        var farmControlsEnabled = !_farmingOperationBusy && _farmingFeaturesAvailable;
        SetEnabled(AddFarmsToListButton, farmControlsEnabled);
        SetEnabled(CreateFarmListButton, farmControlsEnabled);
        SetEnabled(FarmListsItemsControl, farmControlsEnabled);
        SetEnabled(AnalyzeFarmListsButton, !_farmingOperationBusy);
        SetEnabled(AnalyzeNatarsProfileButton, !_farmingOperationBusy && _farmingFeaturesAvailable);
        SetEnabled(ShowNatarsListButton, !_farmingOperationBusy && _farmingFeaturesAvailable && _natarsProfileAnalyzed);
        SetEnabled(StartManualFarmingButton, _farmingFeaturesAvailable);
        UpdateManualFarmingRunningState();
    }

    private void RefreshFarmListsItemsControl()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke((Action)RefreshFarmListsItemsControl, DispatcherPriority.Render);
            return;
        }

        if (FarmListsItemsControl is null)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(FarmListsItemsControl.ItemsSource, _farmLists))
            {
                FarmListsItemsControl.ItemsSource = _farmLists;
            }

            var view = CollectionViewSource.GetDefaultView(FarmListsItemsControl.ItemsSource);
            view?.Refresh();
            FarmListsItemsControl.InvalidateMeasure();
            FarmListsItemsControl.InvalidateArrange();
            FarmListsItemsControl.InvalidateVisual();
            FarmListsItemsControl.UpdateLayout();
        }
        catch (Exception ex)
        {
            AppendLog($"Farm list UI refresh warning: {ex.Message}");
        }
    }

    private void SetFarmingOperationBusy(bool busy)
    {
        _farmingOperationBusy = busy;
        SyncFarmingControlsEnabledState();
    }

    private static BotOptions ApplyManualFarmingSelectionToOptions(BotOptions options, string natarVillageSelection)
    {
        return BotOptionsFactory.CloneWithOverrides(
            options,
            natarVillageSelectionOverride: natarVillageSelection);
    }

    private void UpdateManualFarmingRunningState()
    {
        if (StartManualFarmingButton is not null)
        {
            StartManualFarmingButton.Content = "Start manual farming";
            StartManualFarmingButton.IsEnabled = _farmingFeaturesAvailable && !_farmingOperationBusy;
        }

        if (ManualFarmingStateTextBlock is not null)
        {
            ManualFarmingStateTextBlock.Text = "State:";
            ManualFarmingStateTextBlock.Foreground = _farmingOperationBusy
                ? new SolidColorBrush(Color.FromRgb(22, 163, 74))
                : new SolidColorBrush(Color.FromRgb(107, 114, 128));
        }

        if (ManualFarmingStateDot is not null)
        {
            ManualFarmingStateDot.Fill = _farmingOperationBusy
                ? new SolidColorBrush(Color.FromRgb(22, 163, 74))
                : new SolidColorBrush(Color.FromRgb(156, 163, 175));
        }
    }

    private void UpdateManualFarmingExecutionCounter()
    {
        if (ManualFarmingExecutionCountTextBlock is null)
        {
            return;
        }

        ManualFarmingExecutionCountTextBlock.Text = _manualFarmSessionExecutionCount.ToString("N0");
    }

    private void SetNatarsProfileAnalyzed(bool analyzed)
    {
        _natarsProfileAnalyzed = analyzed;
        if (NatarsProfileAnalyzedIndicator is not null)
        {
            NatarsProfileAnalyzedIndicator.Fill = analyzed
                ? new SolidColorBrush(Color.FromRgb(22, 163, 74))
                : new SolidColorBrush(Color.FromRgb(156, 163, 175));
        }

        SetEnabled(ShowNatarsListButton, !_farmingOperationBusy && _farmingFeaturesAvailable && analyzed);
    }

    private void RefreshAccountPicker()
    {
        try
        {
            var accounts = _accountStore.ListAccounts()
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var active = _accountStore.ActiveAccountName();

            _suppressAccountSelectionChange = true;
            try
            {
                AccountComboBox.ItemsSource = accounts;
                var selected = accounts.FirstOrDefault(item =>
                                   string.Equals(item.Name, active, StringComparison.OrdinalIgnoreCase))
                               ?? accounts.FirstOrDefault();
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

    private void TriggerQueueAutoRunFromEnqueue()
    {
        _queueStopRequested = false;
        _ = TriggerQueueAutoRunAsync();
    }

    private void ResumePausedQueueItems()
    {
        try
        {
            var pausedItems = _botService
                .GetQueueItemsForDisplay()
                .Where(item => item.Status == QueueStatus.Paused)
                .ToList();

            foreach (var item in pausedItems)
            {
                _botService.ResumeQueueItem(item.Id);
            }

            if (pausedItems.Count > 0)
            {
                RefreshQueueUi();
                AppendLog($"Resumed {pausedItems.Count} paused queue item(s).");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not resume paused queue items: {ex.Message}");
        }
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
                var now = DateTimeOffset.UtcNow;
                var nextDeferredItem = _botService
                    .GetQueueItemsForDisplay()
                    .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                    .OrderBy(item => item.NextAttemptAt)
                    .FirstOrDefault();

                if (nextDeferredItem is null)
                {
                    AppendLog($"[AUTOQ {runId}] DONE (queue empty).");
                    return;
                }

                var waitDelay = nextDeferredItem.NextAttemptAt - now;
                if (waitDelay < TimeSpan.Zero)
                {
                    waitDelay = TimeSpan.Zero;
                }

                AppendLog($"[AUTOQ {runId}] WAIT {waitDelay.TotalSeconds:F0}s for deferred task={nextDeferredItem.TaskName}");
                try
                {
                    await Task.Delay(waitDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
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
                SetActiveAutomationTask(next.TaskName);
                SetActiveFunctionExecution(string.IsNullOrWhiteSpace(next.DisplayName) ? next.TaskName : next.DisplayName);
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
                else if (string.Equals(next.TaskName, "load_buildings_snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    await LoadBuildingsSnapshotIntoUiAsync(cancellationToken);
                }
                AppendLog($"[AUTOQ {runId}] OK {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName}");
            }
            catch (OperationCanceledException)
            {
                _botService.MarkQueueItemDeferred(next.Id, TimeSpan.Zero);
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
                SetActiveAutomationTask(null);
                SetActiveFunctionExecution(null);
                RefreshQueueUiOnUiThread(next.Id);
            }
        }
    }

    private void AppendLog(string message)
    {
        try
        {
            lock (_pendingLogSync)
            {
                _pendingLogMessages.Enqueue(message ?? string.Empty);
                if (_logFlushQueued)
                {
                    return;
                }

                _logFlushQueued = true;
            }

            _ = Dispatcher.BeginInvoke((Action)FlushPendingLogsToUi, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppendLog dispatch failed: {ex}");
        }
    }

    private void FlushPendingLogsToUi()
    {
        try
        {
            var messages = new List<string>(MaxLogLinesPerFlush);
            var linesForSessionLog = new List<string>(MaxLogLinesPerFlush * 2);
            var hasMore = false;
            lock (_pendingLogSync)
            {
                for (var i = 0; i < MaxLogLinesPerFlush && _pendingLogMessages.Count > 0; i++)
                {
                    messages.Add(_pendingLogMessages.Dequeue());
                }

                hasMore = _pendingLogMessages.Count > 0;
                _logFlushQueued = hasMore;
            }

            if (messages.Count <= 0)
            {
                return;
            }

            string? lastRawMessage = null;
            string? lastPrimaryPart = null;
            foreach (var message in messages)
            {
                lastRawMessage = message;
                var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
                var parts = normalized
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (parts.Length == 0)
                {
                    parts = [string.Empty];
                }

                if (parts.Length > 0)
                {
                    lastPrimaryPart = parts[0];
                }

                foreach (var part in parts)
                {
                    var line = $"[{GetServerNow():yyyy-MM-dd HH:mm:ss}] {part}";
                    _terminalEntries.Insert(0, line);
                    linesForSessionLog.Add(line);
                    TryApplyInlineResourceLevelUpdateFromLog(part);
                    if (TryExtractQueueWaitDelay(part, out var queueWaitDelay))
                    {
                        var waitUntilUtc = DateTimeOffset.UtcNow.Add(queueWaitDelay);
                        if (waitUntilUtc > _inlineWaitUntilUtc)
                        {
                            _inlineWaitUntilUtc = waitUntilUtc;
                        }
                    }

                    if (IsManualFarmingExecutionMessage(part))
                    {
                        _manualFarmSessionExecutionCount += 1;
                        UpdateManualFarmingExecutionCounter();
                    }

                    if (IsAlarmMessage(part))
                    {
                        _alarmEntries.Insert(0, line);
                        _unacknowledgedAlarmCount += 1;
                        linesForSessionLog.Add($"[{GetServerNow():yyyy-MM-dd HH:mm:ss}] [ALARM] {part}");
                    }

                    if (IsCaptchaSessionStartMessage(part) && !_captchaSessionActive)
                    {
                        _captchaSessionSeenCount += 1;
                        _captchaSessionActive = true;
                    }

                    if (IsManualVerificationAlarmMessage(part))
                    {
                        _manualVerificationAlarmActive = true;
                    }

                    if (_manualVerificationAlarmActive && IsManualVerificationResolvedMessage(part))
                    {
                        _unacknowledgedAlarmCount = 0;
                        _manualVerificationAlarmActive = false;
                    }

                    if (_captchaSessionActive && IsCaptchaSolvedAutomaticallyMessage(part))
                    {
                        _captchaSessionSolvedCount += 1;
                        _captchaSessionActive = false;
                    }
                    else if (_captchaSessionActive && IsManualVerificationResolvedMessage(part))
                    {
                        _captchaSessionActive = false;
                    }

                    if (part.Contains("manual verification appeared", StringComparison.OrdinalIgnoreCase)
                        || part.Contains("captcha/manual", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowManualVerificationPopup(_browserSessionLikelyOpen);
                    }
                }
            }

            TrimToMaxEntries(_terminalEntries, 1000);
            TrimToMaxEntries(_alarmEntries, 200);
            UpdateCaptchaStatsUi();
            TryAppendSessionLogLines(linesForSessionLog);

            if (lastRawMessage is not null)
            {
                StatusTextBlock.Text = lastRawMessage;
                StatusMiniLogTextBlock.Text = string.IsNullOrWhiteSpace(lastPrimaryPart)
                    ? lastRawMessage
                    : lastPrimaryPart;
            }

            UpdateTerminalAlarmUi();
            UpdateExecutionStateIndicator();

            if (hasMore)
            {
                _ = Dispatcher.BeginInvoke((Action)FlushPendingLogsToUi, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppendLog UI update failed: {ex}");
            lock (_pendingLogSync)
            {
                _logFlushQueued = _pendingLogMessages.Count > 0;
            }
        }
    }

    private void InitializeSessionLogFile()
    {
        try
        {
            var sessionLogDirectory = Path.GetDirectoryName(_sessionLogPath);
            if (!string.IsNullOrWhiteSpace(sessionLogDirectory))
            {
                Directory.CreateDirectory(sessionLogDirectory);
            }

            var header = new[]
            {
                "=== Tbot Ultra Session Log ===",
                $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"ProjectRoot: {_projectRoot}",
                string.Empty,
            };
            lock (_sessionLogWriteSync)
            {
                File.AppendAllLines(_sessionLogPath, header);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not initialize session log file: {ex}");
        }
    }

    private void TryAppendSessionLogLines(IReadOnlyList<string> lines)
    {
        if (lines.Count <= 0)
        {
            return;
        }

        try
        {
            lock (_sessionLogWriteSync)
            {
                File.AppendAllLines(_sessionLogPath, lines);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not append session logs: {ex}");
        }
    }

    private void TryApplyInlineResourceLevelUpdateFromLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var levelUp = Regex.Match(
            message,
            @"Resource slot\s+(?<slot>\d+)\s+level increased from\s+\d+\s+to\s+(?<to>\d+)",
            RegexOptions.IgnoreCase);
        if (!levelUp.Success)
        {
            return;
        }

        var slotId = int.Parse(levelUp.Groups["slot"].Value);
        var nextLevel = int.Parse(levelUp.Groups["to"].Value);
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
        var rows = sourceRows.ToList();
        var changed = false;
        var updatedRows = rows.Select(row =>
        {
            if (row.SlotId != slotId)
            {
                return row;
            }

            var existingLevel = row.Level ?? 0;
            if (existingLevel >= nextLevel)
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
                PendingTargetLevel = ResolveQueuedResourceTarget(row.SlotId, nextLevel, queuedTargetsBySlot),
                IsMaxLevel = nextLevel >= _activeVillageResourceMaxLevel || row.IsMaxLevel,
            };
        }).ToList();

        if (!changed)
        {
            return;
        }

        SetResourceRows(updatedRows);
        ResourcesInfoTextBlock.Text = $"Resource slot {slotId} updated to level {nextLevel}.";
    }

    private void ShowManualVerificationPopup(bool browserAlreadyOpen)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastVerificationPopupAt).TotalSeconds < 10)
        {
            return;
        }

        _lastVerificationPopupAt = now;
        if (browserAlreadyOpen)
        {
            var solved = AppDialog.Show(
                this,
                "Manual verification detected. Solve it in the open browser window, then click Yes (Solved).",
                "Manual verification",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (solved == MessageBoxResult.Yes)
            {
                _manualVerificationAlarmActive = false;
                _unacknowledgedAlarmCount = 0;
                AppendLog("Manual verification marked as solved by user.");
            }
            return;
        }

        var openBrowser = AppDialog.Show(
            this,
            "Manual verification is required. Browser is not open. Open/restart verification browser now?",
            "Manual verification",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (openBrowser == MessageBoxResult.Yes)
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

    private static bool IsManualVerificationAlarmMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains("manual verification appeared")
            || value.Contains("captcha/manual")
            || value.Contains("captcha/manual step detected")
            || value.Contains("solve it in the browser window")
            || value.Contains("captured captcha screenshot")
            || value.Contains("captcha auto-solve attempt")
            || value.Contains("captcha solver result");
    }

    private static bool IsManualVerificationResolvedMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains("manual verification cleared")
            || value.Contains("captcha cleared automatically")
            || value.Contains("login completed")
            || value.Contains("login finished");
    }

    private static bool IsCaptchaSolvedAutomaticallyMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("Captcha cleared automatically", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCaptchaSessionStartMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains("captured captcha screenshot")
            || value.Contains("manual verification appeared")
            || value.Contains("captcha/manual step detected");
    }

    private static bool IsAlarmMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        if (value.Contains(" started]"))
        {
            return false;
        }

        if (value.Contains("[completed]"))
        {
            return false;
        }

        if (value.Contains(" paused"))
        {
            return false;
        }

        if (value.Contains(" stopped"))
        {
            return false;
        }

        if (value.Contains(" canceled"))
        {
            return false;
        }

        if (value.Contains("] fail"))
        {
            return true;
        }

        if (value.Contains("alarm:"))
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
            || value.Contains("could not");
    }

    private static bool IsManualFarmingExecutionMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains(" sent raid to (")
            || value.Contains(" sent normal attack to (");
    }

    private void UpdateCaptchaStatsUi()
    {
        CaptchaStatsTextBlock.Text = $"Captchas solved: {_captchaSessionSolvedCount}/{_captchaSessionSeenCount} |";
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

        if (unreadMessages > 0)
        {
            InboxNavButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            InboxNavButton.Foreground = Brushes.White;
        }
        else
        {
            InboxNavButton.Background = new SolidColorBrush(Color.FromRgb(243, 244, 246));
            InboxNavButton.Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
        }
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
        var list = alertsTabSelected ? AlarmListBox : TerminalListBox;
        var selectedLines = list.SelectedItems.Cast<string>().ToList();
        var source = alertsTabSelected ? _alarmEntries.ToList() : _terminalEntries.ToList();
        var linesToCopy = selectedLines.Count > 0 ? selectedLines : source;
        if (linesToCopy.Count == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, linesToCopy));
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
        var activeList = alarmTabSelected ? AlarmListBox : TerminalListBox;
        var hasSelection = activeList.SelectedItems.Count > 0;
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

        if (hasSelection)
        {
            CopyCurrentTabButton.Content = "Copy selected";
        }
        else
        {
            CopyCurrentTabButton.Content = "Copy";
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // Keep current log selection when clicking log action buttons.
        if (IsDescendantOf(source, CopyCurrentTabButton)
            || IsDescendantOf(source, PopoutLogsButton)
            || IsDescendantOf(source, AcknowledgeAlarmButton)
            || IsDescendantOf(source, ClearCurrentLogButton))
        {
            return;
        }

        if (!IsDescendantOf(source, TerminalListBox))
        {
            TerminalListBox.UnselectAll();
        }

        if (!IsDescendantOf(source, AlarmListBox))
        {
            AlarmListBox.UnselectAll();
        }
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void LogListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        var item = GetListBoxItemAt(list, e.GetPosition(list));
        if (item is null)
        {
            return;
        }

        var index = list.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0 || index >= list.Items.Count)
        {
            return;
        }

        _logDragSelecting = true;
        _logDragSourceList = list;
        _logDragAnchorIndex = index;
        SelectListBoxRange(list, index, index);
        list.Focus();
        list.CaptureMouse();
    }

    private void LogListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_logDragSelecting || _logDragSourceList is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!ReferenceEquals(sender, _logDragSourceList))
        {
            return;
        }

        var mousePosition = e.GetPosition(_logDragSourceList);
        var item = GetListBoxItemAt(_logDragSourceList, mousePosition);
        int index;
        if (item is not null)
        {
            index = _logDragSourceList.ItemContainerGenerator.IndexFromContainer(item);
        }
        else if (_logDragSourceList.Items.Count > 0 && mousePosition.Y < 0)
        {
            index = 0;
        }
        else if (_logDragSourceList.Items.Count > 0 && mousePosition.Y > _logDragSourceList.ActualHeight)
        {
            index = _logDragSourceList.Items.Count - 1;
        }
        else
        {
            return;
        }

        if (index < 0 || index >= _logDragSourceList.Items.Count || _logDragAnchorIndex < 0)
        {
            return;
        }

        SelectListBoxRange(_logDragSourceList, _logDragAnchorIndex, index);
    }

    private void LogListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_logDragSelecting)
        {
            return;
        }

        _logDragSelecting = false;
        _logDragAnchorIndex = -1;
        if (_logDragSourceList is not null && _logDragSourceList.IsMouseCaptured)
        {
            _logDragSourceList.ReleaseMouseCapture();
        }

        _logDragSourceList = null;
        UpdateTerminalAlarmUi();
    }

    private static void SelectListBoxRange(ListBox list, int startIndex, int endIndex)
    {
        if (list.Items.Count == 0)
        {
            return;
        }

        var start = Math.Clamp(Math.Min(startIndex, endIndex), 0, list.Items.Count - 1);
        var end = Math.Clamp(Math.Max(startIndex, endIndex), 0, list.Items.Count - 1);
        list.SelectedItems.Clear();
        for (var i = start; i <= end; i++)
        {
            list.SelectedItems.Add(list.Items[i]);
        }

        list.ScrollIntoView(list.Items[end]);
    }

    private static ListBoxItem? GetListBoxItemAt(ListBox list, Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject;
        var direct = FindAncestor<ListBoxItem>(hit);
        if (direct is not null)
        {
            return direct;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
            {
                continue;
            }

            var topLeft = item.TranslatePoint(new Point(0, 0), list);
            var bounds = new Rect(topLeft, new Size(item.ActualWidth, item.ActualHeight));
            if (bounds.Contains(point))
            {
                return item;
            }
        }

        return null;
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
            SelectionMode = SelectionMode.Extended,
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
            SelectionMode = SelectionMode.Extended,
            ItemsSource = _alarmEntries,
        };
        _logsPopupLogList = popupLogList;
        _logsPopupAlarmList = popupAlarmList;
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
            var selected = popupTab.SelectedIndex == 1
                ? popupAlarmList.SelectedItems.Cast<string>().ToList()
                : popupLogList.SelectedItems.Cast<string>().ToList();
            var lines = selected.Count > 0
                ? selected
                : (popupTab.SelectedIndex == 1 ? _alarmEntries.ToList() : _terminalEntries.ToList());
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
        root.PreviewMouseDown += (_, args) =>
        {
            if (args.OriginalSource is not DependencyObject src)
            {
                return;
            }

            if (!IsDescendantOf(src, popupLogList))
            {
                popupLogList.UnselectAll();
            }

            if (!IsDescendantOf(src, popupAlarmList))
            {
                popupAlarmList.UnselectAll();
            }
        };

        _logsPopupWindow = new Window
        {
            Title = "Logs",
            Width = 700,
            Height = 400,
            MinWidth = 580,
            MinHeight = 320,
            Content = root,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + Width + 10,
            Top = Top + 30,
        };

        _logsPopupWindow.Closed += (_, _) =>
        {
            _logsPopupWindow = null;
            _logsPopupLogList = null;
            _logsPopupAlarmList = null;
        };
        closeButton.Click += (_, _) => _logsPopupWindow?.Close();
        _logsPopupWindow.Show();
    }

    private async Task EnsureChromiumInstalledAsync(bool forceInstall = false)
    {
        if (!forceInstall)
        {
            if (_chromiumEnsured || ChromiumAlreadyInstalled(_projectRoot))
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
            Arguments = $"-ExecutionPolicy Bypass -Command \"$env:PLAYWRIGHT_BROWSERS_PATH='{Path.Combine(_projectRoot, "ms-playwright")}'; & '{scriptPath}' install chromium\"",
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

    private static bool ChromiumAlreadyInstalled(string projectRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            var playwrightRoot = Path.Combine(projectRoot, "ms-playwright");
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
        await Dispatcher.InvokeAsync(() =>
        {
            var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
            var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
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
                    PendingTargetLevel = ResolveQueuedResourceTarget(item.SlotId ?? 0, item.Level ?? 0, queuedTargetsBySlot),
                    IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
                })
                .ToList();
            SetResourceRows(rows);
            ApplyVillageStatusToUi(status);
            var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
            ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
        });
    }

    private async Task LoadBuildingsAfterLoginAsync(BotOptions options, CancellationToken cancellationToken = default)
    {
        var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly: false);
        _lastBuildingStatus = status;
        PopulateBuildingsTab(status);
    }

    private async Task LoadCurrentVillageViewsAfterLoginAsync(BotOptions options, CancellationToken cancellationToken = default)
    {
        var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly: false, forceCurrentVillage: false);
        var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
        var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);

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
                PendingTargetLevel = ResolveQueuedResourceTarget(item.SlotId ?? 0, item.Level ?? 0, queuedTargetsBySlot),
                IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
            })
            .ToList();

        SetResourceRows(rows);
        ApplyVillageStatusToUi(status);

        _lastBuildingStatus = status;
        PopulateBuildingsTab(status);

        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
        BuildingsInfoTextBlock.Text = $"Buildings loaded for active village '{status.ActiveVillage}'. Occupied slots: {_buildingRows.Count(row => row.IsOccupied)}, free slots: {_buildingRows.Count(row => !row.IsOccupied)}.";

        TribeInfoTextBlock.Text = $"Tribe: {status.Tribe}";
        VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
        RefreshVillagePickerFromVillages(status.Villages, status.ActiveVillage);
        UpdateDashboardVillageList(status.Villages);

        await _botService.NavigateToVillageResourceFieldsAsync(
            options,
            AppendLog,
            GetSelectedVillageName(),
            GetSelectedVillageUrl(),
            cancellationToken);
        AppendLog("Returned to dorf1 after login scan.");
    }

    private async Task<VillageStatus> ReadVillageStatusWithRetryAsync(BotOptions options, CancellationToken cancellationToken, bool resourceOnly = false, bool forceCurrentVillage = false)
    {
        static bool IsTransientExecutionContextError(Exception ex)
        {
            var current = ex;
            while (current is not null)
            {
                var message = current.Message ?? string.Empty;
                if (message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("cannot find context with specified id", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                current = current.InnerException!;
            }

            return false;
        }

        VillageStatus status;
        var statusAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                status = await ReadVillageStatusAsync(options, cancellationToken, resourceOnly, forceCurrentVillage);
                break;
            }
            catch (Exception ex) when (attempt < statusAttempts && IsTransientExecutionContextError(ex))
            {
                AppendLog($"Village status read hit transient navigation context on attempt {attempt}/{statusAttempts}. Retrying...");
                await Task.Delay(250 * attempt, cancellationToken);
            }
        }

        var requiresRetry = status.ResourceFields.Count < 18
            || (!resourceOnly && status.Buildings.Count == 0);
        if (!requiresRetry)
        {
            return status;
        }

        if (status.ResourceFields.Count < 18)
        {
            AppendLog($"Resource scan returned {status.ResourceFields.Count} fields. Retrying once...");
        }

        if (!resourceOnly && status.Buildings.Count == 0)
        {
            AppendLog("Building scan returned 0 slots. Retrying once...");
        }

        await Task.Delay(350, cancellationToken);
        return await ReadVillageStatusAsync(options, cancellationToken, resourceOnly, forceCurrentVillage);
    }

    private Task<VillageStatus> ReadVillageStatusAsync(BotOptions options, CancellationToken cancellationToken, bool resourceOnly, bool forceCurrentVillage = false)
    {
        var villageName = forceCurrentVillage ? null : GetSelectedVillageName();
        var villageUrl = forceCurrentVillage ? null : GetSelectedVillageUrl();

        if (resourceOnly)
        {
            return _botService.ReadVillageResourceStatusAsync(
                options,
                AppendLog,
                villageName,
                villageUrl,
                cancellationToken);
        }

        return _botService.ReadVillageStatusAsync(
            options,
            AppendLog,
            villageName,
            villageUrl,
            cancellationToken);
    }

    private void SelectVillageInPicker(string? activeVillageName)
    {
        if (string.IsNullOrWhiteSpace(activeVillageName))
        {
            return;
        }

        if (VillageComboBox.ItemsSource is not IEnumerable<VillageSelectionItem> villages)
        {
            return;
        }

        var selected = villages.FirstOrDefault(v =>
            string.Equals(v.Name, activeVillageName, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return;
        }

        _suppressVillageSelectionChange = true;
        try
        {
            VillageComboBox.SelectedItem = selected;
        }
        finally
        {
            _suppressVillageSelectionChange = false;
        }
    }

    private void SetResourceRows(IReadOnlyList<ResourceFieldRow> rows)
    {
        ResourcesDataGrid.ItemsSource = rows.ToList();
        RepopulateResourceGroups(rows);
    }

    private IReadOnlyDictionary<int, int> GetQueuedResourceTargetsBySlot()
    {
        var targetsBySlot = new Dictionary<int, int>();
        IReadOnlyList<QueueItem> queueItems;
        try
        {
            queueItems = _botService.GetQueueItemsForDisplay();
        }
        catch
        {
            return targetsBySlot;
        }

        foreach (var item in queueItems)
        {
            if (!string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.Status is QueueStatus.Succeeded or QueueStatus.Failed)
            {
                continue;
            }

            if (!item.Payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeSlotId, out var slotRaw)
                || !int.TryParse(slotRaw, out var slotId))
            {
                continue;
            }

            if (!item.Payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeTargetLevel, out var targetRaw)
                || !int.TryParse(targetRaw, out var targetLevel))
            {
                continue;
            }

            if (targetLevel <= 0)
            {
                continue;
            }

            if (!targetsBySlot.TryGetValue(slotId, out var existing) || targetLevel > existing)
            {
                targetsBySlot[slotId] = targetLevel;
            }
        }

        return targetsBySlot;
    }

    private int? ResolveQueuedResourceTarget(int slotId, int currentLevel, IReadOnlyDictionary<int, int> queuedTargetsBySlot)
    {
        var hasQueuedTarget = queuedTargetsBySlot.TryGetValue(slotId, out var queuedTarget) && queuedTarget > 0;
        if (!hasQueuedTarget)
        {
            _resourcePendingTargetBySlot.Remove(slotId);
            return null;
        }

        var effectiveTarget = queuedTarget;
        var hasPendingTarget = _resourcePendingTargetBySlot.TryGetValue(slotId, out var rememberedTarget) && rememberedTarget > 0;
        if (hasPendingTarget && rememberedTarget > effectiveTarget)
        {
            effectiveTarget = rememberedTarget;
        }

        if (effectiveTarget <= currentLevel)
        {
            _resourcePendingTargetBySlot.Remove(slotId);
            return null;
        }

        _resourcePendingTargetBySlot[slotId] = effectiveTarget;
        return effectiveTarget;
    }

    private void SyncPendingResourceTargetsInUi()
    {
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
        var changed = false;
        var updatedRows = sourceRows
            .Select(row =>
            {
                var currentLevel = row.Level ?? 0;
                var pendingTarget = ResolveQueuedResourceTarget(row.SlotId, currentLevel, queuedTargetsBySlot);
                if (row.PendingTargetLevel == pendingTarget)
                {
                    return row;
                }

                changed = true;
                return new ResourceFieldRow
                {
                    SlotId = row.SlotId,
                    FieldType = row.FieldType,
                    Name = row.Name,
                    Level = row.Level,
                    Url = row.Url,
                    PendingTargetLevel = pendingTarget,
                    IsMaxLevel = row.IsMaxLevel,
                };
            })
            .ToList();

        if (!changed)
        {
            return;
        }

        SetResourceRows(updatedRows);
    }

    private void ClearPendingResourceLevelsFromUi()
    {
        _resourcePendingTargetBySlot.Clear();
        if (ResourcesDataGrid.ItemsSource is not IEnumerable<ResourceFieldRow> sourceRows)
        {
            return;
        }

        var updatedRows = sourceRows
            .Select(row => new ResourceFieldRow
            {
                SlotId = row.SlotId,
                FieldType = row.FieldType,
                Name = row.Name,
                Level = row.Level,
                Url = row.Url,
                PendingTargetLevel = null,
                IsMaxLevel = row.IsMaxLevel,
            })
            .ToList();

        SetResourceRows(updatedRows);
    }

    private void SetPendingResourceLevel(int slotId, int targetLevel)
    {
        var normalizedTarget = Math.Clamp(targetLevel, 1, _activeVillageResourceMaxLevel);
        if (_resourcePendingTargetBySlot.TryGetValue(slotId, out var existingTarget) && existingTarget > normalizedTarget)
        {
            normalizedTarget = existingTarget;
        }

        _resourcePendingTargetBySlot[slotId] = normalizedTarget;

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
                    PendingTargetLevel = normalizedTarget > (row.Level ?? 0) ? normalizedTarget : null,
                    IsMaxLevel = row.IsMaxLevel,
                }
                : row)
            .ToList();

        if (updated.FirstOrDefault(row => row.SlotId == slotId)?.PendingTargetLevel is null)
        {
            _resourcePendingTargetBySlot.Remove(slotId);
        }

        SetResourceRows(updated);
    }

    private void MarkResourceAsMax(int slotId)
    {
        _resourcePendingTargetBySlot.Remove(slotId);
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

        UpdateCroplandLayout();
    }

    private void UpdateCroplandLayout()
    {
        if (CroplandItemsControl is null)
        {
            return;
        }

        var isDenseCropland = _croplandFields.Count > 6;
        var columns = isDenseCropland ? 2 : 1;
        var factory = new FrameworkElementFactory(typeof(UniformGrid));
        factory.SetValue(UniformGrid.ColumnsProperty, columns);
        var template = new ItemsPanelTemplate(factory);
        template.Seal();
        CroplandItemsControl.ItemsPanel = template;

        if (CroplandColumnPanel is not null)
        {
            CroplandColumnPanel.Width = isDenseCropland ? 350 : 190;
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

        var liveRow = (ResourcesDataGrid.ItemsSource as IEnumerable<ResourceFieldRow>)
            ?.FirstOrDefault(item => item.SlotId == row.SlotId) ?? row;
        var currentLevel = liveRow.Level ?? 0;
        var rowName = string.IsNullOrWhiteSpace(liveRow.Name) ? row.Name : liveRow.Name;

        var now = DateTimeOffset.UtcNow;
        if (_resourceClickCooldownBySlot.TryGetValue(row.SlotId, out var lastClickAt)
            && (now - lastClickAt).TotalMilliseconds < 120)
        {
            return;
        }

        _resourceClickCooldownBySlot[row.SlotId] = now;

        if (liveRow.IsMaxLevel || currentLevel >= _activeVillageResourceMaxLevel)
        {
            MarkResourceAsMax(row.SlotId);
            AppDialog.Show(this, "Max level reached", "Resources", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pendingLevel = liveRow.PendingTargetLevel ?? currentLevel;
        var baseLevel = Math.Max(currentLevel, pendingLevel);
        var target = Math.Clamp(baseLevel + 1, 1, _activeVillageResourceMaxLevel);
        if (_resourceLastQueuedTargetBySlot.TryGetValue(row.SlotId, out var lastQueued)
            && lastQueued.Target == target
            && (now - lastQueued.At).TotalMilliseconds < 2500)
        {
            return;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.ResourceUpgradeSlotId] = row.SlotId.ToString(),
            [BotOptionPayloadKeys.ResourceUpgradeTargetLevel] = target.ToString(),
        };

        EnqueueQuickTask("upgrade_resource_to_level", $"Upgrade {rowName} to level {target}", payload);
        _resourceLastQueuedTargetBySlot[row.SlotId] = (target, now);
        SetPendingResourceLevel(row.SlotId, target);
        ResourcesInfoTextBlock.Text = $"Queued {rowName} to level {target}.";
        AppendLog($"Queued single resource upgrade: slot {row.SlotId} -> level {target}.");
    }

    private static bool IsResourceUpgradeTask(string taskName)
    {
        return string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
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

            var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
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
                    PendingTargetLevel = ResolveQueuedResourceTarget(row.SlotId, nextLevel, queuedTargetsBySlot),
                    IsMaxLevel = nextLevel >= _activeVillageResourceMaxLevel || row.IsMaxLevel || maxedSlots.Contains(row.SlotId),
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
                AppDialog.Show(this, "Max level reached", "Resources", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return true;
        });
    }

    private string BeginOperation(string operationName)
    {
        var id = System.Threading.Interlocked.Increment(ref _operationCounter);
        var operationId = $"OP{id:D4}";
        _operationNamesById[operationId] = operationName;
        _pendingManualOperationId = operationId;
        AppendLog($"[{operationId}] [{operationName} STARTED]");
        return operationId;
    }

    private void CompleteOperation(string operationId, Stopwatch sw, string summary)
    {
        SetManualExecutionOutcome(operationId, ManualExecutionOutcome.Succeeded);
        _operationNamesById.Remove(operationId);
        AppendLog($"[{operationId}] [COMPLETED] {sw.Elapsed.TotalSeconds:F1}s | {summary}");
    }

    private void FailOperation(string operationId, Stopwatch sw, Exception ex)
    {
        SetManualExecutionOutcome(operationId, ManualExecutionOutcome.Failed);
        _operationNamesById.Remove(operationId);
        AppendLog($"[{operationId}] FAIL {sw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            var firstFrames = string.Join(" | ", ex.StackTrace
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(4));
            if (!string.IsNullOrWhiteSpace(firstFrames))
            {
                AppendLog($"[{operationId}] STACK | {firstFrames}");
            }
        }

        if (ex.InnerException is not null)
        {
            AppendLog($"[{operationId}] INNER | {FormatExceptionForLog(ex.InnerException)}");
            if (!string.IsNullOrWhiteSpace(ex.InnerException.StackTrace))
            {
                var innerFrames = string.Join(" | ", ex.InnerException.StackTrace
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(3));
                if (!string.IsNullOrWhiteSpace(innerFrames))
                {
                    AppendLog($"[{operationId}] INNER STACK | {innerFrames}");
                }
            }
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

        var effectiveSeconds = Math.Max(0, seconds);
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
        UpdateAutomationLoopRunningIndicators();

        var loopRunning = _loopTask is not null && !_loopTask.IsCompleted;
        var hasPausedQueueItems = false;
        var hasRunningQueueItems = false;
        var hasDeferredQueueItems = false;
        DateTimeOffset? earliestNextAttemptUtc = null;
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var queueItems = _botService.GetQueueItemsForDisplay();
            hasPausedQueueItems = queueItems.Any(item => item.Status == QueueStatus.Paused);
            hasRunningQueueItems = queueItems.Any(item => item.Status == QueueStatus.Running);
            var deferredItems = queueItems
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > nowUtc)
                .ToList();
            hasDeferredQueueItems = deferredItems.Count > 0;
            if (hasDeferredQueueItems)
            {
                earliestNextAttemptUtc = deferredItems.Min(item => item.NextAttemptAt);
            }

            if (_inlineWaitUntilUtc <= nowUtc)
            {
                _inlineWaitUntilUtc = DateTimeOffset.MinValue;
            }
        }
        catch
        {
            // Ignore indicator read errors.
        }

        var nowForWait = DateTimeOffset.UtcNow;
        var hasInlineWait = _inlineWaitUntilUtc > nowForWait;
        var functionExecutionRunning = IsFunctionExecutionRunning(hasRunningQueueItems);
        if ((hasDeferredQueueItems && !hasRunningQueueItems && !_uiBusy && !functionExecutionRunning) || hasInlineWait)
        {
            int remainingSeconds;
            if (hasInlineWait)
            {
                remainingSeconds = (int)Math.Ceiling((_inlineWaitUntilUtc - nowForWait).TotalSeconds);
            }
            else
            {
                remainingSeconds = earliestNextAttemptUtc.HasValue
                    ? (int)Math.Ceiling((earliestNextAttemptUtc.Value - nowForWait).TotalSeconds)
                    : 0;
            }

            remainingSeconds = Math.Max(0, remainingSeconds);
            LoopStateTextBlock.Text = remainingSeconds > 0
                ? $"State: waiting ({FormatCountdown(remainingSeconds)})"
                : "State: waiting";
            LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(202, 138, 4));
            StartLoopButton.Content = (loopRunning || _autoQueueRunning) ? "Pause bot" : "Start bot";
            return;
        }

        if (functionExecutionRunning)
        {
            LoopStateTextBlock.Text = "State: function running";
            LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            StartLoopButton.Content = "Pause bot";
            return;
        }

        if (loopRunning)
        {
            LoopStateTextBlock.Text = "State: loop running";
            LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
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

    private static string? TryGetFriendlyLoginError(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        if (message.Length == 0)
        {
            return null;
        }

        var normalized = message.ToLowerInvariant();
        if (normalized.Contains("wrong password")
            || normalized.Contains("password is wrong")
            || normalized.Contains("incorrect password")
            || normalized.Contains("invalid password"))
        {
            return "Login failed: wrong password.";
        }

        if (normalized.Contains("username")
            && (normalized.Contains("not exist")
                || normalized.Contains("doesn't exist")
                || normalized.Contains("does not exist")
                || normalized.Contains("unknown")
                || normalized.Contains("not found")))
        {
            return "Login failed: username does not exist.";
        }

        if (normalized.Contains("invalid")
            && normalized.Contains("credential"))
        {
            return "Login failed: username does not exist or password is incorrect.";
        }

        return null;
    }

    private bool? TryGetStoredGoldClubEnabled(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return null;
        }

        try
        {
            if (_accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl()) && analysis is not null)
            {
                return analysis.GoldClubEnabled;
            }
        }
        catch
        {
            // Ignore temporary read errors for account info label.
        }

        return null;
    }

    private string? GetActiveAccountServerUrl()
    {
        try
        {
            var account = _accountProvider.LoadAccount();
            return account.ServerUrl;
        }
        catch
        {
            return null;
        }
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
        UpdateActiveVillageResourceMaxLevel(status);
        VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
        TribeInfoTextBlock.Text = $"Tribe: {status.Tribe}";
        LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}";
        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        var goldText = status.Gold?.ToString() ?? "-";
        var silverText = status.Silver?.ToString() ?? "-";
        ServerResourcesTextBlock.Text = $"Gold: {goldText} | Silver: {silverText}";

        _buildQueueActiveCount = status.ActiveBuildCount;
        _buildQueueRemainingSeconds = status.BuildQueueRemainingSeconds ?? -1;
        _buildQueueReachedZeroPendingCompletion = false;
        UpdateBuildQueueStatusText();
        RefreshVillagePicker(status);
    }

    private int ResolveResourceMaxLevelFromStatus(VillageStatus status)
    {
        if (status.IsCapital == true)
        {
            return ResourceFieldMaxLevel;
        }

        if (status.IsCapital == false)
        {
            return NonCapitalResourceMaxLevel;
        }

        return _activeVillageResourceMaxLevel;
    }

    private void UpdateActiveVillageResourceMaxLevel(VillageStatus status)
    {
        if (status.IsCapital == true)
        {
            _activeVillageResourceMaxLevel = ResourceFieldMaxLevel;
            return;
        }

        if (status.IsCapital == false)
        {
            _activeVillageResourceMaxLevel = NonCapitalResourceMaxLevel;
        }
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
                IsCapital = v.IsCapital == true,
                CoordX = v.CoordX,
                CoordY = v.CoordY,
                Population = v.Population,
                CropFields = v.CropFields,
            })
            .ToList();

        if (villages.Count == 0)
        {
            villages.Add(new VillageSelectionItem { Name = "-", Url = string.Empty });
        }

        _suppressVillageSelectionChange = true;
        try
        {
            VillageComboBox.ItemsSource = villages;
            var selected = villages.FirstOrDefault(v =>
                string.Equals(v.Name, currentSelectedName, StringComparison.OrdinalIgnoreCase))
                ?? villages.FirstOrDefault(v =>
                    string.Equals(v.Name, status.ActiveVillage, StringComparison.OrdinalIgnoreCase))
                ?? villages[0];
            VillageComboBox.SelectedItem = selected;
        }
        finally
        {
            _suppressVillageSelectionChange = false;
        }
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
            if (_buildQueueRemainingSeconds == 0)
            {
                _buildQueueReachedZeroPendingCompletion = true;
            }
        }

        if (_buildQueueRemainingSeconds == 0 && _buildQueueActiveCount > 0)
        {
            if (_buildQueueReachedZeroPendingCompletion)
            {
                _buildQueueReachedZeroPendingCompletion = false;
            }
            else
            {
                _buildQueueActiveCount = Math.Max(0, _buildQueueActiveCount - 1);
                if (_buildQueueActiveCount > 0)
                {
                    _buildQueueRemainingSeconds = -1;
                }
            }
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
        StartLoopButton.IsEnabled = isLoggedIn;

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
        if (_suppressVillageSelectionChange)
        {
            return;
        }

        if (VillageComboBox.SelectedItem is not VillageSelectionItem selected)
        {
            return;
        }

        StatusTextBlock.Text = $"Selected village: {selected.Name}";
        _ = SwitchToSelectedVillageAndRefreshAsync(selected);
    }

    private async Task SwitchToSelectedVillageAndRefreshAsync(VillageSelectionItem selectedVillage)
    {
        if (selectedVillage is null)
        {
            return;
        }

        if (IsExecutionActiveForVillageChange())
        {
            await StopAndClearForVillageChangeAsync(selectedVillage.Name);
        }

        if (_uiBusy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted))
        {
            AppendLog($"Village switch to '{selectedVillage.Name}' skipped because bot is still stopping.");
            return;
        }

        if (!_isLoggedIn || !_browserSessionLikelyOpen)
        {
            return;
        }

        _villageSwitchCts?.Cancel();
        _villageSwitchCts?.Dispose();
        _villageSwitchCts = new CancellationTokenSource();
        var operationToken = _villageSwitchCts.Token;
        var operationId = BeginOperation("SwitchVillage");
        var operationSw = Stopwatch.StartNew();
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] INFO switch village to '{selectedVillage.Name}'");

            // 1. Read resources + buildings, update resource/building tabs
            var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: false, forceCurrentVillage: false);

            var queuedTargetsBySlot = GetQueuedResourceTargetsBySlot();
            var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
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
                    PendingTargetLevel = ResolveQueuedResourceTarget(item.SlotId ?? 0, item.Level ?? 0, queuedTargetsBySlot),
                    IsMaxLevel = (item.Level ?? 0) >= resourceMaxLevel,
                })
                .ToList();

            SetResourceRows(rows);
            ApplyVillageStatusToUi(status);
            _lastBuildingStatus = status;
            PopulateBuildingsTab(status);

            var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
            ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
            BuildingsInfoTextBlock.Text = $"Buildings loaded for selected village '{selectedVillage.Name}'. Occupied slots: {_buildingRows.Count(row => row.IsOccupied)}, free slots: {_buildingRows.Count(row => !row.IsOccupied)}.";

            // 2. Analyze profile (spieler.php) to refresh capital flags and village metadata
            IReadOnlyList<Village> villagesForDashboard = status.Villages;
            try
            {
                var snapshot = await _botService.AnalyzeProfileAsync(options, AppendLog, operationToken);
                TribeInfoTextBlock.Text = $"Tribe: {snapshot.Tribe}";
                VillagesInfoTextBlock.Text = $"Villages: {snapshot.VillageCount}";
                villagesForDashboard = snapshot.Villages;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendLog($"[{operationId}] WARN profile analysis failed: {ex.Message}");
            }

            // 3. Update village dropdown and dashboard list from the same profile-backed data
            RefreshVillagePickerFromVillages(villagesForDashboard, selectedVillage.Name);
            UpdateDashboardVillageList(villagesForDashboard);

            // 4. Navigate back to dorf1
            try
            {
                await _botService.NavigateToVillageResourceFieldsAsync(options, AppendLog, selectedVillage.Name, selectedVillage.Url, operationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendLog($"[{operationId}] WARN could not navigate back to dorf1: {ex.Message}");
            }

            CompleteOperation(operationId, operationSw, $"Village switched to '{selectedVillage.Name}' and UI refreshed.");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{operationId}] INFO canceled.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
        }
    }

    private void UpdateDashboardVillageList(IReadOnlyList<Village> villages)
    {
        var items = villages
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .OrderByDescending(v => v.IsCapital == true)
            .ThenByDescending(v => v.Population ?? -1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new VillageSelectionItem
            {
                Name = v.Name,
                Url = v.Url ?? string.Empty,
                IsCapital = v.IsCapital == true,
                CoordX = v.CoordX,
                CoordY = v.CoordY,
                Population = v.Population,
                CropFields = v.CropFields,
            })
            .ToList();
        DashboardVillageList.ItemsSource = items;
    }

    private void RefreshVillagePickerFromVillages(IReadOnlyList<Village> villages, string? preferredVillageName)
    {
        var currentSelectedName = string.IsNullOrWhiteSpace(preferredVillageName)
            ? GetSelectedVillageName()
            : preferredVillageName;

        var items = villages
            .Select(v => new VillageSelectionItem
            {
                Name = string.IsNullOrWhiteSpace(v.Name) ? "-" : v.Name,
                Url = v.Url ?? string.Empty,
                IsCapital = v.IsCapital == true,
                CoordX = v.CoordX,
                CoordY = v.CoordY,
                Population = v.Population,
                CropFields = v.CropFields,
            })
            .ToList();

        if (items.Count == 0)
        {
            items.Add(new VillageSelectionItem { Name = "-", Url = string.Empty });
        }

        _suppressVillageSelectionChange = true;
        try
        {
            VillageComboBox.ItemsSource = items;
            var selected = items.FirstOrDefault(v =>
                string.Equals(v.Name, currentSelectedName, StringComparison.OrdinalIgnoreCase))
                ?? items[0];
            VillageComboBox.SelectedItem = selected;
        }
        finally
        {
            _suppressVillageSelectionChange = false;
        }
    }

    private bool IsExecutionActiveForVillageChange()
    {
        return _uiBusy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted);
    }

    private async Task StopAndClearForVillageChangeAsync(string? villageName)
    {
        var label = string.IsNullOrWhiteSpace(villageName) ? "-" : villageName;
        AppendLog($"Village changed to '{label}' while bot is running. Stopping active work and clearing queue.");

        _loopStopRequested = true;
        _queueStopRequested = true;
        _operationCts?.Cancel();
        _autoQueueRunCts?.Cancel();
        _loopCts?.Cancel();
        _villageSwitchCts?.Cancel();

        var stopDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < stopDeadline)
        {
            if (!_uiBusy && !_autoQueueRunning && (_loopTask is null || _loopTask.IsCompleted))
            {
                break;
            }

            await Task.Delay(120);
        }

        try
        {
            _botService.ClearQueue();
            RefreshQueueUi();
            AppendLog($"Queue cleared due to village change to '{label}'.");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue after village change: {ex.Message}");
        }
    }

    private void HandleBrowserClosedSignal()
    {
        if (!_botService.ConsumeBrowserClosedByUserSignal())
        {
            return;
        }

        _browserSessionLikelyOpen = false;

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastVerificationPopupAt).TotalSeconds < 5)
        {
            return;
        }

        _lastVerificationPopupAt = now;
        var result = AppDialog.Show(
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
            _isLoggedIn = true;
            _browserSessionLikelyOpen = true;
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


