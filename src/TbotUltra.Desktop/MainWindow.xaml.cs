using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Logging;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Worker;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow : Window
{
    private const string ContinuousLoopGroupOrderConfigKey = "continuous_loop_group_order";
    private const string DashboardVisibleGroupsConfigKey = "dashboard_visible_groups";
    private const int ResourceFieldMaxLevel = 40;
    private const int NonCapitalResourceMaxLevel = 10;
    private const int MaxFarmListsShown = 120;
    private const int MaxLogLinesPerFlush = 220;
    private const int MaxSessionLogFiles = 5;
    private const int ContinuousLoopMaxSleepSliceSeconds = 1;
    private const int ContinuousInboxCheckIntervalSeconds = 15;
    private const int ContinuousKeepAliveMinIntervalSeconds = 60;
    private const int ContinuousKeepAliveMaxIntervalSeconds = 600;
    private const int ContinuousKeepAliveDueSoonSeconds = 120;
    private const int NpcTradeGoldCost = 3;
    private const string RuntimeManualTaskPrefix = "desktop_runtime_manual";

    private sealed class ManualExecutionState
    {
        public string OperationId { get; init; } = string.Empty;
        public string OperationName { get; init; } = string.Empty;
        public Guid QueueItemId { get; init; }
        public ManualExecutionOutcome Outcome { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private sealed class NatarListRow : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _isUiSelected;

        public int Index { get; init; }
        public string VillageName { get; init; } = string.Empty;
        public int X { get; init; }
        public int Y { get; init; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public bool IsUiSelected
        {
            get => _isUiSelected;
            set
            {
                if (_isUiSelected == value)
                {
                    return;
                }

                _isUiSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUiSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record UiSyncVillagePayload(
        string? Name,
        string? Url,
        bool? IsCapital,
        int? CoordX,
        int? CoordY,
        int? Population,
        int? CropFields);

    private sealed record UiSyncPayload(
        int? Gold,
        int? Silver,
        string? ActiveVillage,
        IReadOnlyList<UiSyncVillagePayload>? Villages);

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
    private readonly string _serverCatalogPath;
    private readonly string _sessionLogPath;
    private readonly BotConfigStore _botConfigStore;
    private readonly VillageSettingsStore _villageSettingsStore;
    private readonly TravcoListStore _travcoListStore;
    private readonly VillageCacheStore _villageCacheStore;
    private readonly IAccountProvider _accountProvider;
    private readonly EnvAccountStore _accountStore;
    private readonly AccountAnalysisStore _accountAnalysisStore;
    private readonly HeroAttributeSnapshotStore _heroAttributeSnapshotStore;
    private readonly NatarFarmCacheStore _natarFarmCacheStore;
    private readonly ManualFarmingPreferenceStore _manualFarmingPreferenceStore;
    private readonly AccountDeletionService _accountDeletionService;
    private readonly ServerDiscoveryService _serverDiscoveryService;
    private readonly ServerCatalogStore _serverCatalogStore;
    private readonly IDesktopBotService _botService;
    private readonly ICaptchaAutoSolver _captchaAutoSolver;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly DispatcherTimer _inboxRefreshTimer;
    private readonly DispatcherTimer _buildQueueCountdownTimer;
    private readonly DispatcherTimer _resourceSnapshotRefreshTimer;
    private readonly DispatcherTimer _troopTrainingDeferredRefreshDebounceTimer;
    private readonly ObservableCollection<TerminalEntryRow> _terminalEntries = [];
    private ICollectionView? _terminalView;
    private ICollectionView? _alarmView;
    private LogCategory _terminalFilterCategory = LogCategory.All;
    private bool _terminalCleanMode = true;
    private readonly ObservableCollection<AlarmEntryRow> _alarmEntries = [];
    private readonly ObservableCollection<TravianBuildQueueRow> _travianBuildQueueRows = [];
    private readonly ObservableCollection<TravianSmithyQueueRow> _travianSmithyQueueRows = [];
    private readonly ObservableCollection<LoopTaskOption> _automationLoopTasks = [];
    private ICollectionView? _automationLoopTasksView;
    private readonly ObservableCollection<ResourceTransferVillageItem> _resourceTransferVillages = [];
    private bool _suppressResourceTransferConfigWrite;
    private readonly ObservableCollection<ReinforcementVillageItem> _reinforcementVillages = [];
    private readonly ObservableCollection<ReinforcementVillageItem> _reinforcementSourceVillages = [];
    private readonly ObservableCollection<ReinforcementTroopRuleItem> _reinforcementTroopRules = [];
    private List<ReinforcementTroopRule> _configuredReinforcementTroopRules = [];
    private bool _suppressReinforcementConfigWrite;
    private readonly ObservableCollection<FarmListStatusRow> _farmLists = [];
    // Building slots now live on BuildingsViewModel; this delegates so existing
    // code-behind that mutates _buildingRows in place keeps working unchanged.
    private ObservableCollection<BuildingSlotRow> _buildingRows => _buildingsViewModel.BuildingSlots;
    // These collections also live on BuildingsViewModel now; delegate so existing
    // code-behind that mutates them in place keeps working unchanged.
    private ObservableCollection<BuildingCatalogOption> _buildingCatalogOptions => _buildingsViewModel.BuildingCatalogOptions;
    private ObservableCollection<BuildingSlotRow> _demolishableBuildings => _buildingsViewModel.DemolishableBuildings;
    private readonly Dictionary<int, DateTimeOffset> _resourceClickCooldownBySlot = new();
    private readonly Dictionary<int, (int Target, DateTimeOffset At)> _resourceLastQueuedTargetBySlot = new();
    private FunctionTestWindow? _resourceTestFunctionsWindow;
    private SavePageHtmlWindow? _savePageHtmlWindow;
    private BulkSavePageHtmlWindow? _bulkSavePageHtmlWindow;
    private bool _serverSpeedAlarmRaised;
    private readonly Dictionary<int, DateTimeOffset> _buildingClickCooldownBySlot = new();
    private readonly Dictionary<int, (int Target, DateTimeOffset At)> _buildingLastQueuedTargetBySlot = new();
    private readonly Dictionary<int, (string Name, int Gid, DateTimeOffset At)> _buildingLastQueuedConstructBySlot = new();
    private readonly HashSet<int> _buildingDemolishingSlots = new();
    private static readonly IReadOnlyDictionary<int, (double Left, double Top)> BuildingSlotLayoutById = BuildingsViewModel.CreateBuildingSlotLayout();

    private bool _suppressHeroHideModeApply;
    private Task? _loopTask;
    private bool _chromiumEnsured;
    private bool _suppressAccountSelectionChange;
    private bool _suppressVillageSelectionChange;
    private bool _resourceSnapshotRefreshRunning;
    private bool _resourceProductionRefreshRunning;
    private bool _resourceProductionRefreshPending;
    private bool _resourceTransferScanRunning;
    private bool _deferredConstructionRefreshRunning;
    private bool _deferredTroopTrainingRefreshRunning;
    private VillageStatus? _pendingDeferredTroopTrainingRefreshStatus;
    private string? _pendingDeferredTroopTrainingRefreshSource;
    private TimeSpan _queueServerTimeOffset;
    private long _operationCounter;
    private long _loopTickCounter;
    private readonly LoopController _loopController;
    private readonly BackgroundTaskTracker _backgroundTasks = new();
    private readonly SessionPacer _sessionPacer = new();

    // Initialized in field initializers (i.e. before InitializeComponent runs)
    // so XAML bindings such as {Binding HeroVm, ElementName=RootWindow} resolve
    // against a real instance the very first time WPF evaluates them. Setting
    // them in the constructor body after InitializeComponent leaves bindings
    // pointing at null permanently because the public properties have no
    // PropertyChanged event to refresh them when the field finally gets a value.
    private readonly HeroViewModel _heroViewModel = App.Services.GetRequiredService<HeroViewModel>();
    private readonly InboxViewModel _inboxViewModel = App.Services.GetRequiredService<InboxViewModel>();
    private readonly TroopTrainingViewModel _troopTrainingViewModel = App.Services.GetRequiredService<TroopTrainingViewModel>();
    private readonly ResourcesViewModel _resourcesViewModel = App.Services.GetRequiredService<ResourcesViewModel>();
    private readonly BuildingsViewModel _buildingsViewModel = App.Services.GetRequiredService<BuildingsViewModel>();

    /// <summary>
    /// Public accessor so XAML can bind to the hero view model via
    /// <c>{Binding HeroVm.X, ElementName=RootWindow}</c> or by setting
    /// DataContext on a panel container.
    /// </summary>
    public HeroViewModel HeroVm => _heroViewModel;

    /// <summary>
    /// Public accessor for the inbox view model. The InboxTabItem inherits
    /// this as DataContext; the sidebar nav button binds individual
    /// properties via <c>ElementName=RootWindow</c>.
    /// </summary>
    public InboxViewModel InboxVm => _inboxViewModel;

    /// <summary>
    /// Public accessor for the troop-training view model. The TroopsTabItem
    /// inherits this as DataContext.
    /// </summary>
    public TroopTrainingViewModel TroopTrainingVm => _troopTrainingViewModel;

    /// <summary>
    /// Public accessor for the resources view model. The ResourcesTabItem
    /// inherits this as DataContext.
    /// </summary>
    public ResourcesViewModel ResourcesVm => _resourcesViewModel;
    private readonly SemaphoreSlim _inboxRefreshGate = new(1, 1);
    private readonly DispatcherTimer _queueUiRefreshTimer;
    // UI-thread micro-snapshot of the queue (see GetQueueSnapshotForUi): coalesces the per-tick burst of
    // display reads into one disk read.
    private IReadOnlyList<QueueItem>? _uiQueueSnapshot;
    private DateTimeOffset _uiQueueSnapshotAtUtc;
    private Window? _logsPopupWindow;
    private Window? _queuePopupWindow;
    private ListBox? _logsPopupLogList;
    private ListBox? _logsPopupAlarmList;
    private Button? _activeSidebarButton;
    private Guid? _pendingQueueUiSelectId;
    private volatile bool _autoQueueRunning;
    private volatile bool _uiBusy;
    private volatile bool _inboxAutoEnabled;
    private bool _loginInProgress;
    private DateTimeOffset _lastContinuousInboxCheckUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastContinuousBrowserActivityUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextContinuousKeepAliveAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastContinuousKeepAliveFailureUtc = DateTimeOffset.MinValue;
    private string? _lastConservativeAutomationWarningSignature;
    private volatile bool _isLoggedIn;
    private volatile bool _browserSessionLikelyOpen;
    // True only while a visible (non-headless) login is running. Lets the captcha/manual-verification
    // popup know the browser window is already open WITHOUT enabling background/village-selection ops
    // (which gate on _browserSessionLikelyOpen) to race the login on the shared page.
    private volatile bool _visibleBrowserLoginInProgress;
    // Guards the account-switch flow (logout → close → reopen + auto-login) against overlapping runs so
    // rapid picker changes can't spawn concurrent flows fighting over the shared browser.
    private bool _accountSwitchInProgress;
    private bool _farmingFeaturesAvailable = true;
    private int _activeVillageResourceMaxLevel = NonCapitalResourceMaxLevel;
    private int _buildQueueRemainingSeconds = -1;
    private int _buildQueueActiveCount;
    private bool? _travianPlusActive;
    private List<int> _smithyUpgradeRemainingSeconds = [];
    private bool _smithyUpgradeStatusRefreshRunning;
    private IReadOnlyList<Building>? _pendingSmithyUpgradeStatusBuildings;
    private bool _buildQueueReachedZeroPendingCompletion;
    private int _unacknowledgedAlarmCount;
    private bool _manualVerificationAlarmActive;
    private int _captchaSessionSeenCount;
    private int _captchaSessionSolvedCount;
    private bool _captchaSessionActive;
    private int _npcTradeSessionCount;
    private int _npcTradeTroopSessionCount;
    private int _npcTradeBuildingSessionCount;
    private AppDialog? _captchaAutoSolvePopup;
    private DispatcherTimer? _captchaAutoSolveElapsedTimer;
    private DateTimeOffset _captchaAutoSolveStartedAt;
    private int _captchaAutoSolveMaxSeconds = 60;
    private TextBlock? _captchaAutoSolveAttemptTextBlock;
    private TextBlock? _captchaAutoSolveElapsedTextBlock;
    private DateTimeOffset _lastVerificationPopupAt = DateTimeOffset.MinValue;
    private DateTimeOffset _inlineWaitUntilUtc = DateTimeOffset.MinValue;
    private int _manualFarmSessionExecutionCount;
    private string? _activeAutomationTaskName;
    private string? _activeFunctionDisplayName;
    private string? _troopsBlockedReasonKey;
    private string? _troopsBlockedReasonText;
    private bool _troopsBlockedPreviouslyEnabled;
    private string? _farmingBlockedReasonKey;
    private string? _farmingBlockedReasonText;
    private bool _farmingBlockedPreviouslyEnabled;
    private string? _heroBlockedReasonKey;
    private string? _heroBlockedReasonText;
    private bool _heroBlockedPreviouslyEnabled;
    private string? _breweryBlockedReasonKey;
    private string? _breweryBlockedReasonText;
    private bool _breweryBlockedPreviouslyEnabled;
    // Per-village brewery slot cache. Once we have positively identified a brewery
    // (via local scan or remote probe), we remember the slot id and treat subsequent
    // partial dorf2 scans as "still there" instead of flushing to "Brewery missing".
    // The buildings tab already keeps its own snapshot, but the celebration card was
    // reading status.Buildings directly — so a partial scan caused the badge to flip.
    private readonly Dictionary<string, int> _knownBrewerySlotByVillage = new(StringComparer.Ordinal);
    private string? _pendingManualOperationId;
    private readonly Dictionary<string, string> _operationNamesById = new(StringComparer.OrdinalIgnoreCase);
    private ManualExecutionState? _activeManualExecution;
    private bool _logDragSelecting;
    private int _logDragAnchorIndex = -1;
    private ListBox? _logDragSourceList;
    private Point _automationLoopDragStart;
    private LoopTaskOption? _automationLoopDragSource;
    private bool _suppressAutomationLoopConfigWrite;
    private bool _suppressFarmListUiRefresh;
    private bool _suppressFarmingSettingsConfigWrite;
    private bool _suppressTownHallCelebrationModeConfigWrite;
    private bool _farmingOperationBusy;
    private bool _natarsProfileAnalyzed;
    private DateTimeOffset _lastFarmListsAnalysisAt = DateTimeOffset.MinValue;
    private VillageStatus? _lastBuildingStatus;
    private VillageStatus? _lastResourceStatusForUi;
    private readonly object _pendingLogSync = new();
    private readonly Queue<string> _pendingLogMessages = new();
    private readonly object _sessionLogWriteSync = new();
    private bool _logFlushQueued;
    private bool _continuousLoopConstructionStatusNeedsSync = true;
    private bool _restartContinuousLoopAfterStop;
    private bool _startContinuousLoopAfterQueueStop;
    private int _continuousLoopWakeRequested;

    /// <summary>
    /// Public accessor so the Buildings panel can bind to the buildings view
    /// model via {Binding BuildingsVm..., ElementName=RootWindow}.
    /// </summary>
    public BuildingsViewModel BuildingsVm => _buildingsViewModel;

    private void InitializeBuildingSlotPlaceholders()
    {
        if (_buildingRows.Count > 0)
        {
            return;
        }

        foreach (var slotId in Enumerable.Range(19, 22))
        {
            BuildingSlotLayoutById.TryGetValue(slotId, out var layout);
            var isWallSlot = slotId == 40;
            var isRallyPointSlot = IsRallyPointSlot(slotId);
            _buildingRows.Add(new BuildingSlotRow
            {
                SlotId = slotId,
                Name = isRallyPointSlot ? "Rally Point" : isWallSlot ? "Wall" : "Empty",
                Level = isWallSlot || isRallyPointSlot ? 0 : null,
                Gid = null,
                Category = string.Empty,
                Requirements = string.Empty,
                PendingTargetLevel = null,
                PendingConstructName = string.Empty,
                PendingConstructGid = null,
                IsDemolishing = false,
                MapLeft = layout.Left,
                MapTop = layout.Top,
                IsWallSlot = isWallSlot,
                IsRallyPointSlot = isRallyPointSlot,
            });
        }
    }

    private bool IsMainTabSelected(TabItem tabItem)
    {
        return ReferenceEquals(MainTabControl?.SelectedItem, tabItem);
    }

    public MainWindow()
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        TryApplyWindowIcon();

        _loopController = App.Services.GetRequiredService<LoopController>();
        _loopController.Logger = AppendLog;
        _heroViewModel.Logger = AppendLog;
        _heroViewModel.PropertyChanged += HeroViewModel_PropertyChanged;

        _projectRoot = ProjectRootLocator.FindProjectRoot();
        _versionPath = Path.Combine(_projectRoot, "VERSION");
        _botConfigPath = Path.Combine(_projectRoot, "config", "bot.json");
        _envPath = Path.Combine(_projectRoot, ".env");
        _serverCatalogPath = Path.Combine(_projectRoot, "config", "servers.user.json");
        _sessionLogPath = Path.Combine(_projectRoot, "logs", $"TbotUltra_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        InitializeSessionLogFile();

        _accountProvider = new EnvAccountProvider(_envPath);
        _accountStore = new EnvAccountStore(_envPath);
        _botConfigStore = new BotConfigStore(_botConfigPath, _projectRoot, () => _accountStore.ActiveAccountName());
        _villageSettingsStore = new VillageSettingsStore(_projectRoot, () => _accountStore.ActiveAccountName(), AppendLog);
        _travcoListStore = new TravcoListStore(_projectRoot, () => _accountStore.ActiveAccountName(), AppendLog);
        _villageCacheStore = new VillageCacheStore(_projectRoot, () => _accountStore.ActiveAccountName(), AppendLog);
        InitializeSessionPacing();
        _accountAnalysisStore = new AccountAnalysisStore(_projectRoot);
        _heroAttributeSnapshotStore = new HeroAttributeSnapshotStore(_projectRoot);
        _natarFarmCacheStore = new NatarFarmCacheStore(_projectRoot);
        _manualFarmingPreferenceStore = new ManualFarmingPreferenceStore(_projectRoot);
        _serverDiscoveryService = new ServerDiscoveryService();
        _serverCatalogStore = new ServerCatalogStore(_serverCatalogPath);
        var projectContext = new ProjectContext(_projectRoot);
        var captchaAutoSolver = new CaptchaAutoSolver(projectContext);
        _captchaAutoSolver = captchaAutoSolver;
        var taskRunner = new BotTaskRunner(_accountProvider, projectContext, captchaAutoSolver);
        // One-time migration of the old shared config/queue.json into the active account's per-account
        // queue file. Runs before the store is used so the recover/clear logic below sees the migrated items.
        QueueMigration.MigrateLegacyGlobalQueue(_projectRoot, _accountStore.ActiveAccountName(), AppendLog);
        // The queue is now per account; the store resolves the active account's queue.json per operation.
        var queueStore = new JsonQueueStore(() => AccountStoragePaths.AccountQueuePath(_projectRoot, _accountStore.ActiveAccountName()));
        _accountDeletionService = new AccountDeletionService(_projectRoot, _accountStore, _botConfigStore, queueStore);
        var queueScheduler = new PriorityFifoQueueScheduler();
        var queueExecutor = new QueueExecutor(taskRunner);
        _botService = new DesktopBotService(taskRunner, queueStore, queueScheduler, queueExecutor);
        if (ShouldClearQueueOnStartup())
        {
            _botService.ClearQueue();
        }
        else
        {
            // Queue persists across restarts: recover items left in Running by a previous session
            // that crashed mid-execution, so they don't stay stuck forever.
            var recovered = _botService.ResetOrphanedRunningQueueItems();
            if (recovered > 0)
            {
                AppendLog($"Recovered {recovered} queue item(s) stuck in Running from a previous session; reset to Pending.");
            }
        }

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            try
            {
                var serverNow = GetServerNow();
                var dashboardTabSelected = IsMainTabSelected(DashboardTabItem);

                UpdateClockText(serverNow);
                UpdateSessionPacingUi();
                HandleBrowserClosedSignal();
                TickFarmListCountdowns();
                if (dashboardTabSelected)
                {
                    TickAutomationLoopCountdowns();
                }

                // The "Next task" value runs the loop selector (reads the queue/options). Queue reads now
                // come from the in-memory cache, so recompute every second; also refreshed on real
                // queue/group changes via RefreshAutomationLoopDashboardUi.
                UpdateNextTaskUi();
                _troopTrainingViewModel.TickCountdowns(serverNow);
                TickSmithyUpgradeCountdown();
                _resourcesViewModel.TickLiveForecasts();

                if (IsMainTabSelected(NpcTradeTabItem))
                {
                    TickResourceTransferVillageForecasts();
                }

                if (IsMainTabSelected(ReinforcementsTabItem))
                {
                    UpdateReinforcementStatus();
                }

                UpdateExecutionStateIndicator(updateAutomationLoopCards: dashboardTabSelected);
                if (IsMainTabSelected(FarmingTabItem))
                {
                    UpdateManualFarmingRunningState();
                }
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
        _resourceSnapshotRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(16) };
        _resourceSnapshotRefreshTimer.Tick += async (_, _) => await HandleResourceSnapshotRefreshTickAsync();
        _resourceSnapshotRefreshTimer.Start();
        _troopTrainingDeferredRefreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _troopTrainingDeferredRefreshDebounceTimer.Tick += (_, _) =>
        {
            _troopTrainingDeferredRefreshDebounceTimer.Stop();
            // The user changed troop-training settings. A queued build_troops item still carries the OLD
            // per-village snapshot and the loop won't re-enqueue while it is active, so drop the selected
            // village's item to force a fresh enqueue with the new settings (e.g. a lowered % threshold).
            RemoveTroopTrainingQueueItemsForVillage(GetSelectedVillageName());
            if (_lastResourceStatusForUi is not null)
            {
                TriggerDeferredTroopTrainingWaitRefresh(_lastResourceStatusForUi, "troop_config_changed", force: true);
            }
        };
        _queueUiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _queueUiRefreshTimer.Tick += (_, _) =>
        {
            _queueUiRefreshTimer.Stop();
            var selectId = _pendingQueueUiSelectId;
            _pendingQueueUiSelectId = null;
            RefreshQueueUi(selectId);
        };

        _queueServerTimeOffset = ResolveQueueServerTimeOffset();

        _terminalView = CollectionViewSource.GetDefaultView(_terminalEntries);
        _terminalView.Filter = TerminalEntryFilter;
        TerminalListBox.ItemsSource = _terminalView;
        InitializeLogFilterControls();
        _alarmView = CollectionViewSource.GetDefaultView(_alarmEntries);
        _alarmView.Filter = AlarmEntryFilter;
        AlarmListBox.ItemsSource = _alarmView;
        TravianBuildQueueDataGrid.ItemsSource = _travianBuildQueueRows;
        TravianSmithyQueueDataGrid.ItemsSource = _travianSmithyQueueRows;
        UpdateCaptchaStatsUi();
        UpdateNpcTradeStatsUi();
        _automationLoopTasksView = CollectionViewSource.GetDefaultView(_automationLoopTasks);
        _automationLoopTasksView.Filter = AutomationLoopTaskFilter;
        AutomationLoopListBox.ItemsSource = _automationLoopTasksView;
        ResourceTransferTargetVillageComboBox.ItemsSource = _resourceTransferVillages;
        ResourceTransferSourceVillagesItemsControl.ItemsSource = _resourceTransferVillages;
        ReinforcementTargetVillageComboBox.ItemsSource = _reinforcementVillages;
        ReinforcementSourceVillagesItemsControl.ItemsSource = _reinforcementSourceVillages;
        InitializeReinforcementSendSettings();
        FarmListsItemsControl.ItemsSource = _farmLists;
        EnsureFarmListPlaceholderRow();
        _troopTrainingViewModel.Initialize();
        _troopTrainingViewModel.UpdateTroopOptions(ResolveStoredTroopTrainingTribe());
        _troopTrainingViewModel.ResetQueueStatus();
        _troopTrainingViewModel.ConfigChanged += OnTroopTrainingConfigChanged;
        SubscribeToHeroInventoryUpdates();
        InitializeBuildingSlotPlaceholders();
        _farmLists.CollectionChanged += (_, _) =>
        {
            if (_suppressFarmListUiRefresh)
            {
                return;
            }

            SyncFarmListSelectionHandlers();
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

        ResetVillageSelectionUi();
        VillageComboBox.SelectionChanged += VillageComboBox_SelectionChanged;
        BuildingCategoryComboBox.ItemsSource = new[] { "all", "infrastructure", "army_buildings", "resource_buildings" };
        BuildingCategoryComboBox.SelectedIndex = 0;
        ConstructBuildingComboBox.ItemsSource = _buildingCatalogOptions;
        DemolishBuildingComboBox.ItemsSource = _demolishableBuildings;

        LoadConfigToUi();
        LoadVersionToUi();
        RefreshQueueUi();
        Closing += MainWindow_Closing;
        if (Application.Current is not null)
        {
            Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledExceptionForLog;
        }
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
        _heroViewModel.LoadPriorityFromConfig(null);
        StartBackgroundWarmups();
    }

    // Rad 7 safety net for async void UI handlers. App.xaml.cs already keeps the process alive
    // (e.Handled = true) and writes the exception to the on-disk crash log; this additionally surfaces
    // it in the in-app session log so an async void handler that throws is debuggable without opening
    // the crash file. Does not set e.Handled (App owns that). OperationCanceledException is normal
    // control flow (cancel/stop) and is skipped.
    private void OnDispatcherUnhandledExceptionForLog(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is OperationCanceledException)
        {
            return;
        }

        AppendLog($"[ui] Unhandled exception in UI handler: {e.Exception.Message}");
    }

    internal Task GuardUiAsync(Func<Task> action, [CallerMemberName] string caller = "")
        => AsyncUi.GuardAsync(action, AppendLog, caller);

    private void TryApplyWindowIcon()
    {
        // Keep the application icon from the exe when the .ico resource is not WPF-decodable.
    }

    private BotOptions LoadBotOptions()
    {
        var configJson = _botConfigStore.Load().ToJsonString();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(configJson));
        var configuration = new ConfigurationBuilder()
            .SetBasePath(_projectRoot)
            .AddJsonStream(stream)
            .Build();
        return BotOptionsFactory.FromConfiguration(configuration);
    }

    private void LoadConfigToUi()
    {
        _queueServerTimeOffset = ResolveQueueServerTimeOffset();
        UpdateClockText();
        RefreshAccountPicker();
        SyncServerFromActiveAccount();
        UpdateCaptchaCardVisibility();

        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var storedAutoCelebration = TryGetStoredAutoCelebrationPreference();
        var hasExplicitAutoCelebrationSetting = storedAutoCelebration.HasValue;
        LoadAutomationLoopTasks(options);
        // Settings saves reload the account/global config. Keep the dashboard group toggles on the
        // selected village's saved state so they do not drift from the Village settings popup.
        if (_isLoggedIn)
        {
            ApplyAutomationLoopGroupsForSelectedVillage();
        }

        _troopTrainingViewModel.ApplyConfigToBuildings(options, hasExplicitAutoCelebrationSetting, storedAutoCelebration.Value);
        // Overlay the selected village's per-village training override (if any) onto the building rows so
        // the Troops tab shows that village's settings, not just the account-wide defaults.
        ApplyTroopTrainingForSelectedVillage();
        ApplyTroopTrainingTribeState(ResolveStoredTroopTrainingTribe());
        _troopTrainingViewModel.InfoText = "Configure troop building rules and refresh queues when needed.";
        _suppressHeroHideModeApply = true;
        try
        {
            _heroViewModel.LoadSettingsFromConfig(options);
        }
        finally
        {
            _suppressHeroHideModeApply = false;
        }

        _resourcesViewModel.LoadSettingsFromConfig(options);
        ApplyResourceTransferConfigToUi(options);
        ApplyReinforcementConfigToUi(options);
        ApplyFarmingSettingsToUi(options);
        ApplyTownHallCelebrationModeToUi(options);
        ApplyAutoCollectTasksConfigToUi(options);
        ApplyAutoCollectDailyQuestsConfigToUi(options);
        ApplyHeroResourceTransferConfigToUi(options);

        // Account + runtime state below (account label, inbox counts, gold-club, Natars, hero snapshot) is
        // seeded from caches/disk. That is only correct before login — startup or an account switch — when the
        // UI has no live state yet. When already logged in this method is reached only from a Settings-popup
        // save; re-seeding then would overwrite newer live state with stale cache values (e.g. the hero home
        // village driving the green dashboard icon, or the inbox counts). So skip the reseed while logged in.
        if (!_isLoggedIn)
        {
            try
            {
                var account = _accountProvider.LoadAccount();
                StatusTextBlock.Text = $"Loaded account '{account.Name}'.";
                AppendLog($"Loaded account '{account.Name}'.");
                UpdateAccountInfoLabel(account.Name);
                UpdateInboxButtons(0, 0);
                UpdateGoldClubInfoFromStoredAnalysis();
                RefreshNatarsProfileAnalyzedFromCache();
                LoadHeroAttributeSnapshotForActiveAccount(account.Name);
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

        // Hide/show Natar-only controls based on whether the active server is the private server.
        ApplyNatarFeatureVisibility();
    }

    private void RefreshNatarsProfileAnalyzedFromCache()
    {
        if (!IsNatarFarmingAvailable())
        {
            return;
        }

        AppendLog("[RefreshNatarsProfileAnalyzedFromCache] Started");
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
        AppendLog("[RefreshNatarsProfileAnalyzedFromCache] Completed");
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
            ApplySelectedVillageToPayload(payload);

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

    private void EndInlineWait()
    {
        _inlineWaitUntilUtc = DateTimeOffset.MinValue;
        UpdateExecutionStateIndicator();
    }

    private async Task RefreshCurrentPageStorageStatusAsync(
        BotOptions options,
        string source,
        CancellationToken cancellationToken)
    {
        var status = await _botService.ReadCurrentPageStorageStatusAsync(options, AppendLog, cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            ApplyStorageStatusToUi(status, source);
        });
    }

    private void ApplyPostLoginSnapshot(PostLoginSnapshot snapshot)
    {
        var status = snapshot.VillageStatus;
        var rows = ApplyResourceRowsAndVillageStatus(status, includeQueuedTargets: true);
        TriggerDeferredConstructionWaitRefresh(status, "post_login");

        _lastBuildingStatus = status;
        PopulateBuildingsTab(status);

        BuildingsInfoTextBlock.Text = _buildingsViewModel.DescribeLoadedSlots($"active village '{status.ActiveVillage}'");
        SetTribeText(status.Tribe);
        VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
        // Select the village the browser actually landed in (active village), not a stale prior selection,
        // so the dropdown matches the browser and Start bot works in the landing village.
        SyncDashboardVillageUiFromVillages(status.Villages, status.ActiveVillage, status.ActiveVillage);
        ReconcileConfirmedVillageList(status.Villages, "post_login");
        // Apply the landing village's per-village automation-group override to the dashboard cards. Without
        // this the cards keep the global default loaded by LoadAutomationLoopTasks, so on login they could
        // disagree with the per-village toggles shown in the Village settings window (e.g. Construction on
        // there but off in the dashboard).
        ApplyAutomationLoopGroupsForSelectedVillage();

        // Cache this full read and mark the village the browser actually logged into as the active
        // working village, so the green border is correct and visible immediately after login (not only
        // after the first task runs). This full status carries the village list needed to resolve the key.
        CacheVillageStatus(status);
        SetActiveWorkingVillageFromStatus(status);
        UpdateInboxButtons(snapshot.InboxStatus.UnreadMessages, snapshot.InboxStatus.UnreadReports);
        ApplyFarmingAvailabilityFromGoldClubStatus(TryGetStoredGoldClubEnabled(_accountStore.ActiveAccountName()));

        if (snapshot.AdventureCount is null)
        {
            ApplyHeroAdventureAvailability(null);
            AppendLog("[ApplyPostLoginSnapshot] Adventure count: not found on current page.");
        }
        else
        {
            ApplyHeroAdventureAvailability(snapshot.AdventureCount.Value);
            AppendLog($"[ApplyPostLoginSnapshot] Adventure count after login: {snapshot.AdventureCount.Value}.");
        }

        if (snapshot.HeroInventory is { } heroInventory)
        {
            _heroViewModel.ApplyInventory(heroInventory);
            AppendLog($"[ApplyPostLoginSnapshot] Hero inventory after login: wood={heroInventory.Wood}, clay={heroInventory.Clay}, iron={heroInventory.Iron}, crop={heroInventory.Crop}.");
        }
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
            var sleeping = IsSessionSleeping;
            var defaultEnabled = !busy && !sleeping;
            SetEnabled(AccountComboBox, defaultEnabled);
            SetEnabled(LoginButton, defaultEnabled);
            SetEnabled(LogoutButton, defaultEnabled);
            SetEnabled(SettingsButton, !busy);
            SetEnabled(QueueRemoveButton, !busy);
            SetEnabled(QueueMoveUpButton, !busy);
            SetEnabled(QueueMoveDownButton, !busy);
            SetEnabled(QueueClearButton, !busy);
            SetEnabled(QueueRefreshButton, !busy);
            SetEnabled(ResetProgramButton, true);
            SetEnabled(StorageRefreshButton, defaultEnabled);
            var automationActive = _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted);
            SetEnabled(
                AccountScanButton,
                _isLoggedIn
                && !sleeping
                && !_accountScanInProgress
                && (!busy || automationActive));
            UpdateResourceTransferStatus();
            _resourcesViewModel.ActionsEnabled = !busy;
            _inboxViewModel.ActionsEnabled = defaultEnabled;
            SetEnabled(StopBotButton, true);

            if (StartLoopButton is not null)
            {
                StartLoopButton.IsEnabled = _isLoggedIn && !sleeping;
                StartLoopButton.Content = (busy || _autoQueueRunning || !string.IsNullOrWhiteSpace(_activeFunctionDisplayName) || (_loopTask is not null && !_loopTask.IsCompleted))
                    ? "Pause bot"
                    : "Start bot";
            }

            SyncFarmingControlsEnabledState();
            ApplySessionSleepingUiState();
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

    private static void SetEnabled(UIElement? element, bool enabled)
    {
        if (element is not null)
        {
            element.IsEnabled = enabled;
        }
    }

    private async Task EnsureChromiumInstalledAsync(bool forceInstall = false)
    {
        if (!forceInstall)
        {
            if (_chromiumEnsured || BrowserSession.ChromiumAlreadyInstalled(_projectRoot))
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

    private string BeginOperation(string operationName)
    {
        var id = System.Threading.Interlocked.Increment(ref _operationCounter);
        var operationId = $"OP{id:D4}";
        _operationNamesById[operationId] = operationName;
        _pendingManualOperationId = operationId;
        AppendLog($"[{operationId}] [{operationName} STARTED]");
        return operationId;
    }

    /// <summary>
    /// Disposes the current manual-operation CTS and clears the field. Idempotent;
    /// safe to call from finally blocks even when no operation is running.
    /// Centralizes the dispose/null pattern previously duplicated across panels.
    /// </summary>
    private void DisposeOperationCts() => _loopController.DisposeOperation();

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

    private async Task<bool> TryHandleTroopsBlockedExecutionAsync(QueueItem queueItem, Exception ex, string logPrefix)
    {
        if (!string.Equals(queueItem.TaskName, "upgrade_troops_at_smithy", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryExtractTroopsBlockedReason(ex.Message, out var reasonKey, out var reasonText))
        {
            return false;
        }

        if (string.Equals(reasonKey, TroopsBlockedReasonSmithyMissing, StringComparison.OrdinalIgnoreCase))
        {
            var verifiedMissing = await VerifySmithyMissingAsync();
            if (verifiedMissing != true)
            {
                _botService.MarkQueueItemDeferred(queueItem.Id, TimeSpan.FromSeconds(10));
                AppendLog(verifiedMissing == false
                    ? $"{logPrefix} RETRY task={queueItem.TaskName} | Smithy exists after verification. Ignoring transient missing read."
                    : $"{logPrefix} RETRY task={queueItem.TaskName} | Could not verify Smithy state. Skipping permanent block.");
                return true;
            }
        }

        _botService.MarkQueueItemSucceeded(queueItem.Id);
        SetTroopsBlockedState(reasonKey, reasonText);
        AppendLog($"{logPrefix} BLOCKED task={queueItem.TaskName} | {reasonText}");
        return true;
    }

    private async Task<bool?> VerifySmithyMissingAsync()
    {
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var status = await _botService.ReadBuildingsStatusAsync(options, AppendLog, CancellationToken.None);
            var uiStatus = MergeBuildingStatusForUi(status);
            await Dispatcher.InvokeAsync(() =>
            {
                _lastBuildingStatus = uiStatus;
                ApplyTroopsAvailabilityFromVillageStatus(uiStatus);
                PopulateBuildingsTab(uiStatus);
            });

            return !HasSmithyInVillageStatus(status);
        }
        catch (Exception ex)
        {
            AppendLog($"Smithy verification after blocked read failed: {ex.Message}");
            return null;
        }
    }

    private VillageStatus MergeBuildingStatusForUi(VillageStatus status)
    {
        if (_lastBuildingStatus is null)
        {
            return status;
        }

        return _lastBuildingStatus with
        {
            ActiveVillage = status.ActiveVillage,
            Villages = status.Villages.Count > 0 ? status.Villages : _lastBuildingStatus.Villages,
            Buildings = status.Buildings,
            Tribe = string.IsNullOrWhiteSpace(status.Tribe) || string.Equals(status.Tribe, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? _lastBuildingStatus.Tribe
                : status.Tribe,
            VillageCount = status.VillageCount > 0 ? status.VillageCount : _lastBuildingStatus.VillageCount,
            IsCapital = status.IsCapital ?? _lastBuildingStatus.IsCapital,
            ServerTimeUtc = status.ServerTimeUtc ?? _lastBuildingStatus.ServerTimeUtc,
        };
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
                if (!ReferenceEquals(_loopTask, loopTask))
                {
                    return;
                }

                StartLoopButton.Content = "Start bot";
                StartLoopButton.IsEnabled = true;
                SetLoopIndicator(false);
                NotifySessionPacingAutomationStopped();
                AppendLog("Loop stopped.");
                if (_restartContinuousLoopAfterStop
                    && ContinuousRunToggleButton?.IsChecked == true
                    && _isLoggedIn
                    && (_loopTask is null || _loopTask.IsCompleted))
                {
                    _restartContinuousLoopAfterStop = false;
                    StartContinuousLoopRunner();
                    return;
                }

                _restartContinuousLoopAfterStop = false;
            });
        }
    }

    private void HeroViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressHeroHideModeApply)
        {
            return;
        }

        if (e.PropertyName is not (
            nameof(HeroViewModel.MinHpForAdventure)
            or nameof(HeroViewModel.HeroHpRegenPerDayPercent)
            or nameof(HeroViewModel.AutoRevive)
            or nameof(HeroViewModel.AutoAssignPoints)
            or nameof(HeroViewModel.AutoUseOintments)
            or nameof(HeroViewModel.IsAdventurePickTop)
            or nameof(HeroViewModel.IsAdventurePickShortest)
            or nameof(HeroViewModel.HideModeControlEnabled)
            or nameof(HeroViewModel.IsHideModeFight)
            or nameof(HeroViewModel.IsHideModeHide)
            or nameof(HeroViewModel.ContinuousAdventures)
            or nameof(HeroViewModel.IncreaseAdventuresToHard)
            or nameof(HeroViewModel.ReduceAdventureTime)))
        {
            return;
        }

        PersistHeroSettingsToConfig();
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
            || normalized.Contains("password is incorrect")
            || normalized.Contains("incorrect password")
            || normalized.Contains("invalid password"))
        {
            return "Login failed: wrong password.";
        }

        if (normalized.Contains("name or password")
            || normalized.Contains("name or the password")
            || normalized.Contains("username or password"))
        {
            return "Login failed: wrong username or password.";
        }

        if ((normalized.Contains("username") || normalized.Contains("user") || normalized.Contains("account"))
            && (normalized.Contains("not exist")
                || normalized.Contains("doesn't exist")
                || normalized.Contains("does not exist")
                || normalized.Contains("unknown")
                || normalized.Contains("not found")))
        {
            return "Login failed: that username does not exist on this server.";
        }

        if (normalized.Contains("invalid")
            && normalized.Contains("credential"))
        {
            return "Login failed: username does not exist or password is incorrect.";
        }

        if (normalized.Contains("too many login attempts"))
        {
            return "Login failed: too many attempts. Wait a moment before trying again.";
        }

        if (normalized.Contains("login form did not load"))
        {
            return "Login failed: the login page did not load. The server may be slow or unavailable.";
        }

        if (normalized.Contains("could not find login button")
            || normalized.Contains("could not find input field"))
        {
            return "Login failed: the login controls were not found on the page.";
        }

        if (normalized.Contains("not confirmed before timeout")
            || normalized.Contains("did not complete successfully")
            || normalized.Contains("login was not confirmed"))
        {
            return "Login timed out. The page may be slow, or login/captcha needs attention in the browser.";
        }

        // Surface explicit "Login failed: ..." messages produced by the worker as-is.
        if (message.StartsWith("Login failed:", StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        return null;
    }

    private void ApplyVillageStatusToUi(VillageStatus status)
    {
        status = MergeResourceStatusForUi(status);
        ApplyResourceTransferVillageResourceStatus(status);
        UpdateActiveVillageResourceMaxLevel(status);
        _resourcesViewModel.ApplyStorageForecasts(status);
        VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
        SetTribeText(status.Tribe);
        ApplyTroopTrainingTribeState(status.Tribe);
        LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}";
        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        var goldText = status.Gold?.ToString() ?? "-";
        var silverText = status.Silver?.ToString() ?? "-";
        SetGoldSilverStatusText(ServerResourcesTextBlock, SilverInfoTextBlock, goldText, silverText);

        var constructionTimer = ConstructionQueueState.ResolveLiveConstructionTimer(status);
        _buildQueueActiveCount = constructionTimer.ActiveCount;
        _buildQueueRemainingSeconds = constructionTimer.RemainingSeconds ?? -1;
        _buildQueueReachedZeroPendingCompletion = false;
        ApplyTroopsAvailabilityFromVillageStatus(status);
        _troopTrainingViewModel.ApplyStatus(status, status.TroopTrainingQueues ?? _lastBuildingStatus?.TroopTrainingQueues);
        if (status.SmithyUpgradeStatus is not null)
        {
            ApplySmithyUpgradeStatus(status.SmithyUpgradeStatus);
        }

        if (status.BreweryCelebrationStatus is not null)
        {
            _troopTrainingViewModel.ApplyBreweryCelebrationStatus(status.BreweryCelebrationStatus);
        }
        else
        {
            ApplyLocalBreweryCelebrationStatus(status);
        }
        UpdateBuildQueueStatusText();
        UpdateAutomationLoopRunningIndicators();
        RefreshVillagePicker(status);
    }

    private void RefreshVillagePicker(VillageStatus status)
    {
        SyncDashboardVillageUiFromVillages(status.Villages, status.ActiveVillage);
    }

    private void UpdateLoginButtonsVisual(bool isLoggedIn)
    {
        StartLoopButton.IsEnabled = isLoggedIn;
        AccountScanButton.IsEnabled = isLoggedIn && !IsSessionSleeping && !_accountScanInProgress;

        // Both buttons use a soft/tinted style (like Start/Pause bot). Only the available action is
        // colored: Login is green when logged out, Logout is red when logged in; the other is neutral.
        if (isLoggedIn)
        {
            ApplyNeutralButtonVisual(LoginButton);
            ApplySoftButtonVisual(LogoutButton, "DangerBgBrush", "DangerBorderBrush", "DangerTextBrush");
            return;
        }

        ApplySoftButtonVisual(LoginButton, "SuccessBgBrush", "SuccessBorderBrush", "SuccessTextBrush");
        ApplyNeutralButtonVisual(LogoutButton);
    }

    private static void ApplySoftButtonVisual(System.Windows.Controls.Button button, string bgKey, string borderKey, string foregroundKey)
    {
        button.Background = new SolidColorBrush(ThemeColors.Get(bgKey));
        button.BorderBrush = new SolidColorBrush(ThemeColors.Get(borderKey));
        button.Foreground = new SolidColorBrush(ThemeColors.Get(foregroundKey));
    }

    private static void ApplyNeutralButtonVisual(System.Windows.Controls.Button button)
    {
        button.Background = new SolidColorBrush(ThemeColors.Get("ControlBackgroundBrush"));
        button.BorderBrush = new SolidColorBrush(ThemeColors.Get("BorderBrush"));
        button.Foreground = new SolidColorBrush(ThemeColors.Get("TextSubtleBrush"));
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
        => await GuardUiAsync(OpenVerificationBrowserAsync);

    private async Task OpenVerificationBrowserAsync()
    {
        if (BlockIfSessionSleeping("Open verification browser"))
        {
            return;
        }

        var operationId = BeginOperation("OpenVerificationBrowser");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
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
            DisposeOperationCts();
        }
    }

    private void SupportButton_Click(object sender, RoutedEventArgs e)
    {
        var support = new SupportWindow(_projectRoot, _terminalEntries.Select(entry => entry.Text).ToList())
        {
            Owner = this,
        };
        support.ShowDialog();
    }

}
