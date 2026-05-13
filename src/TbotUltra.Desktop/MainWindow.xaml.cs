using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
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

    private sealed record NatarListRow(
        int Index,
        string VillageName,
        int X,
        int Y);

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
    private readonly ICaptchaAutoSolver _captchaAutoSolver;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly DispatcherTimer _inboxRefreshTimer;
    private readonly DispatcherTimer _buildQueueCountdownTimer;
    private readonly DispatcherTimer _resourceSnapshotRefreshTimer;
    private readonly DispatcherTimer _troopTrainingDeferredRefreshDebounceTimer;
    private readonly ObservableCollection<string> _terminalEntries = [];
    private readonly ObservableCollection<AlarmEntryRow> _alarmEntries = [];
    private readonly ObservableCollection<LoopTaskOption> _automationLoopTasks = [];
    private readonly ObservableCollection<FarmListStatusRow> _farmLists = [];
    private readonly ObservableCollection<BuildingSlotRow> _buildingRows = [];
    private readonly ObservableCollection<BuildingCatalogOption> _buildingCatalogOptions = [];
    private readonly ObservableCollection<BuildingSlotRow> _demolishableBuildings = [];
    private readonly Dictionary<int, DateTimeOffset> _resourceClickCooldownBySlot = new();
    private readonly Dictionary<int, (int Target, DateTimeOffset At)> _resourceLastQueuedTargetBySlot = new();
    private readonly Dictionary<int, int> _resourcePendingTargetBySlot = new();
    private FunctionTestWindow? _resourceTestFunctionsWindow;
    private readonly Dictionary<int, DateTimeOffset> _buildingClickCooldownBySlot = new();
    private readonly Dictionary<int, (int Target, DateTimeOffset At)> _buildingLastQueuedTargetBySlot = new();
    private readonly Dictionary<int, (string Name, DateTimeOffset At)> _buildingLastQueuedConstructBySlot = new();
    private readonly HashSet<int> _buildingDemolishingSlots = new();
    private static readonly IReadOnlyDictionary<int, (double Left, double Top)> BuildingSlotLayoutById = CreateBuildingSlotLayout();

    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _operationCts;
    private bool _suppressHeroHideModeApply;
    private CancellationTokenSource? _villageSwitchCts;
    private Task? _loopTask;
    private bool _chromiumEnsured;
    private bool _suppressAccountSelectionChange;
    private bool _suppressVillageSelectionChange;
    private bool _resourceSnapshotRefreshRunning;
    private bool _resourceProductionRefreshRunning;
    private bool _resourceProductionRefreshPending;
    private bool _deferredConstructionRefreshRunning;
    private bool _deferredTroopTrainingRefreshRunning;
    private VillageStatus? _pendingDeferredTroopTrainingRefreshStatus;
    private string? _pendingDeferredTroopTrainingRefreshSource;
    private TimeSpan _queueServerTimeOffset;
    private long _operationCounter;
    private long _loopTickCounter;
    private readonly LoopController _loopController;

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
    private volatile bool _inboxAutoEnabled;
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
    private AppDialog? _captchaAutoSolvePopup;
    private DateTimeOffset _lastVerificationPopupAt = DateTimeOffset.MinValue;
    private DateTimeOffset _inlineWaitUntilUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _constructionInlineWaitUntilUtc = DateTimeOffset.MinValue;
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
    private bool _farmingOperationBusy;
    private bool _natarsProfileAnalyzed;
    private DateTimeOffset _lastFarmListsAnalysisAt = DateTimeOffset.MinValue;
    private string _lastVillageSwitchRefreshKey = string.Empty;
    private DateTimeOffset _lastVillageSwitchRefreshAt = DateTimeOffset.MinValue;
    private VillageStatus? _lastBuildingStatus;
    private VillageStatus? _lastResourceStatusForUi;
    private readonly object _pendingLogSync = new();
    private readonly Queue<string> _pendingLogMessages = new();
    private readonly object _sessionLogWriteSync = new();
    private readonly List<string> _sessionLogLines = [];
    private readonly List<string> _sessionAlarmLines = [];
    private bool _logFlushQueued;
    private bool _continuousLoopConstructionStatusNeedsSync = true;
    private bool _restartContinuousLoopAfterStop;
    private bool _startContinuousLoopAfterQueueStop;

    public ObservableCollection<BuildingSlotRow> BuildingSlots => _buildingRows;

    private static bool IsPinnedBuildingTopSlot(int slotId)
    {
        return slotId == 26 || slotId == 39 || slotId == 40;
    }

    private void BuildingTopSlotsView_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BuildingSlotRow row && IsPinnedBuildingTopSlot(row.SlotId);
    }

    private void BuildingRemainingSlotsView_Filter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is BuildingSlotRow row && !IsPinnedBuildingTopSlot(row.SlotId);
    }

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
                IsDemolishing = false,
                MapLeft = layout.Left,
                MapTop = layout.Top,
                IsWallSlot = isWallSlot,
                IsRallyPointSlot = isRallyPointSlot,
            });
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        TryApplyWindowIcon();

        _loopController = App.Services.GetRequiredService<LoopController>();
        _loopController.Logger = AppendLog;
        _heroViewModel.Logger = AppendLog;
        _heroViewModel.PropertyChanged += HeroViewModel_PropertyChanged;

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
        _captchaAutoSolver = captchaAutoSolver;
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
                _troopTrainingViewModel.TickCountdowns();
                _resourcesViewModel.TickLiveForecasts();
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
        _resourceSnapshotRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(16) };
        _resourceSnapshotRefreshTimer.Tick += async (_, _) => await HandleResourceSnapshotRefreshTickAsync();
        _resourceSnapshotRefreshTimer.Start();
        _troopTrainingDeferredRefreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _troopTrainingDeferredRefreshDebounceTimer.Tick += (_, _) =>
        {
            _troopTrainingDeferredRefreshDebounceTimer.Stop();
            if (_lastResourceStatusForUi is not null)
            {
                TriggerDeferredTroopTrainingWaitRefresh(_lastResourceStatusForUi, "troop_config_changed", force: true);
            }
        };
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
        FarmListsItemsControl.ItemsSource = _farmLists;
        _troopTrainingViewModel.Initialize();
        _troopTrainingViewModel.UpdateTroopOptions(ResolveStoredTroopTrainingTribe());
        _troopTrainingViewModel.ResetQueueStatus();
        _troopTrainingViewModel.ConfigChanged += OnTroopTrainingConfigChanged;
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
        _heroViewModel.LoadPriorityFromConfig(null);
        StartBackgroundWarmups();
    }

    private void TryApplyWindowIcon()
    {
        // Keep the application icon from the exe when the .ico resource is not WPF-decodable.
    }

    private void StartBackgroundWarmups()
    {
        _ = Task.Run(RunBackgroundWarmupsAsync);
    }

    private async Task RunBackgroundWarmupsAsync()
    {
        await RunChromiumWarmupAsync();
        await RunCaptchaWarmupAsync();
    }

    private async Task RunChromiumWarmupAsync()
    {
        if (!BrowserSession.ChromiumAlreadyInstalled(_projectRoot))
        {
            AppendLog("Chromium warmup skipped: Chromium is not installed locally.");
            return;
        }

        var sw = Stopwatch.StartNew();
        AppendLog("Chromium warmup started.");
        try
        {
            var warmed = await BrowserSession.WarmupAsync(_projectRoot);
            sw.Stop();
            if (!warmed)
            {
                AppendLog("Chromium warmup skipped: already completed.");
                return;
            }

            AppendLog($"Chromium warmup completed in {sw.Elapsed.TotalSeconds:F1}s.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendLog($"Chromium warmup skipped: {ex.Message}");
        }
    }

    private async Task RunCaptchaWarmupAsync()
    {
        BotOptions options;
        try
        {
            options = LoadBotOptions();
        }
        catch (Exception ex)
        {
            AppendLog($"Captcha warmup skipped: could not load config ({ex.Message}).");
            return;
        }

        if (!options.CaptchaAutoSolveEnabled)
        {
            AppendLog("Captcha warmup skipped: captcha auto-solve is disabled.");
            return;
        }

        var sw = Stopwatch.StartNew();
        AppendLog("Captcha warmup started.");
        try
        {
            var warmed = await _captchaAutoSolver.WarmupAsync(CancellationToken.None);
            sw.Stop();
            if (!warmed)
            {
                AppendLog("Captcha warmup skipped: dependencies missing or warmup already completed.");
                return;
            }

            AppendLog($"Captcha warmup completed in {sw.Elapsed.TotalSeconds:F1}s.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendLog($"Captcha warmup skipped: {ex.Message}");
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
        var storedAutoCelebration = TryGetStoredAutoCelebrationPreference();
        var hasExplicitAutoCelebrationSetting = storedAutoCelebration.HasValue;
        LoadAutomationLoopTasks(options);
        _troopTrainingViewModel.ApplyConfigToBuildings(options, hasExplicitAutoCelebrationSetting, storedAutoCelebration.Value);
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateGoldClubInfo(enabled));
            return;
        }

        if (enabled == true)
        {
            GoldClubInfoTextBlock.Text = "Yes";
            GoldClubInfoTextBlock.Foreground = System.Windows.Media.Brushes.Green;
            if (string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoGoldClub, StringComparison.OrdinalIgnoreCase))
            {
                ClearFarmingBlockedState();
            }
        }
        else if (enabled == false)
        {
            GoldClubInfoTextBlock.Text = "No";
            GoldClubInfoTextBlock.Foreground = System.Windows.Media.Brushes.Red;
        }
        else
        {
            GoldClubInfoTextBlock.Text = "-";
            GoldClubInfoTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
        }
        UpdateAccountInfoLabel(_accountStore.ActiveAccountName());
    }

    private void ApplyFarmingAvailabilityFromGoldClubStatus(bool? enabled)
    {
        if (enabled == true)
        {
            if (string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoGoldClub, StringComparison.OrdinalIgnoreCase))
            {
                ClearFarmingBlockedState();
            }

            return;
        }

        if (enabled == false
            && !string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoGoldClub, StringComparison.OrdinalIgnoreCase))
        {
            SetFarmingBlockedState(FarmingBlockedReasonNoGoldClub, "No goldclub");
        }
    }

    private void UpdatePlusInfo(bool? active)
    {
        if (active == true)
        {
            PlusInfoTextBlock.Text = "Yes";
            PlusInfoTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
        else if (active == false)
        {
            PlusInfoTextBlock.Text = "No";
            PlusInfoTextBlock.Foreground = System.Windows.Media.Brushes.Red;
        }
        else
        {
            PlusInfoTextBlock.Text = "-";
            PlusInfoTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex PlusStatusRegex =
        new(@"\[plus\]\s*active=(True|False)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex GoldClubStatusRegex =
        new(@"\[goldclub\]\s*active=(True|False)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex TribeRegex =
        new(@"\[tribe\]\s*([A-Za-z]+)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex UiSyncRegex =
        new(@"\[ui-sync\]\s*(\{.*\})",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void TryApplyPlusStatusFromLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        var plusMatch = PlusStatusRegex.Match(line);
        if (plusMatch.Success)
        {
            var active = string.Equals(plusMatch.Groups[1].Value, "True", StringComparison.OrdinalIgnoreCase);
            UpdatePlusInfo(active);
        }

        var goldMatch = GoldClubStatusRegex.Match(line);
        if (goldMatch.Success)
        {
            var active = string.Equals(goldMatch.Groups[1].Value, "True", StringComparison.OrdinalIgnoreCase);
            UpdateGoldClubInfo(active ? true : false);
        }

        var tribeMatch = TribeRegex.Match(line);
        if (tribeMatch.Success)
        {
            var tribe = tribeMatch.Groups[1].Value;
            TribeInfoTextBlock.Text = $"Tribe: {tribe}";
            ApplyTroopTrainingTribeState(tribe);
        }

        var uiSyncMatch = UiSyncRegex.Match(line);
        if (uiSyncMatch.Success)
        {
            TryApplyUiSyncPayload(uiSyncMatch.Groups[1].Value);
        }
    }

    private void TryApplyUiSyncPayload(string rawJson)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<UiSyncPayload>(rawJson);
            if (payload is null)
            {
                return;
            }

            var goldText = payload.Gold?.ToString() ?? "-";
            var silverText = payload.Silver?.ToString() ?? "-";
            ServerResourcesTextBlock.Text = $"Gold: {goldText} | Silver: {silverText}";

            if (payload.Villages is { Count: > 0 })
            {
                VillagesInfoTextBlock.Text = $"Villages: {payload.Villages.Count}";
                var currentSelectedName = GetSelectedVillageName();
                var existingVillageData = (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
                    ?.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, VillageSelectionItem>(StringComparer.OrdinalIgnoreCase);
                var villages = payload.Villages
                    .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                    .Select(v =>
                    {
                        var name = v.Name!;
                        existingVillageData.TryGetValue(name, out var existing);

                        return new VillageSelectionItem
                        {
                            Name = name,
                            Url = v.Url ?? existing?.Url ?? string.Empty,
                            IsCapital = v.IsCapital ?? existing?.IsCapital ?? false,
                            CoordX = v.CoordX ?? existing?.CoordX,
                            CoordY = v.CoordY ?? existing?.CoordY,
                            Population = v.Population ?? existing?.Population,
                            CropFields = v.CropFields ?? existing?.CropFields,
                        };
                    })
                    .ToList();

                _suppressVillageSelectionChange = true;
                try
                {
                    VillageComboBox.ItemsSource = villages;
                    var selected = villages.FirstOrDefault(v =>
                        string.Equals(v.Name, currentSelectedName, StringComparison.OrdinalIgnoreCase))
                        ?? villages.FirstOrDefault(v =>
                            string.Equals(v.Name, payload.ActiveVillage, StringComparison.OrdinalIgnoreCase))
                        ?? villages.FirstOrDefault();
                    VillageComboBox.SelectedItem = selected;
                }
                finally
                {
                    _suppressVillageSelectionChange = false;
                }

                DashboardVillageList.ItemsSource = villages
                    .OrderByDescending(v => v.IsCapital)
                    .ThenByDescending(v => v.Population ?? -1)
                    .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch
        {
            // Ignore malformed sync lines.
        }
    }

    private void UpdateGoldClubInfoFromStoredAnalysis()
    {
        try
        {
            var accountName = _accountStore.ActiveAccountName();
            if (!string.IsNullOrWhiteSpace(accountName)
                && _accountAnalysisStore.TryLoad(accountName, out var analysis, GetActiveAccountServerUrl())
                && analysis is not null)
            {
                UpdateGoldClubInfo(analysis.GoldClubEnabled);
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

            // Ensure the state badge flips to "function running" (blue) the moment a task is
            // queued so the UI reflects the activity even before the auto-runner has scheduled it.
            SetActiveFunctionExecution(string.IsNullOrWhiteSpace(description) ? taskName : description);
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
        _operationCts = _loopController.CreateCts("operation");
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
            var snapshot = await _botService.LoadPostLoginSnapshotAsync(options, AppendLog, cancellationToken: operationToken);
            ApplyPostLoginSnapshot(snapshot);
            await RefreshResourceSnapshotForUiAsync(options, operationToken, forceCurrentVillage: true);
            if (options.PostLoginAnalyzeFarmlists)
            {
                try
                {
                    await RefreshFarmListsFromServerAsync(options, operationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"Post-login farmlist analyze failed: {ex.Message}");
                }
            }

            if (options.PostLoginAnalyzeHero)
            {
                try
                {
                    await RefreshHeroStatsAsync(operationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"Post-login hero analyze failed: {ex.Message}");
                }
            }

            if (options.PostLoginAnalyzeBrewery)
            {
                try
                {
                    await RefreshBreweryCelebrationStatusAsync(options, snapshot.VillageStatus, operationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"Post-login brewery analyze failed: {ex.Message}");
                }
            }

            await _botService.NavigateToVillageResourceFieldsAsync(
                options,
                AppendLog,
                GetSelectedVillageName(),
                GetSelectedVillageUrl(),
                cancellationToken: operationToken);
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
        _operationCts = _loopController.CreateCts("operation");
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
            _lastResourceStatusForUi = null;
            _resourcesViewModel.ResetStorageForecasts();
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
        var previouslyActiveAccount = _accountStore.ActiveAccountName();
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var defaultServers = await FetchDefaultServerOptionsAsync(options);
        var servers = FetchEffectiveServerOptions(defaultServers);
        var window = new AccountsWindow(_accountStore, _serverCatalogStore, options.ServerName, options.BaseUrl, servers, defaultServers)
        {
            Owner = this,
        };
        window.ShowDialog();
        var activeAccountAfterDialog = _accountStore.ActiveAccountName();
        if (!string.Equals(previouslyActiveAccount, activeAccountAfterDialog, StringComparison.OrdinalIgnoreCase))
        {
            ResetVillageSelectionUi();
        }

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
            _loopController.RequestLoopStop();
            _loopController.RequestQueueStop();
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
            _lastResourceStatusForUi = null;

            SetResourceRows([]);
            _resourcesViewModel.ResetStorageForecasts();
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
                // Graceful pause: don't pick up new queue items. Let the currently running
                // task finish; the runner will exit at its next iteration check.
                _loopController.RequestQueueStop();
                AppendLog("Pause requested. Letting current task finish before stopping.");
                return;
            }

            _loopController.ClearQueueStopRequest();
            ResumePausedQueueItems();
            _ = TriggerQueueAutoRunAsync();
            AppendLog("Function queue start requested.");
            return;
        }

        if (_autoQueueRunning)
        {
            _startContinuousLoopAfterQueueStop = true;
            _loopController.RequestQueueStop();
            AppendLog("Continuous loop requested. Letting current queue task finish before switching.");
            return;
        }

        if (_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            _loopController.RequestQueueStop();
            AppendLog("Pause requested. Letting current function finish before stopping.");
            return;
        }

        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            // Pause the loop gracefully too — flag stop, let current iteration finish.
            _loopController.RequestLoopStop();
            AppendLog("Pause requested. Loop will stop after the current iteration.");
            return;
        }

        StartContinuousLoopRunner();
    }

    private void StopBotButton_Click(object sender, RoutedEventArgs e)
    {
        // Hard stop: abort whatever is running right now (including waits) and clear state.
        _loopController.RequestQueueStop();
        _operationCts?.Cancel();
        _autoQueueRunCts?.Cancel();
        if (ContinuousRunToggleButton.IsChecked == true)
        {
            _loopController.RequestLoopStop();
            _loopCts?.Cancel();
        }

        EndInlineWait();
        ClearPendingResourceLevelsFromUi();
        _buildingDemolishingSlots.Clear();
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingClickCooldownBySlot.Clear();

        // Drop every pending/deferred queue item so the next Start doesn't resume them.
        try
        {
            _botService.ClearQueue();
            RequestQueueUiRefresh();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear queue on stop: {ex.Message}");
        }

        SetActiveFunctionExecution(null);
        UpdateExecutionStateIndicator();
        AppendLog("Stop requested. Running actions, waits, and queue cleared.");
    }

    private void ContinuousRunToggleButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_autoQueueRunning && !_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            return;
        }

        _loopController.RequestQueueStop();
        _loopController.RequestLoopStop();
        _startContinuousLoopAfterQueueStop = false;
        _operationCts?.Cancel();
        _autoQueueRunCts?.Cancel();
        _loopCts?.Cancel();
        ClearPendingResourceLevelsFromUi();
        SetLoopIndicator(false);
        StartLoopButton.Content = "Start bot";
        StartLoopButton.IsEnabled = true;
        AppendLog("Continuous run disabled. Running actions were stopped.");
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

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _loopController.MarkClosing();
            _inboxAutoEnabled = false;
            _clockTimer.Stop();
            _copyFeedbackTimer.Stop();
            _inboxRefreshTimer.Stop();
            _buildQueueCountdownTimer.Stop();
            _loopController.RequestLoopStop();
            _loopController.RequestQueueStop();
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

    private bool IsBuildingUpgradeQueueTask(string taskName)
    {
        return string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResourceAwareQueueTask(string taskName)
    {
        return string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskName, "build_troops", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleDeferredBuildingsMidWaitRefresh(QueueItem item, TimeSpan queueWaitDelay)
    {
        if (!IsBuildingUpgradeQueueTask(item.TaskName) || queueWaitDelay.TotalSeconds < 3)
        {
            return;
        }

        var halfDelay = TimeSpan.FromSeconds(Math.Max(1, Math.Floor(queueWaitDelay.TotalSeconds / 2d)));
        var baseOptions = ApplySelectedVillageToOptions(LoadBotOptions());
        var itemOptions = BotOptionsPayloadApplier.Apply(baseOptions, item.Payload);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(halfDelay);
                var status = await _botService.ReadBuildingsStatusAsync(itemOptions, AppendLog, CancellationToken.None);
                await Dispatcher.InvokeAsync(() =>
                {
                    _lastBuildingStatus = status;
                    PopulateBuildingsTab(status);
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Deferred dorf2 refresh skipped: {ex.Message}");
            }
        });
    }

    private void ScheduleDeferredResourcesMidWaitRefresh(QueueItem item, TimeSpan queueWaitDelay)
    {
        if (!IsResourceAwareQueueTask(item.TaskName) || queueWaitDelay.TotalSeconds < 3)
        {
            return;
        }

        var halfDelay = TimeSpan.FromSeconds(Math.Max(1, Math.Floor(queueWaitDelay.TotalSeconds / 2d)));
        var baseOptions = ApplySelectedVillageToOptions(LoadBotOptions());
        var itemOptions = BotOptionsPayloadApplier.Apply(baseOptions, item.Payload);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(halfDelay);
                var status = await _botService.ReadVillageResourceStatusAsync(itemOptions, AppendLog, null, null, CancellationToken.None);
                await Dispatcher.InvokeAsync(() =>
                {
                    ApplyResourceStatusToUi(status);
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Deferred dorf1 refresh skipped: {ex.Message}");
            }
        });
    }

    private void ApplyPostLoginSnapshot(PostLoginSnapshot snapshot)
    {
        var status = snapshot.VillageStatus;
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
        TriggerDeferredConstructionWaitRefresh(status, "post_login");

        _lastBuildingStatus = status;
        PopulateBuildingsTab(status);

        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        ResourcesInfoTextBlock.Text = $"Loaded {rows.Count} resource fields. Capital: {capitalText}. {BuildResourceForecastSummary(status)}";
        BuildingsInfoTextBlock.Text = $"Buildings loaded for active village '{status.ActiveVillage}'. Occupied slots: {_buildingRows.Count(row => row.IsOccupied)}, free slots: {_buildingRows.Count(row => !row.IsOccupied)}.";
        TribeInfoTextBlock.Text = $"Tribe: {status.Tribe}";
        VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
        RefreshVillagePickerFromVillages(status.Villages, status.ActiveVillage);
        UpdateDashboardVillageList(status.Villages);
        UpdateInboxButtons(snapshot.InboxStatus.UnreadMessages, snapshot.InboxStatus.UnreadReports);
        ApplyFarmingAvailabilityFromGoldClubStatus(TryGetStoredGoldClubEnabled(_accountStore.ActiveAccountName()));

        if (snapshot.AdventureCount is null)
        {
            ApplyHeroAdventureAvailability(null);
            AppendLog("Adventure count: not found on current page.");
        }
        else
        {
            ApplyHeroAdventureAvailability(snapshot.AdventureCount.Value);
            AppendLog($"Adventure count after login: {snapshot.AdventureCount.Value}.");
        }
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
            SetEnabled(QueueRemoveButton, defaultEnabled);
            SetEnabled(QueueMoveUpButton, defaultEnabled);
            SetEnabled(QueueMoveDownButton, defaultEnabled);
            SetEnabled(QueueClearButton, defaultEnabled);
            SetEnabled(QueueRefreshButton, defaultEnabled);
            SetEnabled(ResetProgramButton, true);
            SetEnabled(LoadResourcesButton, defaultEnabled);
            SetEnabled(StorageRefreshButton, defaultEnabled && !_resourceSnapshotRefreshRunning);
            SetEnabled(ResourceTargetLevelComboBox, defaultEnabled);
            SetEnabled(UpgradeAllResourcesButton, defaultEnabled);
            SetEnabled(UpgradeAllResourcesToMaxButton, defaultEnabled);
            InboxPanelControl?.SetActionsEnabled(defaultEnabled);
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

    private void RefreshAccountPicker()
    {
        try
        {
            var active = _accountStore.ActiveAccountName();
            var accounts = _accountStore.ListAccounts()
                .OrderByDescending(item => string.Equals(item.Name, active, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

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
        await RefreshResourceSnapshotForUiAsync(options, cancellationToken);
    }

    private async Task<VillageStatus> ReadVillageStatusWithRetryAsync(BotOptions options, CancellationToken cancellationToken, bool resourceOnly = false, bool forceCurrentVillage = false, bool currentPageOnly = false)
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
                status = await ReadVillageStatusAsync(options, cancellationToken, resourceOnly, forceCurrentVillage, currentPageOnly);
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
        return await ReadVillageStatusAsync(options, cancellationToken, resourceOnly, forceCurrentVillage, currentPageOnly);
    }

    private Task<VillageStatus> ReadVillageStatusAsync(BotOptions options, CancellationToken cancellationToken, bool resourceOnly, bool forceCurrentVillage = false, bool currentPageOnly = false)
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
                cancellationToken,
                currentPageOnly);
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

    private bool TryExtractDeferredUpgradePayload(string message, Dictionary<string, string> basePayload, out Dictionary<string, string> updatedPayload)
    {
        updatedPayload = new Dictionary<string, string>(basePayload, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var changed = false;
        foreach (var key in DeferredUpgradePayloadKeys)
        {
            var match = Regex.Match(message, $@"(?<!\S){Regex.Escape(key)}=(?<value>\S+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            updatedPayload[key] = match.Groups["value"].Value.Trim();
            changed = true;
        }

        return changed;
    }

    private void TriggerDeferredConstructionWaitRefresh(VillageStatus status, string source)
    {
        if (_deferredConstructionRefreshRunning || status.Resources.Count == 0)
        {
            return;
        }

        _deferredConstructionRefreshRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshDeferredConstructionWaitsAsync(status, source);
            }
            catch (Exception ex)
            {
                AppendLog($"Deferred construction wait refresh skipped: {ex.Message}");
            }
            finally
            {
                _deferredConstructionRefreshRunning = false;
            }
        });
    }

    private async Task RefreshDeferredConstructionWaitsAsync(VillageStatus status, string source)
    {
        var currentResources = ReadCurrentResourcesFromStatus(status);
        var productionByHour = ReadCurrentProductionByHourFromStatus(status);
        var deferredItems = _botService
            .GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => IsConstructionQueueTask(item.TaskName))
            .ToList();

        foreach (var item in deferredItems)
        {
            if (!TryReadDeferredUpgradeRequirements(item.Payload, out var required))
            {
                continue;
            }

            var evaluation = EvaluateDeferredUpgradeWait(item.Payload, required, currentResources, productionByHour);
            var updatedPayload = new Dictionary<string, string>(item.Payload, StringComparer.OrdinalIgnoreCase);
            WriteDeferredUpgradeRuntimeValues(updatedPayload, currentResources, productionByHour, evaluation);
            var remainingSeconds = Math.Max(0, (int)Math.Ceiling((item.NextAttemptAt - DateTimeOffset.UtcNow).TotalSeconds));

            if (evaluation.ResourcesEnough)
            {
                if (remainingSeconds <= 1)
                {
                    continue;
                }

                var changed = _botService.UpdateDeferredQueueItem(item.Id, updatedPayload, TimeSpan.Zero);
                if (changed)
                {
                    AppendLog($"Deferred upgrade resumed from {source}: {DescribeDeferredUpgrade(item.Payload)} now has enough resources.");
                }
                continue;
            }

            if (Math.Abs(remainingSeconds - evaluation.WaitSeconds) <= 5)
            {
                continue;
            }

            var delay = TimeSpan.FromSeconds(evaluation.WaitSeconds);
            var updated = _botService.UpdateDeferredQueueItem(item.Id, updatedPayload, delay);
            if (updated)
            {
                AppendLog($"Deferred upgrade wait updated from {source}: {DescribeDeferredUpgrade(item.Payload)} wait={evaluation.WaitSeconds}s reason={evaluation.WaitReason}.");
            }
        }

        await Dispatcher.InvokeAsync(() => RefreshQueueUi());
    }

    private void TriggerDeferredTroopTrainingWaitRefresh(VillageStatus status, string source, bool force = false)
    {
        if (!force && status.Resources.Count == 0)
        {
            return;
        }

        if (_deferredTroopTrainingRefreshRunning)
        {
            _pendingDeferredTroopTrainingRefreshStatus = status;
            _pendingDeferredTroopTrainingRefreshSource = source;
            return;
        }

        _deferredTroopTrainingRefreshRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshDeferredTroopTrainingWaitsAsync(status, source);
            }
            catch (Exception ex)
            {
                AppendLog($"Deferred troop training wait refresh skipped: {ex.Message}");
            }
            finally
            {
                _deferredTroopTrainingRefreshRunning = false;
                if (_pendingDeferredTroopTrainingRefreshStatus is not null)
                {
                    var pendingStatus = _pendingDeferredTroopTrainingRefreshStatus;
                    var pendingSource = _pendingDeferredTroopTrainingRefreshSource ?? "pending_refresh";
                    _pendingDeferredTroopTrainingRefreshStatus = null;
                    _pendingDeferredTroopTrainingRefreshSource = null;
                    TriggerDeferredTroopTrainingWaitRefresh(pendingStatus, pendingSource, force: true);
                }
            }
        });
    }

    private async Task RefreshDeferredTroopTrainingWaitsAsync(VillageStatus status, string source)
    {
        var currentResources = ReadCurrentResourcesFromStatus(status);
        var productionByHour = ReadCurrentProductionByHourFromStatus(status);
        var warehouseCapacity = status.WarehouseCapacity;
        var granaryCapacity = status.GranaryCapacity;
        if (warehouseCapacity is not > 0 || granaryCapacity is not > 0)
        {
            return;
        }

        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var fallbackCooldownSeconds = ResolveTroopTrainingFallbackCooldownSeconds(options.TroopTrainingFallbackCooldownSeconds);
        var deferredItems = _botService
            .GetQueueItemsForDisplay()
            .Where(item => item.Status == QueueStatus.Pending)
            .Where(item => string.Equals(item.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (deferredItems.Count == 0)
        {
            return;
        }

        var requests = BuildDeferredTroopTrainingRequests(options);
        var knownBuildings = _lastBuildingStatus?.Buildings ?? [];
        foreach (var item in deferredItems)
        {
            var evaluation = EvaluateDeferredTroopTrainingWait(
                requests,
                knownBuildings,
                currentResources,
                productionByHour,
                warehouseCapacity.Value,
                granaryCapacity.Value,
                fallbackCooldownSeconds);
            var remainingSeconds = Math.Max(0, (int)Math.Ceiling((item.NextAttemptAt - DateTimeOffset.UtcNow).TotalSeconds));
            if (evaluation.Ready)
            {
                if (remainingSeconds <= 1)
                {
                    continue;
                }

                if (_botService.UpdateDeferredQueueItem(item.Id, item.Payload, TimeSpan.Zero))
                {
                    AppendLog($"Deferred troop training resumed from {source}: resources now satisfy a % limit.");
                }

                continue;
            }

            if (Math.Abs(remainingSeconds - evaluation.WaitSeconds) <= 5)
            {
                continue;
            }

            if (_botService.UpdateDeferredQueueItem(item.Id, item.Payload, TimeSpan.FromSeconds(evaluation.WaitSeconds)))
            {
                AppendLog($"Deferred troop training wait updated from {source}: wait={evaluation.WaitSeconds}s reason={evaluation.WaitReason}.");
            }
        }

        await Dispatcher.InvokeAsync(() => RefreshQueueUi());
    }

    private static Dictionary<string, long> ReadCurrentResourcesFromStatus(VillageStatus status)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            status.Resources.TryGetValue(key, out var raw);
            result[key] = TryParseDesktopResourceValue(raw) ?? 0;
        }

        return result;
    }

    private static Dictionary<string, double?> ReadCurrentProductionByHourFromStatus(VillageStatus status)
    {
        var result = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            result[key] = status.ResourceStorageForecasts?
                .FirstOrDefault(item => string.Equals(item.ResourceKey, key, StringComparison.OrdinalIgnoreCase))
                ?.ProductionPerHour;
        }

        return result;
    }

    private sealed record DeferredTroopTrainingRequest(
        string BuildingName,
        bool Enabled,
        string RunMode,
        int MinimumResourcesPercent,
        bool CheckWood,
        bool CheckClay,
        bool CheckIron,
        bool CheckCrop);

    private sealed record DeferredTroopTrainingEvaluation(
        bool Ready,
        int WaitSeconds,
        string WaitReason);

    private static IReadOnlyList<DeferredTroopTrainingRequest> BuildDeferredTroopTrainingRequests(BotOptions options)
    {
        return
        [
            new DeferredTroopTrainingRequest("Barracks", options.TroopTrainingBarracksEnabled, options.TroopTrainingBarracksRunMode, options.TroopTrainingBarracksMinimumResourcesPercent, options.TroopTrainingBarracksCheckWood, options.TroopTrainingBarracksCheckClay, options.TroopTrainingBarracksCheckIron, options.TroopTrainingBarracksCheckCrop),
            new DeferredTroopTrainingRequest("Stable", options.TroopTrainingStableEnabled, options.TroopTrainingStableRunMode, options.TroopTrainingStableMinimumResourcesPercent, options.TroopTrainingStableCheckWood, options.TroopTrainingStableCheckClay, options.TroopTrainingStableCheckIron, options.TroopTrainingStableCheckCrop),
            new DeferredTroopTrainingRequest("Workshop", options.TroopTrainingWorkshopEnabled, options.TroopTrainingWorkshopRunMode, options.TroopTrainingWorkshopMinimumResourcesPercent, options.TroopTrainingWorkshopCheckWood, options.TroopTrainingWorkshopCheckClay, options.TroopTrainingWorkshopCheckIron, options.TroopTrainingWorkshopCheckCrop),
        ];
    }

    private static DeferredTroopTrainingEvaluation EvaluateDeferredTroopTrainingWait(
        IReadOnlyList<DeferredTroopTrainingRequest> requests,
        IReadOnlyList<Building> knownBuildings,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        long warehouseCapacity,
        long granaryCapacity,
        int fallbackCooldownSeconds)
    {
        var enabledRequests = requests
            .Where(item => item.Enabled)
            .Where(item => string.Equals(item.RunMode, "resource_percent", StringComparison.OrdinalIgnoreCase))
            .Where(item => knownBuildings.Count == 0 || knownBuildings.Any(building =>
                string.Equals(building.Name, item.BuildingName, StringComparison.OrdinalIgnoreCase)
                || (item.BuildingName == "Barracks" && (building.Gid ?? 0) == 19)
                || (item.BuildingName == "Stable" && (building.Gid ?? 0) == 20)
                || (item.BuildingName == "Workshop" && (building.Gid ?? 0) == 21)))
            .ToList();
        if (enabledRequests.Count == 0)
        {
            return new DeferredTroopTrainingEvaluation(false, fallbackCooldownSeconds, "fallback_cooldown");
        }

        var shortestWait = int.MaxValue;
        var waitReason = "fallback_cooldown";
        foreach (var request in enabledRequests)
        {
            var selectedKeys = ResolveDeferredTroopTrainingResourceKeys(request);
            var meetsThreshold = true;
            var requestWait = 0;
            var requestReason = "fallback_cooldown";
            foreach (var key in selectedKeys)
            {
                var capacity = string.Equals(key, "crop", StringComparison.OrdinalIgnoreCase)
                    ? granaryCapacity
                    : warehouseCapacity;
                var thresholdPercent = Math.Clamp(request.MinimumResourcesPercent, 0, 100);
                if (thresholdPercent <= 0)
                {
                    continue;
                }

                var threshold = (long)Math.Ceiling(capacity * (thresholdPercent / 100d));
                currentResources.TryGetValue(key, out var currentValue);
                var missing = Math.Max(0L, threshold - currentValue);
                if (missing <= 0)
                {
                    continue;
                }

                meetsThreshold = false;
                productionByHour.TryGetValue(key, out var productionValue);
                if (productionValue > 0)
                {
                    var perResourceWait = Math.Max(1, (int)Math.Ceiling((missing / productionValue.Value) * 3600d));
                    requestWait = Math.Max(requestWait, perResourceWait);
                    requestReason = "estimated_from_status";
                }
                else
                {
                    requestWait = Math.Max(requestWait, fallbackCooldownSeconds);
                    requestReason = "recheck_needed";
                }
            }

            if (meetsThreshold)
            {
                return new DeferredTroopTrainingEvaluation(true, 0, "ready");
            }

            if (requestWait > 0 && requestWait < shortestWait)
            {
                shortestWait = requestWait;
                waitReason = requestReason;
            }
        }

        if (shortestWait == int.MaxValue)
        {
            shortestWait = fallbackCooldownSeconds;
        }

        return new DeferredTroopTrainingEvaluation(false, shortestWait, waitReason);
    }

    private static IReadOnlyList<string> ResolveDeferredTroopTrainingResourceKeys(DeferredTroopTrainingRequest request)
    {
        var keys = new List<string>();
        if (request.CheckWood)
        {
            keys.Add("wood");
        }

        if (request.CheckClay)
        {
            keys.Add("clay");
        }

        if (request.CheckIron)
        {
            keys.Add("iron");
        }

        if (request.CheckCrop)
        {
            keys.Add("crop");
        }

        return keys.Count > 0 ? keys : ["wood", "clay", "iron", "crop"];
    }

    private static int ResolveTroopTrainingFallbackCooldownSeconds(int configuredSeconds)
    {
        return configuredSeconds switch
        {
            10 or 30 or 60 or 120 or 300 or 600 => configuredSeconds,
            _ => 30,
        };
    }

    private static bool TryReadDeferredUpgradeRequirements(IReadOnlyDictionary<string, string> payload, out Dictionary<string, long> required)
    {
        required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var found = false;
        foreach (var pair in DeferredRequirementKeys)
        {
            if (!payload.TryGetValue(pair.Value, out var raw) || !long.TryParse(raw, out var value))
            {
                continue;
            }

            required[pair.Key] = value;
            found = true;
        }

        return found;
    }

    private static DeferredUpgradeEvaluation EvaluateDeferredUpgradeWait(
        IReadOnlyDictionary<string, string> payload,
        IReadOnlyDictionary<string, long> required,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> liveProductionByHour)
    {
        var resourcesEnough = true;
        var longestFiniteWait = 0;
        var hasUnknownWait = false;

        foreach (var key in new[] { "wood", "clay", "iron", "crop" })
        {
            required.TryGetValue(key, out var requiredValue);
            currentResources.TryGetValue(key, out var currentValue);
            var missing = Math.Max(0, requiredValue - currentValue);
            if (missing <= 0)
            {
                continue;
            }

            resourcesEnough = false;
            liveProductionByHour.TryGetValue(key, out var liveProduction);
            var production = liveProduction ?? ReadStoredProductionValue(payload, key);
            if (production > 0)
            {
                var waitSeconds = (int)Math.Ceiling((missing / production.Value) * 3600d);
                longestFiniteWait = Math.Max(longestFiniteWait, Math.Max(1, waitSeconds));
                continue;
            }

            hasUnknownWait = true;
        }

        if (resourcesEnough)
        {
            return new DeferredUpgradeEvaluation(true, 0, "resources_ready");
        }

        var wait = longestFiniteWait > 0 ? longestFiniteWait : 60;
        if (hasUnknownWait)
        {
            wait = Math.Max(30, Math.Min(wait, 60));
        }

        return new DeferredUpgradeEvaluation(false, wait, hasUnknownWait ? "recheck_needed" : "estimated_from_status");
    }

    private static void WriteDeferredUpgradeRuntimeValues(
        Dictionary<string, string> payload,
        IReadOnlyDictionary<string, long> currentResources,
        IReadOnlyDictionary<string, double?> productionByHour,
        DeferredUpgradeEvaluation evaluation)
    {
        foreach (var pair in DeferredCurrentKeys)
        {
            if (currentResources.TryGetValue(pair.Key, out var current))
            {
                payload[pair.Value] = current.ToString();
            }
        }

        foreach (var pair in DeferredProductionKeys)
        {
            if (productionByHour.TryGetValue(pair.Key, out var production) && production.HasValue)
            {
                payload[pair.Value] = production.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        payload[BotOptionPayloadKeys.UpgradeWaitSeconds] = evaluation.WaitSeconds.ToString();
        payload[BotOptionPayloadKeys.UpgradeWaitReason] = evaluation.WaitReason;
    }

    private static double? ReadStoredProductionValue(IReadOnlyDictionary<string, string> payload, string resourceKey)
    {
        if (!DeferredProductionKeys.TryGetValue(resourceKey, out var key))
        {
            return null;
        }

        if (!payload.TryGetValue(key, out var raw))
        {
            return null;
        }

        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long? TryParseDesktopResourceValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = raw.Replace("\u00a0", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();
        return long.TryParse(cleaned, out var value) ? value : null;
    }

    private static string DescribeDeferredUpgrade(IReadOnlyDictionary<string, string> payload)
    {
        if (payload.TryGetValue(BotOptionPayloadKeys.UpgradeBlockedLabel, out var blockedLabel) && !string.IsNullOrWhiteSpace(blockedLabel))
        {
            return blockedLabel.Replace('_', ' ');
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeName, out var buildingName) && !string.IsNullOrWhiteSpace(buildingName))
        {
            return buildingName;
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.ResourceUpgradeName, out var resourceName) && !string.IsNullOrWhiteSpace(resourceName))
        {
            return resourceName;
        }

        return "upgrade";
    }

    private sealed record DeferredUpgradeEvaluation(bool ResourcesEnough, int WaitSeconds, string WaitReason);

    private static readonly string[] DeferredUpgradePayloadKeys =
    [
        BotOptionPayloadKeys.UpgradeBlockedLabel,
        BotOptionPayloadKeys.UpgradeRequiredWood,
        BotOptionPayloadKeys.UpgradeRequiredClay,
        BotOptionPayloadKeys.UpgradeRequiredIron,
        BotOptionPayloadKeys.UpgradeRequiredCrop,
        BotOptionPayloadKeys.UpgradeCurrentWood,
        BotOptionPayloadKeys.UpgradeCurrentClay,
        BotOptionPayloadKeys.UpgradeCurrentIron,
        BotOptionPayloadKeys.UpgradeCurrentCrop,
        BotOptionPayloadKeys.UpgradeProductionWood,
        BotOptionPayloadKeys.UpgradeProductionClay,
        BotOptionPayloadKeys.UpgradeProductionIron,
        BotOptionPayloadKeys.UpgradeProductionCrop,
        BotOptionPayloadKeys.UpgradeWaitSeconds,
        BotOptionPayloadKeys.UpgradeWaitReason,
    ];

    private static readonly IReadOnlyDictionary<string, string> DeferredRequirementKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = BotOptionPayloadKeys.UpgradeRequiredWood,
        ["clay"] = BotOptionPayloadKeys.UpgradeRequiredClay,
        ["iron"] = BotOptionPayloadKeys.UpgradeRequiredIron,
        ["crop"] = BotOptionPayloadKeys.UpgradeRequiredCrop,
    };

    private static readonly IReadOnlyDictionary<string, string> DeferredCurrentKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = BotOptionPayloadKeys.UpgradeCurrentWood,
        ["clay"] = BotOptionPayloadKeys.UpgradeCurrentClay,
        ["iron"] = BotOptionPayloadKeys.UpgradeCurrentIron,
        ["crop"] = BotOptionPayloadKeys.UpgradeCurrentCrop,
    };

    private static readonly IReadOnlyDictionary<string, string> DeferredProductionKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = BotOptionPayloadKeys.UpgradeProductionWood,
        ["clay"] = BotOptionPayloadKeys.UpgradeProductionClay,
        ["iron"] = BotOptionPayloadKeys.UpgradeProductionIron,
        ["crop"] = BotOptionPayloadKeys.UpgradeProductionCrop,
    };

    private async Task RefreshConstructionStatusAsync(CancellationToken cancellationToken)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly: false);
        await Dispatcher.InvokeAsync(() =>
        {
            _lastBuildingStatus = status;
            ApplyVillageStatusToUi(status);
            PopulateBuildingsTab(status);
        });
    }

    private static bool NeedsConstructionStatusRefresh(string taskName)
    {
        return IsResourceUpgradeTask(taskName)
            || IsBuildingMutationTask(taskName);
    }

    private static bool IsConstructionQueueTask(string taskName)
    {
        return IsResourceUpgradeTask(taskName)
            || IsBuildingMutationTask(taskName);
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
            var status = await ReadVillageStatusWithRetryAsync(options, CancellationToken.None, resourceOnly: false);
            await Dispatcher.InvokeAsync(() =>
            {
                _lastBuildingStatus = status;
                ApplyVillageStatusToUi(status);
                PopulateBuildingsTab(status);
            });

            return !HasSmithyInVillageStatus(status);
        }
        catch (Exception ex)
        {
            AppendLog($"Smithy verification after blocked read failed: {ex.Message}");
            return null;
        }
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

    private void ApplyStartLoopButtonVisual(string startButtonText)
    {
        StartLoopButton.Content = startButtonText;

        var highlightPauseState = string.Equals(startButtonText, "Pause bot", StringComparison.Ordinal);
        if (highlightPauseState)
        {
            StartLoopButton.Background = new SolidColorBrush(Color.FromRgb(253, 230, 138));
            StartLoopButton.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            StartLoopButton.Foreground = new SolidColorBrush(Color.FromRgb(120, 53, 15));
            return;
        }

        StartLoopButton.Background = new SolidColorBrush(Color.FromRgb(3, 8, 38));
        StartLoopButton.BorderBrush = new SolidColorBrush(Color.FromRgb(3, 8, 38));
        StartLoopButton.Foreground = Brushes.White;
    }

    private void SetLoopStateBadge(string stateText, Color color, string startButtonText)
    {
        LoopStateTextBlock.Text = $"State: {stateText}";
        LoopStateBadge.Background = new SolidColorBrush(color);
        ApplyStartLoopButtonVisual(startButtonText);
    }

    private void UpdateExecutionStateIndicator()
    {
        UpdateAutomationLoopRunningIndicators();

        var loopRunning = _loopTask is not null && !_loopTask.IsCompleted;
        var hasPausedQueueItems = false;
        var hasRunningQueueItems = false;
        var hasFailedQueueItems = false;
        var hasDeferredQueueItems = false;
        var hasReadyQueueItems = false;
        DateTimeOffset? earliestNextAttemptUtc = null;
        var enabledGroups = GetContinuousLoopEnabledGroupsInOrder().ToHashSet();
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var queueItems = _botService.GetQueueItemsForDisplay();
            var relevantQueueItems = enabledGroups.Count > 0
                ? queueItems.Where(item => enabledGroups.Contains(item.Group)).ToList()
                : [];
            hasPausedQueueItems = relevantQueueItems.Any(item => item.Status == QueueStatus.Paused);
            hasRunningQueueItems = relevantQueueItems.Any(item => item.Status == QueueStatus.Running);
            hasFailedQueueItems = relevantQueueItems.Any(item => item.Status == QueueStatus.Failed);
            hasReadyQueueItems = relevantQueueItems.Any(item =>
                item.Status == QueueStatus.Pending &&
                item.NextAttemptAt <= nowUtc);
            var deferredItems = relevantQueueItems
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
        var continuousModeEnabled = ContinuousRunToggleButton?.IsChecked == true;
        var continuousModeActive = continuousModeEnabled && (loopRunning || _autoQueueRunning || hasDeferredQueueItems || hasInlineWait || functionExecutionRunning);

        if (!continuousModeEnabled)
        {
            if ((hasDeferredQueueItems && !hasRunningQueueItems && !_uiBusy && !functionExecutionRunning) || hasInlineWait)
            {
                var remainingSeconds = hasInlineWait
                    ? (int)Math.Ceiling((_inlineWaitUntilUtc - nowForWait).TotalSeconds)
                    : earliestNextAttemptUtc.HasValue
                        ? (int)Math.Ceiling((earliestNextAttemptUtc.Value - nowForWait).TotalSeconds)
                        : 0;
                remainingSeconds = Math.Max(0, remainingSeconds);
                LoopStateTextBlock.Text = remainingSeconds > 0
                    ? $"State: waiting ({FormatCountdown(remainingSeconds)})"
                    : "State: waiting";
                LoopStateBadge.Background = new SolidColorBrush(Color.FromRgb(202, 138, 4));
                ApplyStartLoopButtonVisual((loopRunning || _autoQueueRunning) ? "Pause bot" : "Start bot");
                return;
            }

            if (functionExecutionRunning)
            {
                SetLoopStateBadge("function running", Color.FromRgb(37, 99, 235), "Pause bot");
                return;
            }

            if (loopRunning)
            {
                SetLoopStateBadge("loop running", Color.FromRgb(22, 163, 74), "Pause bot");
                return;
            }

            if (hasPausedQueueItems)
            {
                SetLoopStateBadge("paused", Color.FromRgb(217, 119, 6), "Start bot");
                return;
            }

            SetLoopStateBadge("idle", Color.FromRgb(107, 114, 128), "Start bot");
            return;
        }

        if ((continuousModeActive || hasInlineWait || hasDeferredQueueItems)
            && !hasReadyQueueItems
            && !functionExecutionRunning
            && !hasRunningQueueItems
            && !_uiBusy)
        {
            if (hasInlineWait || hasDeferredQueueItems)
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

                _ = Math.Max(0, remainingSeconds);
                SetLoopStateBadge("waiting", Color.FromRgb(202, 138, 4), (loopRunning || _autoQueueRunning) ? "Pause bot" : "Start bot");
                return;
            }

            if (continuousModeActive)
            {
                SetLoopStateBadge("idle", Color.FromRgb(107, 114, 128), "Pause bot");
                return;
            }
        }

        if (continuousModeActive)
        {
            SetLoopStateBadge("running", Color.FromRgb(22, 163, 74), "Pause bot");
            return;
        }

        if (functionExecutionRunning)
        {
            SetLoopStateBadge("running", Color.FromRgb(22, 163, 74), "Pause bot");
            return;
        }

        if (loopRunning)
        {
            SetLoopStateBadge("running", Color.FromRgb(22, 163, 74), "Pause bot");
            return;
        }

        if (hasPausedQueueItems)
        {
            SetLoopStateBadge("paused", Color.FromRgb(217, 119, 6), "Start bot");
            return;
        }

        SetLoopStateBadge("idle", Color.FromRgb(107, 114, 128), "Start bot");
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

    private void UpdateAccountInfoLabel(string accountName)
    {
        ActiveAccountInfoTextBlock.Text = $"Account: {accountName} | Server: {ExtractServerSpeedLabel()}";
    }

    private void HeroViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressHeroHideModeApply)
        {
            return;
        }

        if (e.PropertyName is not (
            nameof(HeroViewModel.MinHpForAdventure)
            or nameof(HeroViewModel.AutoRevive)
            or nameof(HeroViewModel.AutoAssignPoints)
            or nameof(HeroViewModel.IsAdventurePickTop)
            or nameof(HeroViewModel.IsAdventurePickShortest)
            or nameof(HeroViewModel.IsHideModeFight)
            or nameof(HeroViewModel.IsHideModeHide)
            or nameof(HeroViewModel.ContinuousAdventures)))
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
        status = MergeResourceStatusForUi(status);
        UpdateActiveVillageResourceMaxLevel(status);
        _resourcesViewModel.ApplyStorageForecasts(status);
        VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
        TribeInfoTextBlock.Text = $"Tribe: {status.Tribe}";
        ApplyTroopTrainingTribeState(status.Tribe);
        LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}";
        var capitalText = status.IsCapital == true ? "Yes" : status.IsCapital == false ? "No" : "Unknown";
        var goldText = status.Gold?.ToString() ?? "-";
        var silverText = status.Silver?.ToString() ?? "-";
        ServerResourcesTextBlock.Text = $"Gold: {goldText} | Silver: {silverText}";

        _buildQueueActiveCount = status.ActiveBuildCount;
        _buildQueueRemainingSeconds = status.BuildQueueRemainingSeconds ?? -1;
        var hasDeferredConstructionQueueItem = false;
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            hasDeferredConstructionQueueItem = _botService
                .GetQueueItemsForDisplay()
                .Any(item =>
                    item.Group == QueueGroup.Construction
                    && item.Status == QueueStatus.Pending
                    && item.NextAttemptAt > nowUtc);
        }
        catch
        {
            // Ignore temporary queue read failures and keep the current inline wait state.
        }

        if (_buildQueueRemainingSeconds > 0 || (_buildQueueActiveCount <= 0 && !hasDeferredConstructionQueueItem))
        {
            _constructionInlineWaitUntilUtc = DateTimeOffset.MinValue;
        }

        _buildQueueReachedZeroPendingCompletion = false;
        ApplyTroopsAvailabilityFromVillageStatus(status);
        _troopTrainingViewModel.ApplyStatus(status, _lastBuildingStatus?.TroopTrainingQueues);
        ApplyLocalBreweryCelebrationStatus(status);
        UpdateBuildQueueStatusText();
        UpdateAutomationLoopRunningIndicators();
        RefreshVillagePicker(status);
    }

    private void RefreshVillagePicker(VillageStatus status)
    {
        var currentSelectedName = GetSelectedVillageName();
        var villages = BuildMergedVillageSelectionItems(status.Villages);

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

        DashboardVillageList.ItemsSource = villages
            .OrderByDescending(v => v.IsCapital)
            .ThenByDescending(v => v.Population ?? -1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        UpdateAutomationLoopRunningIndicators();
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

        var switchKey = $"{selectedVillage.Name}|{selectedVillage.Url}";
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(_lastVillageSwitchRefreshKey, switchKey, StringComparison.OrdinalIgnoreCase)
            && (now - _lastVillageSwitchRefreshAt).TotalSeconds < 2)
        {
            return;
        }

        _lastVillageSwitchRefreshKey = switchKey;
        _lastVillageSwitchRefreshAt = now;

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
        _villageSwitchCts = _loopController.CreateCts("village-switch");
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

            TribeInfoTextBlock.Text = $"Tribe: {status.Tribe}";
            VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
            RefreshVillagePickerFromVillages(status.Villages, selectedVillage.Name);
            UpdateDashboardVillageList(status.Villages);
            await RefreshResourceSnapshotForUiAsync(options, operationToken);

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

    private bool IsExecutionActiveForVillageChange()
    {
        return _uiBusy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted);
    }

    private async Task StopAndClearForVillageChangeAsync(string? villageName)
    {
        var label = string.IsNullOrWhiteSpace(villageName) ? "-" : villageName;
        AppendLog($"Village changed to '{label}' while bot is running. Stopping active work and clearing queue.");

        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
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
        _operationCts = _loopController.CreateCts("operation");
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
