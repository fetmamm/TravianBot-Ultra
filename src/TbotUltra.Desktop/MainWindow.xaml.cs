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
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
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
    private const int ContinuousLoopMaxSleepSliceSeconds = 5;
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
    private readonly ObservableCollection<string> _terminalEntries = [];
    private readonly ObservableCollection<AlarmEntryRow> _alarmEntries = [];
    private readonly ObservableCollection<LoopTaskOption> _automationLoopTasks = [];
    private readonly ObservableCollection<HeroAttributePriorityItem> _heroAttributePriorityItems = [];
    private readonly ObservableCollection<FarmListStatusRow> _farmLists = [];
    private readonly ObservableCollection<TroopTrainingBuildingOption> _troopTrainingBuildingOptions = [];
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
    private readonly HashSet<int> _buildingDemolishingSlots = new();
    private static readonly IReadOnlyDictionary<int, (double Left, double Top)> BuildingSlotLayoutById = CreateBuildingSlotLayout();

    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _operationCts;
    private DispatcherTimer? _heroCountdownTimer;
    private int _heroCountdownRemainingSeconds;
    private bool _suppressHeroHideModeApply;
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
    private bool _suppressTroopTrainingConfigWrite;
    private bool _suppressFarmListUiRefresh;
    private bool _farmingOperationBusy;
    private bool _natarsProfileAnalyzed;
    private DateTimeOffset _lastFarmListsAnalysisAt = DateTimeOffset.MinValue;
    private string _lastVillageSwitchRefreshKey = string.Empty;
    private DateTimeOffset _lastVillageSwitchRefreshAt = DateTimeOffset.MinValue;
    private VillageStatus? _lastBuildingStatus;
    private readonly object _pendingLogSync = new();
    private readonly Queue<string> _pendingLogMessages = new();
    private readonly object _sessionLogWriteSync = new();
    private readonly List<string> _sessionLogLines = [];
    private readonly List<string> _sessionAlarmLines = [];
    private bool _logFlushQueued;
    private bool _continuousLoopConstructionStatusNeedsSync = true;
    private bool _restartContinuousLoopAfterStop;
    private bool _startContinuousLoopAfterQueueStop;

    public ObservableCollection<ResourceFieldRow> WoodFields => _woodFields;
    public ObservableCollection<ResourceFieldRow> ClayFields => _clayFields;
    public ObservableCollection<ResourceFieldRow> IronFields => _ironFields;
    public ObservableCollection<ResourceFieldRow> CroplandFields => _croplandFields;
    public ObservableCollection<BuildingSlotRow> BuildingSlots => _buildingRows;
    public ObservableCollection<TroopTrainingBuildingOption> TroopTrainingBuildings => _troopTrainingBuildingOptions;

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
                TickTroopTrainingCountdowns();
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
        InitializeTroopTrainingBuildingOptions();
        InitializeBuildingSlotPlaceholders();
        _farmLists.CollectionChanged += (_, _) =>
        {
            SyncFarmListSelectionHandlers();
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
        LoadAutomationLoopTasks(options);
        LoadTroopTrainingConfigToUi(options);
        if (UpdateTroopTrainingTroopOptions(ResolveStoredTroopTrainingTribe()))
        {
            PersistTroopTrainingConfig();
        }
        TroopsInfoTextBlock.Text = "Configure troop building rules and refresh queues when needed.";
        HeroMinHpTextBox.Text = Math.Clamp(options.HeroMinHpForAdventure, 1, 100).ToString();
        HeroAutoReviveCheckBox.IsChecked = options.HeroAutoRevive;
        HeroAutoAssignPointsCheckBox.IsChecked = options.HeroAutoAssignPoints;
        LoadHeroPriorityToUi(options.HeroStatPriority);
        var topFirst = string.Equals(options.HeroAdventurePickOrder, "top", StringComparison.OrdinalIgnoreCase);
        HeroAdventureTopRadio.IsChecked = topFirst;
        HeroAdventureShortestRadio.IsChecked = !topFirst;
        var fightMode = string.Equals(options.HeroHideMode, "fight", StringComparison.OrdinalIgnoreCase);
        _suppressHeroHideModeApply = true;
        try
        {
            HeroFightRadio.IsChecked = fightMode;
            HeroHideRadio.IsChecked = !fightMode;
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
            TribeInfoTextBlock.Text = $"Tribe: {tribeMatch.Groups[1].Value}";
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
        PersistHeroPriorityToConfig();
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
        AppendLog(
            $"[ui-apply] free={snapshot.FreePoints} fight={snapshot.FightingStrength} off={snapshot.OffenceBonus} def={snapshot.DefenceBonus} res={snapshot.Resources}, items={_heroAttributePriorityItems.Count}, thread=" +
            (Dispatcher.CheckAccess() ? "ui" : "background"));

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

    private void PersistHeroPriorityToConfig()
    {
        try
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.HeroStatPriority] = BuildHeroPriorityPayload();
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save hero attribute priority: {ex.Message}");
        }
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

    private void InitializeTroopTrainingBuildingOptions()
    {
        if (_troopTrainingBuildingOptions.Count > 0)
        {
            return;
        }

        foreach (var option in new[]
                 {
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Barracks, Title = "Barracks" },
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Stable, Title = "Stable" },
                     new TroopTrainingBuildingOption { BuildingType = TroopTrainingBuildingType.Workshop, Title = "Workshop" },
                 })
        {
            option.PropertyChanged += TroopTrainingBuildingOption_PropertyChanged;
            _troopTrainingBuildingOptions.Add(option);
        }

        UpdateTroopTrainingTroopOptions(ResolveStoredTroopTrainingTribe());
        ResetTroopTrainingQueueStatus();
    }

    private void TroopTrainingBuildingOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressTroopTrainingConfigWrite)
        {
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.IsEnabled), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.SelectedTroop), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.MaxQueueMode), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.AmountMode), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.KeepResourcesPercent), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.RunMode), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.MinimumTroops), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(TroopTrainingBuildingOption.MinimumResourcesPercent), StringComparison.Ordinal))
        {
            return;
        }

        PersistTroopTrainingConfig();
        UpdateAutomationLoopRunningIndicators();
    }

    private string ResolveStoredTroopTrainingTribe()
    {
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
            // Ignore temporary analysis read failures.
        }

        return TribeInfoTextBlock?.Text?.Replace("Tribe:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim() ?? "Unknown";
    }

    private void LoadTroopTrainingConfigToUi(BotOptions options)
    {
        _suppressTroopTrainingConfigWrite = true;
        try
        {
            foreach (var option in _troopTrainingBuildingOptions)
            {
                switch (option.BuildingType)
                {
                    case TroopTrainingBuildingType.Barracks:
                        option.IsEnabled = options.TroopTrainingBarracksEnabled;
                        option.SelectedTroop = options.TroopTrainingBarracksTroopType;
                        option.MaxQueueMode = options.TroopTrainingBarracksMaxQueueHours;
                        option.AmountMode = options.TroopTrainingBarracksAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingBarracksKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingBarracksRunMode;
                        option.MinimumTroops = options.TroopTrainingBarracksMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingBarracksMinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Stable:
                        option.IsEnabled = options.TroopTrainingStableEnabled;
                        option.SelectedTroop = options.TroopTrainingStableTroopType;
                        option.MaxQueueMode = options.TroopTrainingStableMaxQueueHours;
                        option.AmountMode = options.TroopTrainingStableAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingStableKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingStableRunMode;
                        option.MinimumTroops = options.TroopTrainingStableMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingStableMinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Workshop:
                        option.IsEnabled = options.TroopTrainingWorkshopEnabled;
                        option.SelectedTroop = options.TroopTrainingWorkshopTroopType;
                        option.MaxQueueMode = options.TroopTrainingWorkshopMaxQueueHours;
                        option.AmountMode = options.TroopTrainingWorkshopAmountMode;
                        option.KeepResourcesPercent = options.TroopTrainingWorkshopKeepResourcesPercent;
                        option.RunMode = options.TroopTrainingWorkshopRunMode;
                        option.MinimumTroops = options.TroopTrainingWorkshopMinimumTroops;
                        option.MinimumResourcesPercent = options.TroopTrainingWorkshopMinimumResourcesPercent;
                        break;
                }
            }
        }
        finally
        {
            _suppressTroopTrainingConfigWrite = false;
        }
    }

    private void PersistTroopTrainingConfig()
    {
        try
        {
            var config = _botConfigStore.Load();
            foreach (var option in _troopTrainingBuildingOptions)
            {
                switch (option.BuildingType)
                {
                    case TroopTrainingBuildingType.Barracks:
                        config[BotOptionPayloadKeys.TroopTrainingBarracksEnabled] = option.IsEnabled;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksTroopType] = option.SelectedTroop;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksMaxQueueHours] = option.MaxQueueMode;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksAmountMode] = option.AmountMode;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksKeepResourcesPercent] = option.KeepResourcesPercent;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksRunMode] = option.RunMode;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksMinimumTroops] = option.MinimumTroops;
                        config[BotOptionPayloadKeys.TroopTrainingBarracksMinimumResourcesPercent] = option.MinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Stable:
                        config[BotOptionPayloadKeys.TroopTrainingStableEnabled] = option.IsEnabled;
                        config[BotOptionPayloadKeys.TroopTrainingStableTroopType] = option.SelectedTroop;
                        config[BotOptionPayloadKeys.TroopTrainingStableMaxQueueHours] = option.MaxQueueMode;
                        config[BotOptionPayloadKeys.TroopTrainingStableAmountMode] = option.AmountMode;
                        config[BotOptionPayloadKeys.TroopTrainingStableKeepResourcesPercent] = option.KeepResourcesPercent;
                        config[BotOptionPayloadKeys.TroopTrainingStableRunMode] = option.RunMode;
                        config[BotOptionPayloadKeys.TroopTrainingStableMinimumTroops] = option.MinimumTroops;
                        config[BotOptionPayloadKeys.TroopTrainingStableMinimumResourcesPercent] = option.MinimumResourcesPercent;
                        break;
                    case TroopTrainingBuildingType.Workshop:
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopEnabled] = option.IsEnabled;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopTroopType] = option.SelectedTroop;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopMaxQueueHours] = option.MaxQueueMode;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopAmountMode] = option.AmountMode;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopKeepResourcesPercent] = option.KeepResourcesPercent;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopRunMode] = option.RunMode;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopMinimumTroops] = option.MinimumTroops;
                        config[BotOptionPayloadKeys.TroopTrainingWorkshopMinimumResourcesPercent] = option.MinimumResourcesPercent;
                        break;
                }
            }

            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save troop training config: {ex.Message}");
        }
    }

    private bool UpdateTroopTrainingTroopOptions(string? tribe)
    {
        var configChanged = false;
        _suppressTroopTrainingConfigWrite = true;
        try
        {
            foreach (var option in _troopTrainingBuildingOptions)
            {
                var resolvedTroops = TroopCatalog.ResolveTroopTypesForTribe(tribe, option.BuildingType);
                var currentSelection = option.SelectedTroop;
                option.TroopOptions.Clear();
                foreach (var troop in resolvedTroops)
                {
                    option.TroopOptions.Add(troop);
                }

                if (resolvedTroops.Contains(currentSelection, StringComparer.OrdinalIgnoreCase))
                {
                    option.SelectedTroop = resolvedTroops.First(item => string.Equals(item, currentSelection, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var fallbackTroop = resolvedTroops.FirstOrDefault() ?? string.Empty;
                    if (!string.Equals(option.SelectedTroop, fallbackTroop, StringComparison.Ordinal))
                    {
                        configChanged = true;
                    }

                    option.SelectedTroop = fallbackTroop;
                }
            }
        }
        finally
        {
            _suppressTroopTrainingConfigWrite = false;
        }

        return configChanged;
    }

    private void ApplyTroopTrainingStatusToUi(VillageStatus status)
    {
        var queueStatuses = status.TroopTrainingQueues ?? _lastBuildingStatus?.TroopTrainingQueues;
        foreach (var option in _troopTrainingBuildingOptions)
        {
            var queueStatus = queueStatuses?.FirstOrDefault(item => item.BuildingType == option.BuildingType);
            if (queueStatus is not null)
            {
                option.Exists = queueStatus.Exists;
                option.QueueRemainingSeconds = queueStatus.RemainingSeconds;
                option.QueueStatusText = queueStatus.Exists
                    ? $"Queue: {queueStatus.RemainingText}"
                    : "Building not found";
                continue;
            }

            if (option.Exists)
            {
                if (string.IsNullOrWhiteSpace(option.QueueStatusText))
                {
                    option.QueueStatusText = "Queue not loaded.";
                }

                continue;
            }

            var buildingExists = status.Buildings.Any(item =>
                item.SlotId is > 0
                && ((option.BuildingType == TroopTrainingBuildingType.Barracks && (item.Gid ?? 0) == 19)
                    || (option.BuildingType == TroopTrainingBuildingType.Stable && (item.Gid ?? 0) == 20)
                    || (option.BuildingType == TroopTrainingBuildingType.Workshop && (item.Gid ?? 0) == 21)
                    || string.Equals(item.Name, option.Title, StringComparison.OrdinalIgnoreCase)));
            option.Exists = buildingExists;
            if (!buildingExists)
            {
                option.QueueRemainingSeconds = null;
                option.QueueStatusText = "Building not found";
            }
            else if (string.IsNullOrWhiteSpace(option.QueueStatusText))
            {
                option.QueueStatusText = "Queue not loaded.";
            }
        }
    }

    private void ResetTroopTrainingQueueStatus()
    {
        foreach (var option in _troopTrainingBuildingOptions)
        {
            option.Exists = false;
            option.QueueRemainingSeconds = null;
            option.QueueStatusText = "Queue not loaded.";
        }
    }

    private int? ResolveTroopTrainingGroupRemainingSeconds()
    {
        var enabled = _troopTrainingBuildingOptions
            .Where(item => item.IsEnabled && item.Exists && !string.IsNullOrWhiteSpace(item.SelectedTroop))
            .ToList();
        if (enabled.Count <= 0)
        {
            return null;
        }

        if (enabled.Any(item => (item.QueueRemainingSeconds ?? 0) <= 0))
        {
            return null;
        }

        return enabled
            .Select(item => item.QueueRemainingSeconds ?? 0)
            .Where(seconds => seconds > 0)
            .DefaultIfEmpty(0)
            .Min();
    }

    private async Task RefreshTroopTrainingQueuesAsync(
        BotOptions options,
        CancellationToken cancellationToken,
        IReadOnlyList<Building>? knownBuildings = null,
        bool refreshBuildingsBeforeRead = false)
    {
        IReadOnlyList<Building>? effectiveBuildings = knownBuildings;
        if (refreshBuildingsBeforeRead)
        {
            try
            {
                var refreshedStatus = await _botService.ReadBuildingsStatusAsync(options, AppendLog, cancellationToken);
                effectiveBuildings = refreshedStatus.Buildings;
                await Dispatcher.InvokeAsync(() =>
                {
                    _lastBuildingStatus = _lastBuildingStatus is null
                        ? refreshedStatus
                        : _lastBuildingStatus with
                        {
                            ActiveVillage = refreshedStatus.ActiveVillage,
                            Villages = refreshedStatus.Villages,
                            Tribe = refreshedStatus.Tribe,
                            Buildings = refreshedStatus.Buildings,
                            IsCapital = refreshedStatus.IsCapital,
                        };

                    ApplyTroopTrainingStatusToUi(_lastBuildingStatus);
                });
            }
            catch (Exception ex)
            {
                AppendLog($"Could not refresh troop building list before queue read: {ex.Message}");
            }
        }

        var queueStatuses = await _botService.ReadTroopTrainingQueuesAsync(options, AppendLog, effectiveBuildings, cancellationToken);
        await Dispatcher.InvokeAsync(() =>
        {
            var effectiveStatus = _lastBuildingStatus is null
                ? null
                : _lastBuildingStatus with { TroopTrainingQueues = queueStatuses };
            if (effectiveStatus is not null)
            {
                _lastBuildingStatus = effectiveStatus;
                ApplyTroopTrainingStatusToUi(effectiveStatus);
            }
            else
            {
                ApplyTroopTrainingStatusToUi(new VillageStatus(
                    ActiveVillage: string.Empty,
                    Villages: [],
                    Resources: new Dictionary<string, string>(),
                    ResourceFields: [],
                    Buildings: effectiveBuildings?.ToList() ?? [],
                    BuildQueue: [],
                    TroopTrainingQueues: queueStatuses));
            }

            UpdateAutomationLoopRunningIndicators();
        });
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
        var configured = (options.ContinuousLoopGroups ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (configured.Count <= 0)
        {
            configured = (options.LoopTasks ?? [])
                .Select(NormalizeLegacyLoopTaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(QueueGroupCatalog.ResolveGroup)
                .Select(QueueGroupCatalog.GetKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var orderedNames = LoadConfiguredContinuousLoopGroupOrder();
        var visibleGroups = LoadConfiguredDashboardVisibleGroups();

        _suppressAutomationLoopConfigWrite = true;
        try
        {
            _automationLoopTasks.Clear();
            foreach (var groupKey in orderedNames)
            {
                if (!QueueGroupCatalog.TryParse(groupKey, out var group))
                {
                    continue;
                }

                _automationLoopTasks.Add(new LoopTaskOption
                {
                    TaskName = groupKey,
                    Title = QueueGroupCatalog.GetTitle(group),
                    Description = QueueGroupCatalog.GetDescription(group),
                    IsEnabled = configured.Contains(groupKey, StringComparer.OrdinalIgnoreCase),
                    IsVisible = visibleGroups.Contains(groupKey, StringComparer.OrdinalIgnoreCase),
                    StateText = "Idle",
                    DetailText = "No queued task.",
                });
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
        var visibleCount = _automationLoopTasks.Count(item => item.IsVisible);
        AutomationLoopSummaryTextBlock.Text = enabledCount <= 0
            ? $"No group enabled. Visible on dashboard: {visibleCount}."
            : $"Continuous loop uses {enabledCount} enabled group(s). Visible on dashboard: {visibleCount}.";
        UpdateAutomationLoopColumns();
    }

    private void UpdateAutomationLoopColumns()
    {
        if (AutomationLoopListBox is null)
        {
            return;
        }

        var visibleCount = Math.Max(1, _automationLoopTasks.Count(item => item.IsVisible));
        var columns = visibleCount <= 4 ? 1 : 2;
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

    private QueueGroup? GetActiveContinuousLoopGroup()
    {
        var taskName = _activeAutomationTaskName;
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return null;
        }

        return QueueGroupCatalog.ResolveGroup(taskName);
    }

    private bool HasEnabledContinuousLoopGroupsExcept(QueueGroup excludedGroup)
    {
        return GetContinuousLoopEnabledGroupsInOrder().Any(group => group != excludedGroup);
    }

    private void StartContinuousLoopRunner()
    {
        var initialOptions = LoadBotOptions();
        _loopStopRequested = false;
        _queueStopRequested = false;
        _continuousLoopConstructionStatusNeedsSync = true;
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;

        StartLoopButton.Content = "Pause bot";
        StartLoopButton.IsEnabled = true;
        SetLoopIndicator(true);
        AppendLog($"Loop started. Interval={initialOptions.LoopIntervalSeconds}s");

        _loopTask = Task.Run(() => RunContinuousLoopAsync(token), token);
        _ = TrackLoopCompletionAsync(_loopTask);
    }

    private bool IsContinuousLoopGroupEnabled(QueueGroup group)
    {
        return GetContinuousLoopEnabledGroupsInOrder().Contains(group);
    }

    private IReadOnlyList<QueueItem> GetContinuousLoopRelevantQueueItems()
    {
        var enabledGroups = GetContinuousLoopEnabledGroupsInOrder().ToHashSet();
        if (enabledGroups.Count <= 0)
        {
            return [];
        }

        return _botService.GetQueueItemsForDisplay()
            .Where(item => enabledGroups.Contains(item.Group))
            .ToList();
    }

    private static IReadOnlyList<QueueItem> OrderContinuousLoopGroupItems(IEnumerable<QueueItem> items)
    {
        return items
            .OrderBy(item => item.IsRuntimeOnly)
            .ThenByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .ToList();
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

    private static bool TryExtractTroopsBlockedReason(string? message, out string reasonKey, out string reasonText)
    {
        reasonKey = string.Empty;
        reasonText = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.Trim();
        if (value.Contains("Smithy not found in this village", StringComparison.OrdinalIgnoreCase))
        {
            reasonKey = TroopsBlockedReasonSmithyMissing;
            reasonText = "Smithy missing";
            return true;
        }

        if (value.Contains("Smithy:", StringComparison.OrdinalIgnoreCase)
            && value.Contains("All done", StringComparison.OrdinalIgnoreCase))
        {
            reasonKey = TroopsBlockedReasonAllDone;
            reasonText = "All troops fully developed";
            return true;
        }

        return false;
    }

    private void SetTroopsBlockedState(string reasonKey, string reasonText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetTroopsBlockedState(reasonKey, reasonText));
            return;
        }

        var troopsOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Troops), StringComparison.OrdinalIgnoreCase));
        if (troopsOption is not null)
        {
            _troopsBlockedPreviouslyEnabled = troopsOption.IsEnabled;
            troopsOption.IsEnabled = false;
        }

        _troopsBlockedReasonKey = reasonKey;
        _troopsBlockedReasonText = reasonText;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void SetFarmingBlockedState(string reasonKey, string reasonText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetFarmingBlockedState(reasonKey, reasonText));
            return;
        }

        var farmingOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Farming), StringComparison.OrdinalIgnoreCase));
        if (farmingOption is not null)
        {
            _farmingBlockedPreviouslyEnabled = farmingOption.IsEnabled;
            farmingOption.IsEnabled = false;
        }

        _farmingBlockedReasonKey = reasonKey;
        _farmingBlockedReasonText = reasonText;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void SetHeroBlockedState(string reasonKey, string reasonText)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetHeroBlockedState(reasonKey, reasonText));
            return;
        }

        var heroOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase));
        if (heroOption is not null)
        {
            _heroBlockedPreviouslyEnabled = heroOption.IsEnabled;
            heroOption.IsEnabled = false;
        }

        _heroBlockedReasonKey = reasonKey;
        _heroBlockedReasonText = reasonText;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void ClearTroopsBlockedState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearTroopsBlockedState);
            return;
        }

        if (string.IsNullOrWhiteSpace(_troopsBlockedReasonKey) && string.IsNullOrWhiteSpace(_troopsBlockedReasonText))
        {
            return;
        }

        _troopsBlockedReasonKey = null;
        _troopsBlockedReasonText = null;
        var troopsOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Troops), StringComparison.OrdinalIgnoreCase));
        if (troopsOption is not null && _troopsBlockedPreviouslyEnabled)
        {
            troopsOption.IsEnabled = true;
        }

        _troopsBlockedPreviouslyEnabled = false;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void ClearFarmingBlockedState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearFarmingBlockedState);
            return;
        }

        if (string.IsNullOrWhiteSpace(_farmingBlockedReasonKey) && string.IsNullOrWhiteSpace(_farmingBlockedReasonText))
        {
            return;
        }

        _farmingBlockedReasonKey = null;
        _farmingBlockedReasonText = null;
        var farmingOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Farming), StringComparison.OrdinalIgnoreCase));
        if (farmingOption is not null && _farmingBlockedPreviouslyEnabled)
        {
            farmingOption.IsEnabled = true;
        }

        _farmingBlockedPreviouslyEnabled = false;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private void ClearHeroBlockedState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearHeroBlockedState);
            return;
        }

        if (string.IsNullOrWhiteSpace(_heroBlockedReasonKey) && string.IsNullOrWhiteSpace(_heroBlockedReasonText))
        {
            return;
        }

        _heroBlockedReasonKey = null;
        _heroBlockedReasonText = null;
        var heroOption = _automationLoopTasks.FirstOrDefault(item =>
            string.Equals(item.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase));
        if (heroOption is not null && _heroBlockedPreviouslyEnabled)
        {
            heroOption.IsEnabled = true;
        }

        _heroBlockedPreviouslyEnabled = false;
        UpdateAutomationLoopRunningIndicators();
        PersistAutomationLoopTasksToConfig();
    }

    private static bool HasSmithyInVillageStatus(VillageStatus status)
    {
        return status.Buildings.Any(item =>
            item.Gid == 12
            || string.Equals(item.Name, "Smithy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, "Blacksmith", StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyTroopsAvailabilityFromVillageStatus(VillageStatus status)
    {
        var hasSmithy = HasSmithyInVillageStatus(status);
        if (hasSmithy)
        {
            if (string.Equals(_troopsBlockedReasonKey, TroopsBlockedReasonSmithyMissing, StringComparison.OrdinalIgnoreCase))
            {
                ClearTroopsBlockedState();
                AppendLog("Troops group re-enabled: Smithy detected after building refresh.");
            }

            return;
        }

        if (string.Equals(_troopsBlockedReasonKey, TroopsBlockedReasonAllDone, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(_troopsBlockedReasonKey, TroopsBlockedReasonSmithyMissing, StringComparison.OrdinalIgnoreCase))
        {
            SetTroopsBlockedState(TroopsBlockedReasonSmithyMissing, "Smithy missing");
        }
    }

    private int? ResolveConstructionGroupRemainingSeconds()
    {
        var remainingSeconds = _buildQueueRemainingSeconds > 0 ? _buildQueueRemainingSeconds : 0;
        if (_constructionInlineWaitUntilUtc > DateTimeOffset.UtcNow)
        {
            var inlineSeconds = (int)Math.Ceiling((_constructionInlineWaitUntilUtc - DateTimeOffset.UtcNow).TotalSeconds);
            remainingSeconds = Math.Max(remainingSeconds, Math.Max(0, inlineSeconds));
        }

        return remainingSeconds > 0 ? remainingSeconds : null;
    }

    private bool IsConstructionGroupReady()
    {
        return ResolveConstructionGroupRemainingSeconds() is not > 0;
    }

    private void ApplyConstructionInlineWait(TimeSpan waitDelay)
    {
        if (waitDelay <= TimeSpan.Zero)
        {
            return;
        }

        var waitUntilUtc = DateTimeOffset.UtcNow.Add(waitDelay);
        if (waitUntilUtc <= _constructionInlineWaitUntilUtc)
        {
            return;
        }

        _constructionInlineWaitUntilUtc = waitUntilUtc;
        UpdateAutomationLoopRunningIndicators();
    }

    private bool IsTroopsGroupBlocked()
    {
        return !string.IsNullOrWhiteSpace(_troopsBlockedReasonKey);
    }

    private bool IsFarmingGroupBlocked()
    {
        return !string.IsNullOrWhiteSpace(_farmingBlockedReasonKey);
    }

    private bool IsHeroGroupBlocked()
    {
        return !string.IsNullOrWhiteSpace(_heroBlockedReasonKey);
    }

    private void ApplyHeroAdventureAvailability(int? count)
    {
        if (count is null)
        {
            HeroAdventureCountTextBlock.Text = "?";
            return;
        }

        HeroAdventureCountTextBlock.Text = count.Value.ToString();
        if (count.Value > 0)
        {
            ClearHeroBlockedState();
            return;
        }

        if (!string.Equals(_heroBlockedReasonKey, HeroBlockedReasonNoAdventures, StringComparison.OrdinalIgnoreCase))
        {
            SetHeroBlockedState(HeroBlockedReasonNoAdventures, "No adventures");
        }
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
        IReadOnlyList<QueueItem> queueItems = [];
        try
        {
            queueItems = _botService.GetQueueItemsForDisplay();
            hasPausedQueueItems = queueItems.Any(item => item.Status == QueueStatus.Paused);
            queueRunningTaskName = queueItems.FirstOrDefault(item => item.Status == QueueStatus.Running)?.TaskName;
        }
        catch
        {
            // Ignore temporary queue read failures.
        }

        var runningTaskName = !string.IsNullOrWhiteSpace(queueRunningTaskName)
            ? queueRunningTaskName
            : isRunning
                ? _activeAutomationTaskName
                : null;

        var runningGroup = string.IsNullOrWhiteSpace(runningTaskName)
            ? (QueueGroup?)null
            : QueueGroupCatalog.ResolveGroup(runningTaskName);

        foreach (var item in _automationLoopTasks)
        {
            if (!QueueGroupCatalog.TryParse(item.TaskName, out var group))
            {
                item.IsRunning = false;
                item.StateText = "Idle";
                item.DetailText = "Unknown group.";
                item.QueuedCount = 0;
                item.RemainingSeconds = null;
                continue;
            }

            var groupItems = queueItems.Where(entry => entry.Group == group).ToList();
            var pendingCount = groupItems.Count(entry => entry.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused);
            var deferred = groupItems
                .Where(entry => entry.Status == QueueStatus.Pending && entry.NextAttemptAt > DateTimeOffset.UtcNow)
                .OrderBy(entry => entry.NextAttemptAt)
                .FirstOrDefault();
            var runningItem = groupItems.FirstOrDefault(entry => entry.Status == QueueStatus.Running);
            var paused = groupItems.Any(entry => entry.Status == QueueStatus.Paused);
            var constructionWaitSeconds = group == QueueGroup.Construction
                ? ResolveConstructionGroupRemainingSeconds()
                : (int?)null;
            var troopTrainingWaitSeconds = group == QueueGroup.TroopTraining
                ? ResolveTroopTrainingGroupRemainingSeconds()
                : (int?)null;

            item.QueuedCount = pendingCount;
            item.IsRunning = runningGroup.HasValue && runningGroup.Value == group;
            item.IsBlocked = false;
            item.BlockedText = "Blocked";
            if (runningItem is not null || item.IsRunning)
            {
                item.StateText = "Running";
                item.DetailText = runningItem is not null
                    ? BuildQueueDisplayName(runningItem)
                    : "Coordinator active.";
                item.RemainingSeconds = null;
            }
            else if (deferred is not null || constructionWaitSeconds is > 0 || troopTrainingWaitSeconds is > 0)
            {
                item.StateText = "Waiting";
                if (deferred is not null)
                {
                    item.DetailText = $"Next try {FormatQueueServerTime(deferred.NextAttemptAt)}";
                    item.RemainingSeconds = Math.Max(0, (int)Math.Ceiling((deferred.NextAttemptAt - DateTimeOffset.UtcNow).TotalSeconds));
                }
                else if (troopTrainingWaitSeconds is > 0)
                {
                    item.DetailText = "Troop queue active.";
                    item.RemainingSeconds = troopTrainingWaitSeconds;
                }
                else
                {
                    item.DetailText = "Build queue active.";
                    item.RemainingSeconds = constructionWaitSeconds;
                }
            }
            else if (group == QueueGroup.Troops && IsTroopsGroupBlocked())
            {
                item.StateText = "Blocked";
                item.DetailText = _troopsBlockedReasonText ?? "Troops group blocked.";
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = _troopsBlockedReasonText ?? "Blocked";
            }
            else if (group == QueueGroup.Farming && IsFarmingGroupBlocked())
            {
                item.StateText = "Blocked";
                item.DetailText = _farmingBlockedReasonText ?? "Farming group blocked.";
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = _farmingBlockedReasonText ?? "Blocked";
            }
            else if (group == QueueGroup.Hero && IsHeroGroupBlocked())
            {
                item.StateText = "Blocked";
                item.DetailText = _heroBlockedReasonText ?? "Hero group blocked.";
                item.RemainingSeconds = null;
                item.IsBlocked = true;
                item.BlockedText = _heroBlockedReasonText ?? "Blocked";
            }
            else if (!item.IsEnabled)
            {
                item.StateText = "Disabled";
                item.DetailText = pendingCount > 0 ? $"{pendingCount} queued." : "No queued task.";
                item.RemainingSeconds = null;
            }
            else if (paused)
            {
                item.StateText = "Paused";
                item.DetailText = "Contains paused task.";
                item.RemainingSeconds = null;
            }
            else
            {
                item.StateText = item.IsEnabled ? "Idle" : "Disabled";
                item.DetailText = pendingCount > 0 ? $"{pendingCount} queued." : "No queued task.";
                item.RemainingSeconds = null;
            }
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
            var enabledGroupNames = _automationLoopTasks
                .Where(item => item.IsEnabled)
                .Select(item => item.TaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            var orderedGroupNames = _automationLoopTasks
                .Select(item => item.TaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            var visibleGroupNames = _automationLoopTasks
                .Where(item => item.IsVisible)
                .Select(item => item.TaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            var config = _botConfigStore.Load();
            config["continuous_loop_groups"] = new JsonArray(enabledGroupNames.Select(name => JsonValue.Create(name)!).ToArray());
            config[ContinuousLoopGroupOrderConfigKey] = new JsonArray(orderedGroupNames.Select(name => JsonValue.Create(name)!).ToArray());
            config[DashboardVisibleGroupsConfigKey] = new JsonArray(visibleGroupNames.Select(name => JsonValue.Create(name)!).ToArray());

            var existingLoopTasks = config["loop_tasks"] as JsonArray ?? new JsonArray();
            var normalizedLoopTasks = existingLoopTasks
                .Select(node => NormalizeLegacyLoopTaskName(node?.ToString()))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedLoopTasks.Count > 0)
            {
                config["loop_tasks"] = new JsonArray(normalizedLoopTasks.Select(name => JsonValue.Create(name)!).ToArray());
            }

            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save continuous loop groups: {ex.Message}");
        }
    }

    private List<string> LoadConfiguredDashboardVisibleGroups()
    {
        try
        {
            var config = _botConfigStore.Load();
            var configuredVisible = (config[DashboardVisibleGroupsConfigKey] as JsonArray ?? new JsonArray())
                .Select(node => node?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Where(name => QueueGroupCatalog.TryParse(name, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (configuredVisible.Count > 0)
            {
                return configuredVisible;
            }
        }
        catch
        {
            // Ignore read errors and fall back to all visible.
        }

        return QueueGroupCatalog.AllGroups
            .Select(QueueGroupCatalog.GetKey)
            .ToList();
    }

    private List<string> LoadConfiguredContinuousLoopGroupOrder()
    {
        try
        {
            var config = _botConfigStore.Load();
            var configuredOrder = (config[ContinuousLoopGroupOrderConfigKey] as JsonArray ?? new JsonArray())
                .Select(node => node?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Where(name => QueueGroupCatalog.TryParse(name, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var groupKey in QueueGroupCatalog.AllGroups.Select(QueueGroupCatalog.GetKey))
            {
                if (!configuredOrder.Contains(groupKey, StringComparer.OrdinalIgnoreCase))
                {
                    configuredOrder.Add(groupKey);
                }
            }

            return configuredOrder;
        }
        catch
        {
            return QueueGroupCatalog.AllGroups
                .Select(QueueGroupCatalog.GetKey)
                .ToList();
        }
    }

    private static string NormalizeLegacyLoopTaskName(string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return string.Empty;
        }

        return string.Equals(taskName.Trim(), "hero_send_adventure", StringComparison.OrdinalIgnoreCase)
            ? "hero_manage"
            : taskName.Trim();
    }

    private const string TroopsBlockedReasonSmithyMissing = "smithy_missing";
    private const string TroopsBlockedReasonAllDone = "all_done";
    private const string FarmingBlockedReasonNoGoldClub = "no_goldclub";
    private const string FarmingBlockedReasonNoFarmLists = "no_farmlists";
    private const string HeroBlockedReasonNoAdventures = "no_adventures";

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

    private string BuildQueueDisplayName(QueueItem item)
    {
        if (item is null)
        {
            return "-";
        }

        var payload = item.Payload ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var slotId = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeSlotId)
            ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeSlotId)
            ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingConstructSlotId);
        var targetLevel = TryGetIntPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeTargetLevel)
            ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeTargetLevel)
            ?? TryGetIntPayloadValue(payload, BotOptionPayloadKeys.TargetLevel);
        var resourceName = GetPayloadValue(payload, BotOptionPayloadKeys.ResourceUpgradeName);
        var buildingName = GetPayloadValue(payload, BotOptionPayloadKeys.BuildingUpgradeName)
            ?? GetPayloadValue(payload, BotOptionPayloadKeys.BuildingConstructName);

        if (string.Equals(item.TaskName, "upgrade_all_resources_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            return $"Upgrade all resources to level {targetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            var name = !string.IsNullOrWhiteSpace(resourceName)
                ? resourceName
                : (slotId.HasValue ? ResolveResourceName(slotId.Value) : null);
            return !string.IsNullOrWhiteSpace(name) && slotId.HasValue
                ? $"Upgrade {name} slot {slotId.Value} to level {targetLevel.Value}"
                : !string.IsNullOrWhiteSpace(name)
                    ? $"Upgrade {name} to level {targetLevel.Value}"
                    : $"Upgrade resource slot {slotId ?? 0} to level {targetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
        {
            var slotSuffix = slotId.HasValue ? $" (slot {slotId.Value})" : string.Empty;
            return !string.IsNullOrWhiteSpace(buildingName)
                ? $"Construct {buildingName} to level 1{slotSuffix}"
                : $"Construct building{slotSuffix}";
        }

        if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            var name = !string.IsNullOrWhiteSpace(buildingName)
                ? buildingName
                : (slotId.HasValue ? ResolveBuildingName(slotId.Value) : null);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} to level {targetLevel.Value}{BuildSlotSuffix(slotId)}"
                : $"Upgrade building slot {slotId ?? 0} to level {targetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            var name = !string.IsNullOrWhiteSpace(buildingName)
                ? buildingName
                : (slotId.HasValue ? ResolveBuildingName(slotId.Value) : null);
            return !string.IsNullOrWhiteSpace(name)
                ? $"Upgrade {name} to max level{BuildSlotSuffix(slotId)}"
                : $"Upgrade building slot {slotId ?? 0} to max level";
        }

        if (string.Equals(item.TaskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase)
            && targetLevel.HasValue)
        {
            var targetBuilding = GetPayloadValue(payload, BotOptionPayloadKeys.TargetBuildingSlotOrName);
            return !string.IsNullOrWhiteSpace(targetBuilding)
                ? $"Demolish {targetBuilding} to level {targetLevel.Value}"
                : $"Demolish building to level {targetLevel.Value}";
        }

        if (string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase))
        {
            var names = (GetPayloadValue(payload, BotOptionPayloadKeys.ContinuousFarmListNames) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return names.Length > 0
                ? $"Send farmlists: {string.Join(", ", names)}"
                : "Send selected farmlists";
        }

        return string.IsNullOrWhiteSpace(item.DisplayName) ? HumanizeTaskName(item.TaskName) : item.DisplayName;
    }

    private static string? GetPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static int? TryGetIntPayloadValue(IReadOnlyDictionary<string, string> payload, string key)
    {
        return payload.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;
    }

    private string? ResolveResourceName(int slotId)
    {
        return (ResourcesDataGrid.ItemsSource as IEnumerable<ResourceFieldRow>)
            ?.FirstOrDefault(row => row.SlotId == slotId)
            ?.Name;
    }

    private string? ResolveBuildingName(int slotId)
    {
        return _buildingRows.FirstOrDefault(row => row.SlotId == slotId)?.Name;
    }

    private static string BuildSlotSuffix(int? slotId)
    {
        return slotId.HasValue ? $" (slot {slotId.Value})" : string.Empty;
    }

    private void AutomationLoopToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: LoopTaskOption option } toggle)
        {
            return;
        }

        option.IsEnabled = toggle.IsChecked == true;
        if (!option.IsEnabled
            && QueueGroupCatalog.TryParse(option.TaskName, out var disabledGroup)
            && ContinuousRunToggleButton?.IsChecked == true
            && GetActiveContinuousLoopGroup() == disabledGroup
            && _loopTask is not null
            && !_loopTask.IsCompleted)
        {
            _restartContinuousLoopAfterStop = HasEnabledContinuousLoopGroupsExcept(disabledGroup);
            _loopStopRequested = true;
            _loopCts?.Cancel();
            AppendLog($"{QueueGroupCatalog.GetTitle(disabledGroup)} group disabled. Stopping current loop task.");
        }

        if (option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Troops), StringComparison.OrdinalIgnoreCase))
        {
            ClearTroopsBlockedState();
        }
        else if (option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Farming), StringComparison.OrdinalIgnoreCase))
        {
            _lastFarmListsAnalysisAt = DateTimeOffset.MinValue;
            ClearFarmingBlockedState();
        }
        else if (option.IsEnabled
            && string.Equals(option.TaskName, QueueGroupCatalog.GetKey(QueueGroup.Hero), StringComparison.OrdinalIgnoreCase))
        {
            ClearHeroBlockedState();
        }

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

    private void TickTroopTrainingCountdowns()
    {
        if (_troopTrainingBuildingOptions.Count <= 0)
        {
            return;
        }

        foreach (var option in _troopTrainingBuildingOptions)
        {
            option.TickOneSecond();
        }
    }

    private async Task<bool> RefreshFarmListsFromServerAsync(BotOptions options, CancellationToken cancellationToken)
    {
        var goldClubEnabled = await _botService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, cancellationToken);
        UpdateGoldClubInfo(goldClubEnabled);
        if (!goldClubEnabled)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _farmLists.Clear();
                SetFarmingFeatureAvailability(false, "Farming unavailable: Gold Club is not active on this account.");
            });
            return false;
        }

        var lists = await _botService.ReadFarmListsOverviewAsync(options, AppendLog, cancellationToken) ?? [];
        var selectedFarmLists = LoadConfiguredContinuousFarmListNames();
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

        await Dispatcher.InvokeAsync(() =>
        {
            _suppressFarmListUiRefresh = true;
            try
            {
                _farmLists.Clear();
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
                        IsEnabled = selectedFarmLists.Count <= 0 || selectedFarmLists.Contains(pair.Key),
                        RemainingSeconds = pair.Value.RemainingSeconds,
                    });
                    displayedRows++;
                }
            }
            finally
            {
                _suppressFarmListUiRefresh = false;
            }

            SetFarmingFeatureAvailability(true);
            _lastFarmListsAnalysisAt = DateTimeOffset.UtcNow;
            if (_farmLists.Count > 0)
            {
                if (string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoFarmLists, StringComparison.OrdinalIgnoreCase))
                {
                    ClearFarmingBlockedState();
                }
            }
            else
            {
                SetFarmingBlockedState(FarmingBlockedReasonNoFarmLists, "No farmlists available");
            }

            UpdateFarmingUiState();
            SyncFarmListSelectionHandlers();
            RefreshFarmListsItemsControl();
        });

        if (mergedByName.Count > MaxFarmListsShown)
        {
            AppendLog($"Farm list UI limited to {MaxFarmListsShown} rows (detected {mergedByName.Count}).");
        }

        return true;
    }

    private async void AnalyzeFarmListsButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("Analyze Farmlists");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        SetFarmingFunctionRunning(true);
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
            SetFarmingFunctionRunning(false);
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
        SetFarmingFunctionRunning(true);
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
            SetFarmingFunctionRunning(false);
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
        SetFarmingFunctionRunning(true);
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
            SetFarmingFunctionRunning(false);
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
        SetFarmingFunctionRunning(true);
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
            SetFarmingFunctionRunning(false);
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
        SetFarmingFunctionRunning(true);
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
            SetFarmingFunctionRunning(false);
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
            var snapshot = await _botService.LoadPostLoginSnapshotAsync(options, AppendLog, cancellationToken: operationToken);
            ApplyPostLoginSnapshot(snapshot);
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
            await QueueUpgradeAllResourcesAsync(operationId, operationToken, targetLevel);
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

    private async void UpgradeAllResourcesToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("UpgradeAllResourcesToMax");
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            await QueueUpgradeAllResourcesAsync(operationId, operationToken, null);
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

    private async Task QueueUpgradeAllResourcesAsync(string operationId, CancellationToken operationToken, int? targetLevel)
    {
        await EnsureChromiumInstalledAsync();
        var options = LoadBotOptions();
        var status = await ReadVillageStatusWithRetryAsync(options, operationToken, resourceOnly: true, forceCurrentVillage: true);
        var resourceMaxLevel = ResolveResourceMaxLevelFromStatus(status);
        var requestedTargetLevel = targetLevel.HasValue
            ? Math.Min(targetLevel.Value, resourceMaxLevel)
            : resourceMaxLevel;
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
                _queueStopRequested = true;
                AppendLog("Pause requested. Letting current task finish before stopping.");
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
            _startContinuousLoopAfterQueueStop = true;
            _queueStopRequested = true;
            AppendLog("Continuous loop requested. Letting current queue task finish before switching.");
            return;
        }

        if (_uiBusy && (_loopTask is null || _loopTask.IsCompleted))
        {
            _queueStopRequested = true;
            AppendLog("Pause requested. Letting current function finish before stopping.");
            return;
        }

        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            // Pause the loop gracefully too — flag stop, let current iteration finish.
            _loopStopRequested = true;
            AppendLog("Pause requested. Loop will stop after the current iteration.");
            return;
        }

        StartContinuousLoopRunner();
    }

    private void StopBotButton_Click(object sender, RoutedEventArgs e)
    {
        // Hard stop: abort whatever is running right now (including waits) and clear state.
        _queueStopRequested = true;
        _operationCts?.Cancel();
        _autoQueueRunCts?.Cancel();
        if (ContinuousRunToggleButton.IsChecked == true)
        {
            _loopStopRequested = true;
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

        _queueStopRequested = true;
        _loopStopRequested = true;
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
                "troops" => TroopsTabItem,
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
        var currentByGroup = _automationLoopTasks
            .ToDictionary(item => item.TaskName, item => item, StringComparer.OrdinalIgnoreCase);
        var orderedGroupKeys = QueueGroupCatalog.AllGroups
            .Select(QueueGroupCatalog.GetKey)
            .ToList();

        var options = orderedGroupKeys
            .Select(groupKey =>
            {
                QueueGroupCatalog.TryParse(groupKey, out var group);
                return new DashboardFunctionOption
                {
                    Key = groupKey,
                    Label = currentByGroup.TryGetValue(groupKey, out var current)
                        ? current.Title
                        : QueueGroupCatalog.GetTitle(group),
                    IsVisible = currentByGroup.TryGetValue(groupKey, out var selected) && selected.IsVisible,
                };
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

        var selectedGroupNames = dialog.SelectedVisibility
            .Where(item => item.Value)
            .Select(item => item.Key)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _suppressAutomationLoopConfigWrite = true;
        try
        {
            _automationLoopTasks.Clear();
            foreach (var groupKey in orderedGroupKeys)
            {
                if (!QueueGroupCatalog.TryParse(groupKey, out var group))
                {
                    continue;
                }

                if (currentByGroup.TryGetValue(groupKey, out var existing))
                {
                    _automationLoopTasks.Add(new LoopTaskOption
                    {
                        TaskName = existing.TaskName,
                        Title = existing.Title,
                        Description = existing.Description,
                        IsEnabled = existing.IsEnabled && selectedGroupNames.Contains(groupKey),
                        IsVisible = selectedGroupNames.Contains(groupKey),
                        StateText = existing.StateText,
                        DetailText = existing.DetailText,
                        QueuedCount = existing.QueuedCount,
                        RemainingSeconds = existing.RemainingSeconds,
                    });
                    continue;
                }

                _automationLoopTasks.Add(new LoopTaskOption
                {
                    TaskName = groupKey,
                    Title = QueueGroupCatalog.GetTitle(group),
                    Description = QueueGroupCatalog.GetDescription(group),
                    IsEnabled = false,
                    IsVisible = selectedGroupNames.Contains(groupKey),
                    StateText = "Idle",
                    DetailText = "No queued task.",
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
            TroopsNavButton,
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

        var existingItem = _botService.GetQueueItemsForDisplay().FirstOrDefault(item => item.Id == selected.Id);
        if (_botService.RemoveQueueItem(selected.Id))
        {
            if (existingItem is not null)
            {
                ForgetBuildingQueueCachesForItem(existingItem);
            }

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
            if (ReferenceEquals(QueueSectionTabControl?.SelectedItem, HistoryQueueTabItem))
            {
                ClearHistoryQueueItems();
                return;
            }

            ClearActiveQueueItems();
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
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Task", Binding = new Binding("DisplayName"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        activeGrid.Columns.Add(new DataGridTextColumn { Header = "Retries", Binding = new Binding("RetriesText"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

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
        historyGrid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding("GroupName"), Width = new DataGridLength(1.15, DataGridLengthUnitType.Star) });
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
            ClearStaleBuildingPendingCaches(ordered);
            _queueServerTimeOffset = ResolveQueueServerTimeOffset();
            var displayRunningId = ResolveDisplayRunningQueueItemId(ordered);
            var rows = ordered
                .Select(item => new QueueItemRow
                {
                    Id = item.Id,
                    Group = item.Group,
                    GroupName = QueueGroupCatalog.GetTitle(item.Group),
                    DisplayName = BuildQueueDisplayName(item),
                    TaskName = item.TaskName,
                    Status = item.Id == displayRunningId ? QueueStatus.Running : item.Status,
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
            if (_lastBuildingStatus is not null)
            {
                PopulateBuildingsTab(_lastBuildingStatus);
            }

            if (selectId.HasValue)
            {
                var selected = activeRows.FirstOrDefault(item => item.Id == selectId.Value);
                if (selected is not null)
                {
                    QueueDataGrid.SelectedItem = selected;
                }
            }

            QueueInfoTextBlock.Text = $"Queue active: {activeRows.Count} | done: {historyRows.Count}";
            UpdateQueueClearButtonContent();
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

    private static Guid? ResolveDisplayRunningQueueItemId(IReadOnlyList<QueueItem> ordered)
    {
        if (ordered.Any(item => item.Status == QueueStatus.Running))
        {
            return null;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        return ordered
            .FirstOrDefault(item =>
                !item.IsRuntimeOnly &&
                item.Status == QueueStatus.Pending &&
                item.NextAttemptAt > nowUtc)?.Id;
    }

    private void UpdateQueueClearButtonContent()
    {
        if (QueueClearButton is null)
        {
            return;
        }

        QueueClearButton.Content = ReferenceEquals(QueueSectionTabControl?.SelectedItem, HistoryQueueTabItem)
            ? "Clear history"
            : "Clear active queue";
    }

    private void ClearActiveQueueItems()
    {
        var activeRows = (QueueDataGrid.ItemsSource as IEnumerable<QueueItemRow>)?.ToList() ?? [];
        if (activeRows.Count == 0)
        {
            AppendLog("Active queue is already empty.");
            return;
        }

        _loopStopRequested = true;
        _queueStopRequested = true;
        _operationCts?.Cancel();
        _autoQueueRunCts?.Cancel();
        _loopCts?.Cancel();

        var removed = 0;
        foreach (var row in activeRows)
        {
            var existingItem = _botService.GetQueueItemsForDisplay().FirstOrDefault(item => item.Id == row.Id);
            if (!_botService.RemoveQueueItem(row.Id))
            {
                continue;
            }

            if (existingItem is not null)
            {
                ForgetBuildingQueueCachesForItem(existingItem);
            }

            removed += 1;
        }

        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        ClearPendingResourceLevelsFromUi();
        RefreshQueueUi();
        AppendLog(removed > 0
            ? "Active queue cleared and running actions stopped."
            : "Could not clear active queue.");
    }

    private void ClearHistoryQueueItems()
    {
        var historyRows = (QueueHistoryDataGrid.ItemsSource as IEnumerable<QueueItemRow>)?.ToList() ?? [];
        if (historyRows.Count == 0)
        {
            AppendLog("History is already empty.");
            return;
        }

        var removed = 0;
        foreach (var row in historyRows)
        {
            if (_botService.RemoveQueueItem(row.Id))
            {
                removed += 1;
            }
        }

        RefreshQueueUi();
        AppendLog(removed > 0
            ? "Queue history cleared."
            : "Could not clear queue history.");
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

    private void QueueSectionTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, QueueSectionTabControl))
        {
            return;
        }

        UpdateQueueClearButtonContent();
    }

    private void LoadBuildingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Clear any stale pending/queued state so the upcoming snapshot is the source of truth.
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingClickCooldownBySlot.Clear();
        _buildingDemolishingSlots.Clear();

        EnqueueQuickTask("load_buildings_snapshot", "Load buildings snapshot");
        BuildingsInfoTextBlock.Text = "Queued buildings load.";
    }

    private void UpgradeTroopsButton_Click(object sender, RoutedEventArgs e)
    {
        EnqueueQuickTask("upgrade_troops_at_smithy", "Upgrade all troops at Smithy");
        TroopsInfoTextBlock.Text = "Queued: upgrade all troops at Smithy.";
        AppendLog("Queued upgrade_troops_at_smithy task.");
    }

    private void BuildTroopsNowButton_Click(object sender, RoutedEventArgs e)
    {
        EnqueueQuickTask("build_troops", "Build troops");
        TroopsInfoTextBlock.Text = "Queued: build troops.";
        AppendLog("Queued build_troops task.");
    }

    private async void RefreshTroopQueuesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshTroopQueuesButton.IsEnabled = false;
        var operationId = BeginOperation("Refresh troop queues");
        try
        {
            await EnsureChromiumInstalledAsync();
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await RefreshTroopTrainingQueuesAsync(options, CancellationToken.None, _lastBuildingStatus?.Buildings, refreshBuildingsBeforeRead: true);
            TroopsInfoTextBlock.Text = "Troop training queues refreshed.";
            AppendLog($"[{operationId}] Troop training queues refreshed.");
        }
        catch (Exception ex)
        {
            TroopsInfoTextBlock.Text = $"Could not refresh troop queues: {ex.Message}";
            AppendLog($"[{operationId}] Troop queue refresh failed: {ex.Message}");
        }
        finally
        {
            RefreshTroopQueuesButton.IsEnabled = true;
        }
    }

    private void UpgradeAllBuildingsToMaxButton_Click(object sender, RoutedEventArgs e)
    {
        // Always refresh snapshot first so we work from current levels.
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingClickCooldownBySlot.Clear();
        EnqueueQuickTask("load_buildings_snapshot", "Load buildings snapshot");

        // Queue upgrade-to-max for every building slot 19-40. Each task self-validates and skips
        // empty / already-max slots, so we don't need a perfectly fresh snapshot here — the load
        // task above will refresh the UI before/while these run.
        var queued = 0;
        foreach (var slotId in Enumerable.Range(19, 22))
        {
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            };
            EnqueueQuickTask(
                "upgrade_building_to_max",
                $"Upgrade slot {slotId} to max",
                payload);
            queued++;
        }

        BuildingsInfoTextBlock.Text = $"Queued load + upgrade-to-max for {queued} slot(s).";
        AppendLog($"Upgrade-all-to-max: queued load_buildings_snapshot + {queued} upgrade_building_to_max task(s).");
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

        var actionsWindow = new BuildingSlotActionsWindow(row)
        {
            Owner = this,
        };
        if (actionsWindow.ShowDialog() != true)
        {
            return;
        }

        switch (actionsWindow.SelectedAction)
        {
            case BuildingSlotAction.BuildBuilding:
                ShowConstructChoicesForSlot(row.SlotId);
                break;
            case BuildingSlotAction.Upgrade:
                ShowUpgradeTargetForSlot(row);
                break;
            case BuildingSlotAction.UpgradeToMax:
                TryQueueBuildingUpgradeToMax(row.SlotId);
                break;
            case BuildingSlotAction.Demolish:
                TryQueueBuildingDemolish(row, 0);
                break;
        }
    }

    private void ShowUpgradeTargetForSlot(BuildingSlotRow row)
    {
        var liveRow = _buildingRows.FirstOrDefault(item => item.SlotId == row.SlotId) ?? row;
        if (!liveRow.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {liveRow.SlotId} is empty. Choose a building to construct.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_buildingClickCooldownBySlot.TryGetValue(liveRow.SlotId, out var lastClickAt)
            && (now - lastClickAt).TotalMilliseconds < 120)
        {
            return;
        }

        _buildingClickCooldownBySlot[liveRow.SlotId] = now;
        if (liveRow.HasPendingUpgrade)
        {
            BuildingsInfoTextBlock.Text = $"{liveRow.Name} already has a queued upgrade.";
            return;
        }

        var currentLevel = liveRow.Level ?? 0;
        var maxLevel = liveRow.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        if (currentLevel >= maxLevel)
        {
            BuildingsInfoTextBlock.Text = $"{liveRow.Name} in slot {liveRow.SlotId} is already max level ({maxLevel}).";
            return;
        }

        var targetWindow = new BuildingUpgradeTargetWindow(liveRow, maxLevel)
        {
            Owner = this,
        };
        if (targetWindow.ShowDialog() != true)
        {
            return;
        }

        _ = TryQueueBuildingUpgradeToLevel(liveRow.SlotId, targetWindow.SelectedTargetLevel);
    }

    private void QueueSingleBuildingUpgradeFromSlot(int slotId)
    {
        var row = _buildingRows.FirstOrDefault(item => item.SlotId == slotId);
        if (row is null || !row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty. Choose a building to construct.";
            return;
        }

        var currentLevel = row.Level ?? 0;
        var maxLevel = row.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        var pendingLevel = row.PendingTargetLevel ?? currentLevel;
        var baseLevel = Math.Max(currentLevel, pendingLevel);
        var targetLevel = Math.Clamp(baseLevel + 1, 1, maxLevel);
        _ = TryQueueBuildingUpgradeToLevel(slotId, targetLevel);
    }

    private static bool IsRallyPointSlot(int slotId) => slotId == 39;

    private static bool IsRallyPointGid(int gid) => gid == 16;

    private bool TryQueueBuildingUpgradeToLevel(int slotId, int targetLevel)
    {
        if (targetLevel < 1)
        {
            BuildingsInfoTextBlock.Text = "Target level must be an integer >= 1.";
            return false;
        }

        var row = _buildingRows.FirstOrDefault(item => item.SlotId == slotId);
        if (row is null || !row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty.";
            return false;
        }

        var currentLevel = row.Level ?? 0;
        var maxLevel = row.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid) : 40;
        if (currentLevel >= maxLevel)
        {
            BuildingsInfoTextBlock.Text = $"{row.Name} in slot {slotId} is already max level ({maxLevel}).";
            return false;
        }

        targetLevel = Math.Clamp(targetLevel, currentLevel + 1, maxLevel);
        var now = DateTimeOffset.UtcNow;
        if (_buildingLastQueuedTargetBySlot.TryGetValue(slotId, out var lastQueued)
            && lastQueued.Target == targetLevel
            && (now - lastQueued.At).TotalMilliseconds < 2500)
        {
            return false;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = targetLevel.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeName] = row.Name,
        };
        var item = EnqueueBuildingUpgradeTaskCoalesced(
            "upgrade_building_to_level",
            payload,
            slotId,
            targetLevel,
            out var effectiveTargetLevel,
            out var enqueued,
            out var removedCount);
        if (!enqueued)
        {
            BuildingsInfoTextBlock.Text = $"{row.Name} already has a queued upgrade to level {effectiveTargetLevel ?? targetLevel} or higher.";
            return false;
        }

        targetLevel = effectiveTargetLevel ?? targetLevel;
        _buildingLastQueuedTargetBySlot[slotId] = (targetLevel, now);
        SetPendingBuildingUpgrade(slotId, targetLevel);
        RequestQueueUiRefresh(selectId: item?.Id);
        TriggerQueueAutoRunFromEnqueue();
        UpgradeSlotTextBox.Text = slotId.ToString();
        UpgradeTargetLevelTextBox.Text = targetLevel.ToString();
        BuildingsInfoTextBlock.Text = $"Queued {row.Name} in slot {slotId} to level {targetLevel}.";
        var removedSuffix = removedCount > 0 ? $" (replaced {removedCount} pending item(s))" : string.Empty;
        AppendLog($"Queued single building upgrade: slot {slotId} -> level {targetLevel}{removedSuffix}.");
        return true;
    }

    private void ShowConstructChoicesForSlot(int slotId)
    {
        if (_lastBuildingStatus is null)
        {
            BuildingsInfoTextBlock.Text = "Load buildings first.";
            return;
        }

        var options = GetClassifiedConstructOptionsForSlot(slotId);
        if (options.Count == 0)
        {
            BuildingsInfoTextBlock.Text = $"No constructable buildings available for slot {slotId} right now.";
            return;
        }

        ConstructSlotTextBox.Text = slotId.ToString();
        var choiceWindow = new BuildingConstructChoiceWindow(slotId, options)
        {
            Owner = this,
        };
        if (choiceWindow.ShowDialog() != true || choiceWindow.SelectedOption is null)
        {
            return;
        }

        var selected = choiceWindow.SelectedOption;
        var targetLevel = choiceWindow.SelectedTargetLevel;
        if (!TryQueueConstructBuilding(slotId, selected))
        {
            return;
        }

        if (targetLevel == 0)
        {
            // Slot is still empty at this moment (construct hasn't run yet), so we queue
            // upgrade-to-max directly instead of going through TryQueueBuildingUpgradeToMax
            // which gates on IsOccupied.
            var maxPayload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
                [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = selected.MaxLevel.ToString(),
                [BotOptionPayloadKeys.BuildingUpgradeName] = selected.Name,
            };
            var queuedMax = EnqueueBuildingUpgradeTaskCoalesced(
                "upgrade_building_to_max",
                maxPayload,
                slotId,
                selected.MaxLevel,
                out var effectiveTargetLevel,
                out var enqueued,
                out _);
            if (enqueued)
            {
                SetPendingBuildingUpgrade(slotId, effectiveTargetLevel ?? selected.MaxLevel);
                RequestQueueUiRefresh(selectId: queuedMax?.Id);
                TriggerQueueAutoRunFromEnqueue();
            }
            BuildingsInfoTextBlock.Text = $"Queued construct + upgrade to max for {selected.Name} in slot {slotId}.";
        }
        else if (targetLevel > 1)
        {
            var clamped = Math.Clamp(targetLevel, 1, selected.MaxLevel);
            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
                [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = clamped.ToString(),
                [BotOptionPayloadKeys.BuildingUpgradeName] = selected.Name,
            };
            var queuedUpgrade = EnqueueBuildingUpgradeTaskCoalesced(
                "upgrade_building_to_level",
                payload,
                slotId,
                clamped,
                out var effectiveTargetLevel,
                out var enqueued,
                out _);
            if (enqueued)
            {
                SetPendingBuildingUpgrade(slotId, effectiveTargetLevel ?? clamped);
                RequestQueueUiRefresh(selectId: queuedUpgrade?.Id);
                TriggerQueueAutoRunFromEnqueue();
            }
            BuildingsInfoTextBlock.Text = $"Queued construct + upgrade to level {clamped} for {selected.Name} in slot {slotId}.";
        }
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

    private static readonly HashSet<int> WallGids = [31, 32, 33, 42, 43];
    private static readonly HashSet<int> DuplicateAllowedGids = [10, 11, 23, 38, 39];

    private static bool IsBuildingMutationTask(string taskName) =>
        string.Equals(taskName, "load_buildings_snapshot", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "construct_building", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskName, "demolish_building_to_level", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<QueueItem> GetActiveQueueItems()
    {
        return _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused)
            .ToList();
    }

    private static bool IsActiveBuildingQueueStatus(QueueStatus status)
    {
        return status is QueueStatus.Pending or QueueStatus.Paused or QueueStatus.Running;
    }

    private static bool TryReadBuildingConstructPayload(
        IReadOnlyDictionary<string, string> payload,
        out int slotId,
        out int gid,
        out string buildingName)
    {
        slotId = 0;
        gid = 0;
        buildingName = string.Empty;

        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out slotId))
        {
            return false;
        }

        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructGid, out var gidRaw)
            || !int.TryParse(gidRaw, out gid))
        {
            return false;
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructName, out var nameRaw))
        {
            buildingName = nameRaw?.Trim() ?? string.Empty;
        }

        return true;
    }

    private static bool TryReadBuildingUpgradePayload(
        IReadOnlyDictionary<string, string> payload,
        out int slotId,
        out int? targetLevel)
    {
        slotId = 0;
        targetLevel = null;

        if (!payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var slotRaw)
            || !int.TryParse(slotRaw, out slotId))
        {
            return false;
        }

        if (payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var targetRaw)
            && int.TryParse(targetRaw, out var parsedTargetLevel))
        {
            targetLevel = parsedTargetLevel;
        }

        return true;
    }

    private void ForgetBuildingQueueCachesForItem(QueueItem item)
    {
        if (item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var upgradeSlotRaw)
            && int.TryParse(upgradeSlotRaw, out var upgradeSlotId))
        {
            _buildingLastQueuedTargetBySlot.Remove(upgradeSlotId);
        }

        if (item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var constructSlotRaw)
            && int.TryParse(constructSlotRaw, out var constructSlotId))
        {
            _buildingLastQueuedConstructBySlot.Remove(constructSlotId);
        }
    }

    private void ApplySelectedVillageToPayload(Dictionary<string, string> payload)
    {
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
    }

    private QueueItem? EnqueueBuildingConstructTaskCoalesced(
        Dictionary<string, string> payload,
        int slotId,
        int gid,
        out bool enqueued,
        out int removedCount)
    {
        var relatedItems = _botService.GetQueueItemsForDisplay()
            .Where(item => IsActiveBuildingQueueStatus(item.Status))
            .Where(item =>
            {
                if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                    && TryReadBuildingConstructPayload(item.Payload, out var existingSlotId, out _, out _))
                {
                    return existingSlotId == slotId;
                }

                if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                    && TryReadBuildingUpgradePayload(item.Payload, out var existingUpgradeSlotId, out _))
                {
                    return existingUpgradeSlotId == slotId;
                }

                return false;
            })
            .ToList();

        var matchingConstruct = relatedItems
            .Where(item => string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(item =>
                TryReadBuildingConstructPayload(item.Payload, out _, out var existingGid, out _)
                && existingGid == gid);
        if (matchingConstruct is not null)
        {
            enqueued = false;
            removedCount = 0;
            return matchingConstruct;
        }

        removedCount = 0;
        foreach (var related in relatedItems.Where(item => item.Status is QueueStatus.Pending or QueueStatus.Paused))
        {
            if (_botService.RemoveQueueItem(related.Id))
            {
                ForgetBuildingQueueCachesForItem(related);
                removedCount += 1;
            }
        }

        ApplySelectedVillageToPayload(payload);
        var created = _botService.Enqueue("construct_building", payload, priority: 0, maxRetries: 3);
        enqueued = true;
        return created;
    }

    private QueueItem? EnqueueBuildingUpgradeTaskCoalesced(
        string taskName,
        Dictionary<string, string> payload,
        int slotId,
        int? requestedTargetLevel,
        out int? effectiveTargetLevel,
        out bool enqueued,
        out int removedCount)
    {
        var relatedItems = _botService.GetQueueItemsForDisplay()
            .Where(item =>
                (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && IsActiveBuildingQueueStatus(item.Status))
            .Select(item =>
            {
                var parsed = TryReadBuildingUpgradePayload(item.Payload, out var parsedSlotId, out var parsedTargetLevel);
                return new
                {
                    Item = item,
                    Parsed = parsed,
                    SlotId = parsedSlotId,
                    TargetLevel = parsedTargetLevel,
                    IsMax = string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase),
                };
            })
            .Where(item => item.Parsed && item.SlotId == slotId)
            .ToList();

        var existingMax = relatedItems.FirstOrDefault(item => item.IsMax);
        var highestExistingTarget = relatedItems
            .Where(item => item.TargetLevel.HasValue)
            .Select(item => item.TargetLevel!.Value)
            .DefaultIfEmpty(0)
            .Max();
        effectiveTargetLevel = requestedTargetLevel.HasValue
            ? Math.Max(requestedTargetLevel.Value, highestExistingTarget)
            : highestExistingTarget > 0 ? highestExistingTarget : null;

        if (string.Equals(taskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
        {
            if (existingMax is not null)
            {
                enqueued = false;
                removedCount = 0;
                return existingMax.Item;
            }
        }
        else if (requestedTargetLevel.HasValue)
        {
            if (existingMax is not null || highestExistingTarget >= requestedTargetLevel.Value)
            {
                enqueued = false;
                removedCount = 0;
                return relatedItems
                    .OrderByDescending(item => item.IsMax)
                    .ThenByDescending(item => item.TargetLevel ?? 0)
                    .ThenBy(item => item.Item.CreatedAt)
                    .Select(item => item.Item)
                    .FirstOrDefault();
            }
        }

        removedCount = 0;
        foreach (var related in relatedItems.Where(item => item.Item.Status is QueueStatus.Pending or QueueStatus.Paused))
        {
            if (_botService.RemoveQueueItem(related.Item.Id))
            {
                ForgetBuildingQueueCachesForItem(related.Item);
                removedCount += 1;
            }
        }

        if (effectiveTargetLevel.HasValue)
        {
            payload[BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = effectiveTargetLevel.Value.ToString();
        }

        ApplySelectedVillageToPayload(payload);
        var created = _botService.Enqueue(taskName, payload, priority: 0, maxRetries: 3);
        enqueued = true;
        return created;
    }

    private VillageStatus BuildProjectedBuildingStatus(VillageStatus status, IReadOnlyList<QueueItem>? queueItems = null)
    {
        var projectedBuildings = status.Buildings
            .Select(item => item with { })
            .ToList();
        var bySlot = projectedBuildings
            .Where(item => item.SlotId is not null)
            .GroupBy(item => item.SlotId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Level ?? 0).First());

        foreach (var item in queueItems ?? GetActiveQueueItems())
        {
            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var constructSlotRaw)
                && int.TryParse(constructSlotRaw, out var constructSlotId)
                && item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructGid, out var constructGidRaw)
                && int.TryParse(constructGidRaw, out var constructGid))
            {
                var constructName = item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructName, out var queuedConstructName)
                    ? queuedConstructName
                    : $"gid {constructGid}";
                var projected = new Building(constructSlotId, constructName, 0, null, constructGid);
                bySlot[constructSlotId] = projected;
                continue;
            }

            if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var upgradeSlotRaw)
                && int.TryParse(upgradeSlotRaw, out var upgradeSlotId)
                && bySlot.TryGetValue(upgradeSlotId, out var currentProjected))
            {
                var currentLevel = currentProjected.Level ?? 0;
                var targetLevel = currentLevel;
                if (string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                    && item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var upgradeTargetRaw)
                    && int.TryParse(upgradeTargetRaw, out var parsedTargetLevel))
                {
                    targetLevel = Math.Max(currentLevel, parsedTargetLevel);
                }
                else if (currentProjected.Gid is int currentGid)
                {
                    targetLevel = Math.Max(currentLevel, BuildingCatalogService.MaxLevelFor(currentGid));
                }

                bySlot[upgradeSlotId] = currentProjected with { Level = targetLevel };
            }
        }

        return status with { Buildings = bySlot.Values.ToList() };
    }

    private IReadOnlyDictionary<int, string> GetQueuedBuildingConstructsBySlot(IReadOnlyList<QueueItem>? queueItems = null)
    {
        var result = new Dictionary<int, string>();
        foreach (var item in queueItems ?? GetActiveQueueItems())
        {
            if (!string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructSlotId, out var slotRaw)
                || !int.TryParse(slotRaw, out var slotId))
            {
                continue;
            }

            if (item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingConstructName, out var name)
                && !string.IsNullOrWhiteSpace(name))
            {
                result[slotId] = name;
            }
        }

        return result;
    }

    private IReadOnlyDictionary<int, int> GetQueuedBuildingTargetsBySlot(IReadOnlyList<QueueItem>? queueItems = null)
    {
        var result = new Dictionary<int, int>();
        foreach (var item in queueItems ?? GetActiveQueueItems())
        {
            if (!string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeSlotId, out var slotRaw)
                || !int.TryParse(slotRaw, out var slotId))
            {
                continue;
            }

            if (item.Payload.TryGetValue(BotOptionPayloadKeys.BuildingUpgradeTargetLevel, out var targetRaw)
                && int.TryParse(targetRaw, out var targetLevel))
            {
                result[slotId] = targetLevel;
            }
        }

        return result;
    }

    private void ClearStaleBuildingPendingCaches(IReadOnlyList<QueueItem>? queueItems = null)
    {
        var activeItems = queueItems ?? GetActiveQueueItems();
        var activeUpgradeSlots = new HashSet<int>();
        var activeConstructSlots = new HashSet<int>();

        foreach (var item in activeItems)
        {
            if ((string.Equals(item.TaskName, "upgrade_building_to_level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.TaskName, "upgrade_building_to_max", StringComparison.OrdinalIgnoreCase))
                && TryReadBuildingUpgradePayload(item.Payload, out var slotId, out _))
            {
                activeUpgradeSlots.Add(slotId);
            }

            if (string.Equals(item.TaskName, "construct_building", StringComparison.OrdinalIgnoreCase)
                && TryReadBuildingConstructPayload(item.Payload, out var constructSlotId, out _, out _))
            {
                activeConstructSlots.Add(constructSlotId);
            }
        }

        foreach (var slotId in _buildingLastQueuedTargetBySlot.Keys.Except(activeUpgradeSlots).ToList())
        {
            _buildingLastQueuedTargetBySlot.Remove(slotId);
        }

        foreach (var slotId in _buildingLastQueuedConstructBySlot.Keys.Except(activeConstructSlots).ToList())
        {
            _buildingLastQueuedConstructBySlot.Remove(slotId);
        }
    }

    private static string NormalizeBuildingName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim().ToLowerInvariant();
        return trimmed.Replace("'", string.Empty).Replace("’", string.Empty);
    }

    private IReadOnlyList<BuildingCatalogOption> GetClassifiedConstructOptionsForSlot(int slotId)
    {
        if (_lastBuildingStatus is null)
        {
            return [];
        }

        var status = BuildProjectedBuildingStatus(_lastBuildingStatus);
        var fullCatalog = BuildingCatalogService.GetFullCatalog(status.Tribe);
        var occupiedBuildings = status.Buildings
            .Where(b => (b.Level ?? 0) > 0)
            .ToList();
        var existingGids = occupiedBuildings
            .Where(b => b.Gid is not null)
            .Select(b => b.Gid!.Value)
            .ToHashSet();
        var existingNames = occupiedBuildings
            .Select(b => NormalizeBuildingName(b.Name))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wallNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "city wall", "earth wall", "palisade", "stone wall", "makeshift wall",
        };
        var anyWallExists = existingGids.Any(g => WallGids.Contains(g))
            || existingNames.Any(n => wallNames.Contains(n));
        var result = new List<BuildingCatalogOption>(fullCatalog.Count);

        foreach (var entry in fullCatalog)
        {
            var maxLevel = BuildingCatalogService.MaxLevelFor(entry.Gid);
            var option = new BuildingCatalogOption
            {
                Gid = entry.Gid,
                Name = entry.Name,
                Category = entry.Category,
                IsSpecial = entry.IsSpecial,
                Tribe = entry.RequiredTribe,
                MaxLevel = maxLevel,
                RequirementEntries = entry.Requirements,
                Requirements = entry.Requirements.Count == 0
                    ? "-"
                    : string.Join(", ", entry.Requirements.Select(req => $"{req.Name} {req.Level}+")),
            };

            if (entry.IsSpecial && !entry.MatchesPlayerTribe)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = string.IsNullOrEmpty(entry.RequiredTribe)
                    ? "Wrong tribe"
                    : $"Only available for {entry.RequiredTribe}";
                result.Add(option);
                continue;
            }

            // World Wonder, Great Warehouse, Great Granary require building plans — not yet supported.
            if (entry.Gid is 38 or 39 or 40)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = entry.Gid == 40
                    ? "World Wonder requires building plans"
                    : $"{entry.Name} requires building plans";
                result.Add(option);
                continue;
            }

            // Great Barracks (29) and Great Stable (30) cannot be built in the capital village.
            if ((entry.Gid is 29 or 30) && status.IsCapital == true)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = $"{entry.Name} cannot be built in the capital";
                result.Add(option);
                continue;
            }

            // Palace (26) conflicts with Residence (25) and Command Center (44) — only one allowed per village.
            if (entry.Gid == 26 && (existingGids.Contains(25) || existingGids.Contains(44)))
            {
                var conflicting = existingGids.Contains(25) ? "Residence" : "Command Center";
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = $"{conflicting} already exists in this village";
                result.Add(option);
                continue;
            }
            // Residence (25) conflicts with Palace (26) and Command Center (44) symmetrically.
            if (entry.Gid == 25 && (existingGids.Contains(26) || existingGids.Contains(44)))
            {
                var conflicting = existingGids.Contains(26) ? "Palace" : "Command Center";
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = $"{conflicting} already exists in this village";
                result.Add(option);
                continue;
            }
            // Command Center (44) conflicts with Palace (26) and Residence (25).
            if (entry.Gid == 44 && (existingGids.Contains(25) || existingGids.Contains(26)))
            {
                var conflicting = existingGids.Contains(25) ? "Residence" : "Palace";
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = $"{conflicting} already exists in this village";
                result.Add(option);
                continue;
            }

            var isWall = WallGids.Contains(entry.Gid);
            var isRallyPoint = IsRallyPointGid(entry.Gid);
            const int rallyPointSlotId = 39;
            const int wallSlotId = 40;
            if (isRallyPoint && slotId != rallyPointSlotId)
            {
                continue;
            }
            if (!isRallyPoint && slotId == rallyPointSlotId)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = "Slot 39 is the Rally Point slot";
                result.Add(option);
                continue;
            }
            if (isWall && slotId != wallSlotId)
            {
                // Walls can only be built on slot 40 — hide from other slots.
                continue;
            }
            if (!isWall && slotId == wallSlotId)
            {
                option.Availability = BuildingConstructAvailability.Unavailable;
                option.UnavailableReason = "Slot 40 is the wall slot";
                result.Add(option);
                continue;
            }
            var matchesByName = existingNames.Contains(NormalizeBuildingName(entry.Name));
            var alreadyBuilt = ((existingGids.Contains(entry.Gid) || matchesByName)
                    && !DuplicateAllowedGids.Contains(entry.Gid) && !isWall)
                || (isWall && anyWallExists);
            if (alreadyBuilt)
            {
                option.Availability = BuildingConstructAvailability.AlreadyBuilt;
                option.UnavailableReason = isWall
                    ? "Wall already built in this village"
                    : "Already built in this village";
                result.Add(option);
                continue;
            }

            if (CanQueueConstructBuilding(slotId, option, out var reason))
            {
                option.Availability = BuildingConstructAvailability.Available;
            }
            else
            {
                var missing = MissingRequirements(status, option.RequirementEntries);
                if (missing.Count > 0)
                {
                    option.Availability = BuildingConstructAvailability.Locked;
                    option.MissingRequirements = missing;
                }
                else
                {
                    option.Availability = BuildingConstructAvailability.Unavailable;
                    option.UnavailableReason = reason;
                }
            }

            result.Add(option);
        }

        return result;
    }

    private bool CanQueueConstructBuilding(int slotId, BuildingCatalogOption selectedBuilding, out string reason)
    {
        reason = string.Empty;
        if (_lastBuildingStatus is null)
        {
            reason = "Load buildings first.";
            return false;
        }

        var projectedStatus = BuildProjectedBuildingStatus(_lastBuildingStatus);
        var occupied = projectedStatus.Buildings.FirstOrDefault(item => item.SlotId == slotId && ((item.Level ?? 0) > 0 || (item.Gid ?? 0) > 0));
        if (occupied is not null)
        {
            reason = (occupied.Level ?? 0) > 0
                ? $"Slot {slotId} is occupied by {occupied.Name} level {occupied.Level}."
                : $"Slot {slotId} is already reserved for {occupied.Name}.";
            return false;
        }

        var existingSameGidLevels = projectedStatus.Buildings
            .Where(item => item.Gid == selectedBuilding.Gid && ((item.Level ?? 0) > 0 || (item.Gid ?? 0) > 0))
            .Select(item => item.Level ?? 0)
            .ToList();
        var duplicateAllowed = selectedBuilding.Gid is 23 or 38 or 39;
        var wallGid = selectedBuilding.Gid is 31 or 32 or 33 or 42 or 43;
        var rallyPointGid = IsRallyPointGid(selectedBuilding.Gid);
        if (rallyPointGid && slotId != 39)
        {
            reason = "Rally Point can only be built on slot 39.";
            return false;
        }

        if (!rallyPointGid && slotId == 39)
        {
            reason = "Slot 39 is the Rally Point slot.";
            return false;
        }

        if (wallGid && slotId != 40)
        {
            reason = "Wall can only be built on slot 40.";
            return false;
        }

        if (!wallGid && slotId == 40)
        {
            reason = "Slot 40 is the wall slot.";
            return false;
        }

        if (selectedBuilding.Gid is 10 or 11)
        {
            if (existingSameGidLevels.Count > 0)
            {
                var currentHighest = existingSameGidLevels.Max();
                if (currentHighest < 20)
                {
                    reason = $"{selectedBuilding.Name} can only be duplicated after an existing one reaches level 20.";
                    return false;
                }
            }
        }
        else if (selectedBuilding.Gid == 23)
        {
            if (existingSameGidLevels.Count > 0)
            {
                var currentHighest = existingSameGidLevels.Max();
                if (currentHighest < 10)
                {
                    reason = $"{selectedBuilding.Name} can only be duplicated after an existing one reaches level 10.";
                    return false;
                }
            }
        }
        else if (existingSameGidLevels.Count > 0 && !duplicateAllowed && !wallGid)
        {
            reason = $"{selectedBuilding.Name} already exists in this village.";
            return false;
        }

        var missing = MissingRequirements(projectedStatus, selectedBuilding.RequirementEntries);
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
        var item = EnqueueBuildingConstructTaskCoalesced(
            payload,
            slotId,
            selectedBuilding.Gid,
            out var enqueued,
            out var removedCount);
        if (!enqueued)
        {
            BuildingsInfoTextBlock.Text = $"{selectedBuilding.Name} is already queued for slot {slotId}.";
            return false;
        }

        _buildingLastQueuedConstructBySlot[slotId] = (selectedBuilding.Name, now);
        SetPendingBuildingConstruct(slotId, selectedBuilding.Name);
        RequestQueueUiRefresh(selectId: item?.Id);
        TriggerQueueAutoRunFromEnqueue();
        ConstructSlotTextBox.Text = slotId.ToString();
        ConstructBuildingComboBox.SelectedItem = _buildingCatalogOptions.FirstOrDefault(item => item.Gid == selectedBuilding.Gid);
        BuildingsInfoTextBlock.Text = $"Queued construct: {selectedBuilding.Name} in slot {slotId}.";
        var removedSuffix = removedCount > 0 ? $" (replaced {removedCount} pending item(s))" : string.Empty;
        AppendLog($"Queued building construct: slot {slotId} -> {selectedBuilding.Name}{removedSuffix}.");
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
            PendingConstructName = row.PendingConstructName,
            IsDemolishing = row.IsDemolishing,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
            IsWallSlot = row.IsWallSlot,
            IsRallyPointSlot = row.IsRallyPointSlot,
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
            IsDemolishing = row.IsDemolishing,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
            IsWallSlot = row.IsWallSlot,
            IsRallyPointSlot = row.IsRallyPointSlot,
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

    private void QueueUpgradeBuildingMaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(UpgradeSlotTextBox.Text.Trim(), out var slotId) || slotId < 19)
        {
            BuildingsInfoTextBlock.Text = "Upgrade slot must be an integer >= 19.";
            return;
        }

        TryQueueBuildingUpgradeToMax(slotId);
    }

    private bool TryQueueBuildingUpgradeToMax(int slotId)
    {
        var row = _buildingRows.FirstOrDefault(item => item.SlotId == slotId);
        if (row is null || !row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {slotId} is empty.";
            return false;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.BuildingUpgradeSlotId] = slotId.ToString(),
            [BotOptionPayloadKeys.BuildingUpgradeTargetLevel] = row.Gid is int gid ? BuildingCatalogService.MaxLevelFor(gid).ToString() : "40",
            [BotOptionPayloadKeys.BuildingUpgradeName] = row.Name,
        };
        var item = EnqueueBuildingUpgradeTaskCoalesced(
            "upgrade_building_to_max",
            payload,
            slotId,
            row.Gid is int existingGid ? BuildingCatalogService.MaxLevelFor(existingGid) : 40,
            out var effectiveTargetLevel,
            out var enqueued,
            out var removedCount);
        if (!enqueued)
        {
            BuildingsInfoTextBlock.Text = $"{row.Name} already has a queued max upgrade.";
            return false;
        }

        SetPendingBuildingUpgrade(slotId, effectiveTargetLevel ?? (row.Gid is int effectiveGid ? BuildingCatalogService.MaxLevelFor(effectiveGid) : 40));
        RequestQueueUiRefresh(selectId: item?.Id);
        TriggerQueueAutoRunFromEnqueue();
        UpgradeSlotTextBox.Text = slotId.ToString();
        BuildingsInfoTextBlock.Text = $"Queued max-upgrade for slot {slotId}.";
        if (removedCount > 0)
        {
            AppendLog($"Queued building max-upgrade: slot {slotId} (replaced {removedCount} pending item(s)).");
        }
        return true;
    }

    private bool TryQueueBuildingDemolish(BuildingSlotRow row, int targetLevel)
    {
        if (!row.IsOccupied)
        {
            BuildingsInfoTextBlock.Text = $"Slot {row.SlotId} is empty.";
            return false;
        }

        if (targetLevel < 0)
        {
            BuildingsInfoTextBlock.Text = "Demolish target level must be an integer >= 0.";
            return false;
        }

        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetBuildingSlotOrName] = row.SlotId.ToString(),
            [BotOptionPayloadKeys.TargetLevel] = targetLevel.ToString(),
        };
        EnqueueQuickTask("demolish_building_to_level", $"Demolish {row.Name} to level {targetLevel}", payload);
        SetDemolishingFlag(row.SlotId, true);
        DemolishBuildingComboBox.SelectedItem = _demolishableBuildings.FirstOrDefault(item => item.SlotId == row.SlotId);
        DemolishTargetLevelTextBox.Text = targetLevel.ToString();
        BuildingsInfoTextBlock.Text = $"Queued demolition for {row.Name} (slot {row.SlotId}) to level {targetLevel}.";
        return true;
    }

    private void SetDemolishingFlag(int slotId, bool demolishing)
    {
        if (demolishing)
        {
            _buildingDemolishingSlots.Add(slotId);
        }
        else
        {
            _buildingDemolishingSlots.Remove(slotId);
        }

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
        if (row.IsDemolishing == demolishing)
        {
            return;
        }

        _buildingRows[index] = new BuildingSlotRow
        {
            SlotId = row.SlotId,
            Name = row.Name,
            Level = row.Level,
            Gid = row.Gid,
            Category = row.Category,
            Requirements = row.Requirements,
            PendingTargetLevel = row.PendingTargetLevel,
            PendingConstructName = row.PendingConstructName,
            IsDemolishing = demolishing,
            MapLeft = row.MapLeft,
            MapTop = row.MapTop,
            IsWallSlot = row.IsWallSlot,
            IsRallyPointSlot = row.IsRallyPointSlot,
        };
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
        PersistHeroPriorityToConfig();
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
        PersistHeroPriorityToConfig();
    }

    private async void RefreshHeroStatsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshHeroStatsButton.IsEnabled = false;
        var operationId = BeginOperation("Refresh hero stats");
        var operationSw = Stopwatch.StartNew();

        try
        {
            await EnsureChromiumInstalledAsync();
            var snapshot = await RefreshHeroStatsAsync(CancellationToken.None);
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

    private async Task<HeroAttributeSnapshot> RefreshHeroStatsAsync(CancellationToken cancellationToken)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, cancellationToken);
        ApplyHeroAttributeSnapshotToUi(snapshot);
        return snapshot;
    }

    private void HeroHideModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires during InitializeComponent for the XAML-default IsChecked="True", before
        // services and other controls are wired — bail out until the window has finished loading.
        if (_suppressHeroHideModeApply || !IsLoaded)
        {
            return;
        }

        var mode = HeroFightRadio?.IsChecked == true ? "fight" : "hide";
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.HeroHideMode] = mode,
        };
        EnqueueQuickTask("hero_set_hide_mode", $"Set hero hide mode to '{mode}'", payload);
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
            [BotOptionPayloadKeys.HeroAdventurePickOrder] = HeroAdventureTopRadio?.IsChecked == true ? "top" : "shortest",
            [BotOptionPayloadKeys.HeroHideMode] = HeroFightRadio?.IsChecked == true ? "fight" : "hide",
        };

        var continuous = ContinuousAdventuresCheckBox?.IsChecked == true;
        var copies = 1;
        if (continuous && int.TryParse(HeroAdventureCountTextBlock.Text.Trim(), out var available) && available > 1)
        {
            copies = Math.Min(available, 20); // hard cap to avoid runaway queues if count is wrong
        }

        for (var i = 0; i < copies; i++)
        {
            EnqueueQuickTask("hero_manage", "Hero adventure (with revive/points checks)", payload);
        }
        BuildingsInfoTextBlock.Text = continuous && copies > 1
            ? $"Queued {copies} hero adventures."
            : "Queued hero adventure.";
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

    private static bool IsResourceUpgradeQueueTask(string taskName)
    {
        return string.Equals(taskName, "upgrade_resource_to_level", StringComparison.OrdinalIgnoreCase);
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
        if (!IsResourceUpgradeQueueTask(item.TaskName) || queueWaitDelay.TotalSeconds < 3)
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

    private async Task RefreshAdventureCountAfterLoginAsync(BotOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var count = await _botService.RefreshAdventureCountAsync(options, AppendLog, cancellationToken);
            if (count is null)
            {
                ApplyHeroAdventureAvailability(null);
                AppendLog("Adventure count: not found on current page.");
            }
            else
            {
                ApplyHeroAdventureAvailability(count.Value);
                AppendLog($"Adventure count after login: {count.Value}.");
            }
        }
        catch (OperationCanceledException)
        {
            // Login flow was cancelled — leave the count unchanged.
        }
        catch (Exception ex)
        {
            AppendLog($"Adventure count refresh after login failed: {ex.Message}");
        }
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
                ApplyHeroAdventureAvailability(null);
                HeroAdventureStatusTextBlock.Text = "Adventures not found on current page.";
            }
            else
            {
                ApplyHeroAdventureAvailability(count.Value);
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
            ResourceFields: (snapshot.ResourceFields ?? [])
                .Select(item => new ResourceField(
                    item.SlotId,
                    item.FieldType ?? string.Empty,
                    item.Name ?? string.Empty,
                    item.Level,
                    item.Url))
                .ToList(),
            Buildings: (snapshot.Buildings ?? [])
                .Select(item =>
                {
                    var name = string.IsNullOrWhiteSpace(item.Name) || string.Equals(item.Name, "g0", StringComparison.OrdinalIgnoreCase)
                        ? "Empty"
                        : item.Name!;
                    var gid = item.Gid is > 0 ? item.Gid : null;
                    var level = gid is null && string.Equals(name, "Empty", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : item.Level;
                    return new Building(item.SlotId, name, level, item.Url, gid);
                })
                .ToList(),
            BuildQueue: [],
            Tribe: snapshot.Tribe ?? "Unknown",
            VillageCount: 0,
            IsCapital: snapshot.IsCapital);

        _lastBuildingStatus = status;
        await Dispatcher.InvokeAsync(() =>
        {
            ApplyTroopsAvailabilityFromVillageStatus(status);
            PopulateBuildingsTab(status);
            BuildingsInfoTextBlock.Text = $"Loaded {status.Buildings.Count} building slots from queue snapshot.";
        });
    }

    private sealed record BuildingSnapshotDto(
        string? Account,
        string? ActiveVillage,
        string? Tribe,
        bool? IsCapital,
        List<BuildingSnapshotItemDto>? Buildings,
        List<ResourceFieldSnapshotItemDto>? ResourceFields);

    private sealed record BuildingSnapshotItemDto(
        int? SlotId,
        string? Name,
        int? Level,
        string? Url,
        int? Gid);

    private sealed record ResourceFieldSnapshotItemDto(
        int? SlotId,
        string? FieldType,
        string? Name,
        int? Level,
        string? Url);

    private void PopulateBuildingsTab(VillageStatus status)
    {
        _buildingRows.Clear();
        _demolishableBuildings.Clear();
        var queueItems = GetActiveQueueItems();
        var projectedStatus = BuildProjectedBuildingStatus(status, queueItems);
        var queuedConstructsBySlot = GetQueuedBuildingConstructsBySlot(queueItems);
        var queuedTargetsBySlot = GetQueuedBuildingTargetsBySlot(queueItems);

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

        var projectedBuildingBySlot = projectedStatus.Buildings
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
            var isKnownEmpty = building is null || IsEmptyBuilding(building);
            var hasIdentifiedBuildingName = building is not null
                && !string.IsNullOrWhiteSpace(building.Name)
                && !string.Equals(building.Name, "Unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(building.Name, "g0", StringComparison.OrdinalIgnoreCase)
                && !building.Name.StartsWith("Slot ", StringComparison.OrdinalIgnoreCase);
            var occupied = building is not null
                && !isKnownEmpty
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

            int? pendingTarget = queuedTargetsBySlot.TryGetValue(slotId, out var queuedTarget)
                ? queuedTarget
                : _buildingLastQueuedTargetBySlot.TryGetValue(slotId, out var lastTarget)
                    ? lastTarget.Target
                    : null;
            var pendingConstruct = queuedConstructsBySlot.TryGetValue(slotId, out var queuedConstruct)
                ? queuedConstruct
                : _buildingLastQueuedConstructBySlot.TryGetValue(slotId, out var lastConstruct)
                    ? lastConstruct.Name
                    : string.Empty;

            if (!BuildingSlotLayoutById.TryGetValue(slotId, out var layout))
            {
                layout = (0d, 0d);
            }

            if (occupied)
            {
                occupiedCount += 1;
                if (pendingTarget is int pendingQueuedTarget && pendingQueuedTarget <= (building!.Level ?? 0))
                {
                    pendingTarget = null;
                }

                pendingConstruct = string.Empty;
            }
            else
            {
                pendingTarget = null;
                if (projectedBuildingBySlot.TryGetValue(slotId, out var projected)
                    && (projected.Gid ?? 0) > 0
                    && string.IsNullOrWhiteSpace(pendingConstruct))
                {
                    pendingConstruct = projected.Name;
                }
            }

            string slotName;
            int? slotLevel;
            int? slotGid;
            var isWallSlot = slotId == 40;
            var isRallyPointSlot = IsRallyPointSlot(slotId);
            if (occupied)
            {
                slotName = building!.Name;
                slotLevel = building.Level;
                slotGid = building.Gid;
            }
            else if (isWallSlot || isRallyPointSlot)
            {
                slotName = isRallyPointSlot
                    ? "Rally Point"
                    : BuildingCatalogService.WallForTribe(status.Tribe)?.Name ?? "Wall";
                slotLevel = 0;
                slotGid = null;
            }
            else
            {
                slotName = "Empty";
                slotLevel = null;
                slotGid = null;
            }

            // If a demolish has actually completed (slot is now empty), drop the in-progress flag.
            if (!occupied && _buildingDemolishingSlots.Contains(slotId))
            {
                _buildingDemolishingSlots.Remove(slotId);
            }

            var row = new BuildingSlotRow
            {
                SlotId = slotId,
                Name = slotName,
                Level = slotLevel,
                Gid = slotGid,
                Category = category,
                Requirements = requirements,
                PendingTargetLevel = pendingTarget,
                PendingConstructName = pendingConstruct,
                IsDemolishing = _buildingDemolishingSlots.Contains(slotId),
                MapLeft = layout.Left,
                MapTop = layout.Top,
                IsWallSlot = isWallSlot,
                IsRallyPointSlot = isRallyPointSlot,
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

    private static bool IsEmptyBuilding(Building building)
    {
        return (building.Gid ?? 0) <= 0
            && ((building.Level ?? 0) <= 0
                || string.IsNullOrWhiteSpace(building.Name)
                || string.Equals(building.Name, "Empty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(building.Name, "g0", StringComparison.OrdinalIgnoreCase));
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

    private List<BuildingRequirementEntry> MissingRequirements(VillageStatus status, IReadOnlyList<BuildingRequirementEntry> requirements)
    {
        var projectedStatus = BuildProjectedBuildingStatus(status);
        var missing = new List<BuildingRequirementEntry>();
        foreach (var requirement in requirements)
        {
            var fromBuildings = projectedStatus.Buildings
                .Where(item => item.Level is not null && item.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Level!.Value)
                .DefaultIfEmpty(0)
                .Max();
            var fromResourceFields = status.ResourceFields
                .Where(item => item.Level is not null
                    && (item.Name.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase)
                        || (item.FieldType?.Contains(requirement.Name, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Select(item => item.Level!.Value)
                .DefaultIfEmpty(0)
                .Max();
            var fromUiResourceRows = MaxLevelInUiResourceRows(requirement.Name);
            var level = Math.Max(Math.Max(fromBuildings, fromResourceFields), fromUiResourceRows);
            if (level < requirement.Level)
            {
                missing.Add(requirement);
            }
        }

        return missing;
    }

    private int MaxLevelInUiResourceRows(string requirementName)
    {
        // Resource field requirements (Cropland, Iron Mine, Clay Pit, Woodcutter) live on the
        // Resources tab — buildings snapshot doesn't carry them. Look them up directly.
        IEnumerable<ResourceFieldRow> rows = requirementName switch
        {
            var n when n.Contains("Wood", StringComparison.OrdinalIgnoreCase) => _woodFields,
            var n when n.Contains("Clay", StringComparison.OrdinalIgnoreCase) => _clayFields,
            var n when n.Contains("Iron", StringComparison.OrdinalIgnoreCase) => _ironFields,
            var n when n.Contains("Crop", StringComparison.OrdinalIgnoreCase) => _croplandFields,
            _ => [],
        };
        return rows
            .Where(r => r.Level is not null)
            .Select(r => r.Level!.Value)
            .DefaultIfEmpty(0)
            .Max();
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

    private void SyncFarmListSelectionHandlers()
    {
        foreach (var row in _farmLists)
        {
            row.PropertyChanged -= FarmListStatusRow_PropertyChanged;
            row.PropertyChanged += FarmListStatusRow_PropertyChanged;
        }
    }

    private void FarmListStatusRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressFarmListUiRefresh)
        {
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(FarmListStatusRow.IsEnabled), StringComparison.Ordinal))
        {
            return;
        }

        PersistContinuousFarmListSelectionToConfig();
        UpdateAutomationLoopRunningIndicators();
        UpdateFarmingUiState();
    }

    private IReadOnlySet<string> LoadConfiguredContinuousFarmListNames()
    {
        try
        {
            var options = LoadBotOptions();
            return options.ContinuousFarmListNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void PersistContinuousFarmListSelectionToConfig()
    {
        try
        {
            var selectedNames = _farmLists
                .Where(item => item.IsEnabled)
                .Select(item => item.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.ContinuousFarmListNames] = new JsonArray(selectedNames.Select(name => JsonValue.Create(name)!).ToArray());
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not save selected farmlists: {ex.Message}");
        }
    }

    private void SetFarmingOperationBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetFarmingOperationBusy(busy));
            return;
        }

        if (busy)
        {
            EnsureManualExecutionTracking();
        }

        _farmingOperationBusy = busy;
        try
        {
            SyncFarmingControlsEnabledState();
            UpdateExecutionStateIndicator();
        }
        finally
        {
            if (!busy)
            {
                CompleteManualExecutionTrackingIfNeeded();
            }
        }
    }

    private void SetFarmingFunctionRunning(bool running)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetFarmingFunctionRunning(running));
            return;
        }

        try
        {
            if (running)
            {
                EnsureManualExecutionTracking();
            }

            UpdateExecutionStateIndicator();
        }
        finally
        {
            if (!running)
            {
                CompleteManualExecutionTrackingIfNeeded();
            }
        }
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
            ManualFarmingStateTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
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
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_startContinuousLoopAfterQueueStop
                        && ContinuousRunToggleButton?.IsChecked == true
                        && _isLoggedIn
                        && !_uiBusy
                        && !_autoQueueRunning
                        && (_loopTask is null || _loopTask.IsCompleted))
                    {
                        _startContinuousLoopAfterQueueStop = false;
                        StartContinuousLoopRunner();
                        return;
                    }

                    _startContinuousLoopAfterQueueStop = false;
                });
            }
        });
    }

    private void TriggerQueueAutoRunFromEnqueue()
    {
        // When continuous-run is toggled ON, queued items must NOT auto-start from an enqueue.
        // They may only begin when the user presses "Start bot", or be picked up by a runner
        // that is already executing (the existing ExecuteQueuedItemsNowAsync / loop will see
        // new items on its next iteration).
        if (ContinuousRunToggleButton?.IsChecked == true)
        {
            var alreadyRunning = _autoQueueRunning
                || (_loopTask is not null && !_loopTask.IsCompleted);

            if (!alreadyRunning)
            {
                return;
            }
        }

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

    private IReadOnlyList<QueueGroup> GetContinuousLoopEnabledGroupsInOrder()
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(GetContinuousLoopEnabledGroupsInOrder);
        }

        return _automationLoopTasks
            .Where(item => item.IsEnabled)
            .Select(item => QueueGroupCatalog.TryParse(item.TaskName, out var group) ? group : (QueueGroup?)null)
            .Where(group => group.HasValue)
            .Select(group => group!.Value)
            .ToList();
    }

    private async Task EnsureContinuousLoopRuntimeItemsAsync(BotOptions options)
    {
        var enabledGroups = GetContinuousLoopEnabledGroupsInOrder();
        if (enabledGroups.Count <= 0)
        {
            return;
        }

        var activeItems = _botService.GetQueueItemsForDisplay()
            .Where(item => item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused)
            .ToList();

        bool HasActiveTask(string taskName)
        {
            return activeItems.Any(item =>
                string.Equals(item.TaskName, taskName, StringComparison.OrdinalIgnoreCase));
        }

        if (enabledGroups.Contains(QueueGroup.Hero) && !IsHeroGroupBlocked() && !HasActiveTask("hero_manage"))
        {
            var adventureCount = await _botService.RefreshAdventureCountAsync(options, AppendLog, CancellationToken.None);
            await Dispatcher.InvokeAsync(() => ApplyHeroAdventureAvailability(adventureCount));
            if (adventureCount is > 0)
            {
                _botService.EnqueueRuntime("hero_manage", "Hero adventure", null, priority: -50, maxRetries: 0);
            }
        }

        if (enabledGroups.Contains(QueueGroup.Troops) && !IsTroopsGroupBlocked() && !HasActiveTask("upgrade_troops_at_smithy"))
        {
            _botService.EnqueueRuntime("upgrade_troops_at_smithy", "Troop upgrades", null, priority: -50, maxRetries: 0);
        }

        if (enabledGroups.Contains(QueueGroup.TroopTraining) && !HasActiveTask("build_troops"))
        {
            _botService.EnqueueRuntime("build_troops", "Build troops", null, priority: -50, maxRetries: 0);
        }

        if (enabledGroups.Contains(QueueGroup.Farming) && !IsFarmingGroupBlocked() && !HasActiveTask("send_farmlists"))
        {
            var goldClubEnabled = await _botService.ReadAndPersistGoldClubStatusAsync(options, AppendLog, CancellationToken.None);
            UpdateGoldClubInfo(goldClubEnabled);
            if (goldClubEnabled)
            {
                await EnsureContinuousFarmListsReadyAsync(options);
                var selectedFarmLists = Dispatcher.CheckAccess()
                    ? _farmLists
                        .Where(item => item.IsEnabled)
                        .Select(item => item.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : Dispatcher.Invoke(() => _farmLists
                        .Where(item => item.IsEnabled)
                        .Select(item => item.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());
                var availableFarmListCount = Dispatcher.CheckAccess()
                    ? _farmLists.Count
                    : Dispatcher.Invoke(() => _farmLists.Count);
                if (availableFarmListCount <= 0)
                {
                    SetFarmingBlockedState(FarmingBlockedReasonNoFarmLists, "No farmlists available");
                }
                else
                {
                    if (string.Equals(_farmingBlockedReasonKey, FarmingBlockedReasonNoFarmLists, StringComparison.OrdinalIgnoreCase))
                    {
                        ClearFarmingBlockedState();
                    }
                }

                if (selectedFarmLists.Count > 0)
                {
                    var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [BotOptionPayloadKeys.ContinuousFarmListNames] = string.Join(",", selectedFarmLists),
                        [BotOptionPayloadKeys.ContinuousFarmDispatchDelayMinutes] = options.ContinuousFarmDispatchDelayMinutes.ToString(),
                    };
                    _botService.EnqueueRuntime("send_farmlists", "Send selected farmlists", payload, priority: -50, maxRetries: 0);
                }
            }
            else
            {
                SetFarmingBlockedState(FarmingBlockedReasonNoGoldClub, "No goldclub");
            }
        }
    }

    private async Task EnsureContinuousLoopConstructionStatusAsync(BotOptions options, CancellationToken cancellationToken)
    {
        if (!_continuousLoopConstructionStatusNeedsSync
            || !GetContinuousLoopEnabledGroupsInOrder().Contains(QueueGroup.Construction))
        {
            return;
        }

        try
        {
            var status = await ReadVillageStatusWithRetryAsync(options, cancellationToken, resourceOnly: false);
            await Dispatcher.InvokeAsync(() =>
            {
                _lastBuildingStatus = status;
                ApplyVillageStatusToUi(status);
                PopulateBuildingsTab(status);
            });
            _continuousLoopConstructionStatusNeedsSync = false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"Continuous construction status sync failed: {ex.Message}");
        }
    }

    private async Task EnsureContinuousFarmListsReadyAsync(BotOptions options)
    {
        var farmingEnabled = GetContinuousLoopEnabledGroupsInOrder().Contains(QueueGroup.Farming);
        if (!farmingEnabled || _farmingOperationBusy)
        {
            return;
        }

        var farmSnapshot = Dispatcher.CheckAccess()
            ? new
            {
                TotalCount = _farmLists.Count,
                SelectedNames = _farmLists.Where(item => item.IsEnabled).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
                AvailableNames = _farmLists.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            }
            : await Dispatcher.InvokeAsync(() => new
            {
                TotalCount = _farmLists.Count,
                SelectedNames = _farmLists.Where(item => item.IsEnabled).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
                AvailableNames = _farmLists.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList(),
            });

        var needsAnalyze = farmSnapshot.TotalCount <= 0
            || farmSnapshot.SelectedNames.Count <= 0
            || farmSnapshot.SelectedNames.Any(name => !farmSnapshot.AvailableNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            || _lastFarmListsAnalysisAt == DateTimeOffset.MinValue;

        if (!needsAnalyze)
        {
            return;
        }

        AppendLog("Continuous farming: analyzing farmlists before runtime send.");
        try
        {
            await RefreshFarmListsFromServerAsync(options, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog($"Continuous farming analyze failed: {ex.Message}");
        }
    }

    private QueueItem? SelectNextQueueItemForContinuousLoop()
    {
        var orderedGroups = GetContinuousLoopEnabledGroupsInOrder().ToList();
        if (orderedGroups.Count <= 0)
        {
            return null;
        }

        var queueItems = _botService.GetQueueItemsForDisplay();
        var now = DateTimeOffset.UtcNow;
        foreach (var group in orderedGroups)
        {
            if (group == QueueGroup.Construction && !IsConstructionGroupReady())
            {
                continue;
            }

            var orderedGroupItems = OrderContinuousLoopGroupItems(
                queueItems.Where(item =>
                    item.Group == group &&
                    item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused));
            var head = orderedGroupItems.FirstOrDefault();
            if (head is null)
            {
                continue;
            }

            if (head.Status != QueueStatus.Pending || head.NextAttemptAt > now)
            {
                continue;
            }

            return head;
        }

        return null;
    }

    private async Task RunContinuousLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_loopStopRequested)
            {
                AppendLog("Loop stop requested. Exiting after current action.");
                break;
            }

            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var loopDelaySeconds = Math.Max(5, options.LoopIntervalSeconds);
            var tickId = Interlocked.Increment(ref _loopTickCounter);
            var tickSw = Stopwatch.StartNew();
            try
            {
                AppendLog($"[LOOP {tickId}] START interval={loopDelaySeconds}s, headless={options.Headless}");
                await EnsureChromiumInstalledAsync();
                await EnsureContinuousLoopConstructionStatusAsync(options, token);
                await EnsureContinuousLoopRuntimeItemsAsync(options);

                var next = SelectNextQueueItemForContinuousLoop();
                if (next is not null)
                {
                    var terminalCountBefore = await Dispatcher.InvokeAsync(() => _terminalEntries.Count);
                    AppendLog($"[LOOP {tickId}] PICK group={next.Group}, task={next.TaskName}, retries={next.Retries}/{next.MaxRetries}");
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
                        if (NeedsConstructionStatusRefresh(next.TaskName))
                        {
                            await RefreshConstructionStatusAsync(token);
                        }
                        else if (string.Equals(next.TaskName, "load_buildings_snapshot", StringComparison.OrdinalIgnoreCase))
                        {
                            await LoadBuildingsSnapshotIntoUiAsync(token);
                        }
                        else if (string.Equals(next.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, token);
                                await Dispatcher.InvokeAsync(() => ApplyHeroAttributeSnapshotToUi(snapshot));
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"Hero stats refresh after run failed: {ex.Message}");
                            }
                        }
                        else if (string.Equals(next.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await RefreshTroopTrainingQueuesAsync(options, token, _lastBuildingStatus?.Buildings, refreshBuildingsBeforeRead: true);
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"Troop queue refresh after run failed: {ex.Message}");
                            }
                        }

                        AppendLog($"[LOOP {tickId}] OK {tickSw.Elapsed.TotalSeconds:F1}s | queue:{next.TaskName}");
                        _ = Dispatcher.BeginInvoke(() => LastScanInfoTextBlock.Text = $"Last scan: {GetServerNow():HH:mm:ss}");
                    }
                    catch (OperationCanceledException)
                    {
                        _botService.MarkQueueItemDeferred(next.Id, TimeSpan.Zero);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (await TryHandleTroopsBlockedExecutionAsync(next, ex, $"[LOOP {tickId}]"))
                        {
                            continue;
                        }

                        if (TryExtractQueueWaitDelay(ex.Message, out var queueWaitDelay))
                        {
                            if (IsConstructionQueueTask(next.TaskName))
                            {
                                await Dispatcher.InvokeAsync(() => ApplyConstructionInlineWait(queueWaitDelay));
                            }

                            var deferred = _botService.MarkQueueItemDeferred(next.Id, queueWaitDelay);
                            if (deferred)
                            {
                                ScheduleDeferredBuildingsMidWaitRefresh(next, queueWaitDelay);
                                ScheduleDeferredResourcesMidWaitRefresh(next, queueWaitDelay);
                                AppendLog($"[LOOP {tickId}] DEFER {tickSw.Elapsed.TotalSeconds:F1}s task={next.TaskName} | next try in {queueWaitDelay.TotalSeconds:F0}s");
                            }
                            else
                            {
                                _botService.MarkQueueItemExecutionFailed(next.Id);
                                AppendLog($"[LOOP {tickId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
                                RaiseAlarmIfQueueItemPermanentlyFailed(next, ex.Message);
                            }
                        }
                        else
                        {
                            _botService.MarkQueueItemExecutionFailed(next.Id);
                            AppendLog($"[LOOP {tickId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
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
                    var waitDelay = ResolveContinuousLoopWaitDelay(loopDelaySeconds);
                    AppendLog($"[LOOP {tickId}] WAIT {waitDelay.TotalSeconds:F0}s");
                    await Task.Delay(waitDelay, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppendLog($"[LOOP {tickId}] FAIL {tickSw.Elapsed.TotalSeconds:F1}s | {FormatExceptionForLog(ex)}");
                await Task.Delay(TimeSpan.FromSeconds(loopDelaySeconds), token);
            }
        }
    }

    private TimeSpan ResolveContinuousLoopWaitDelay(int fallbackSeconds)
    {
        try
        {
            if (GetContinuousLoopEnabledGroupsInOrder().Count <= 0)
            {
                return TimeSpan.FromSeconds(Math.Max(5, fallbackSeconds));
            }

            var now = DateTimeOffset.UtcNow;
            var nextDeferred = GetContinuousLoopRelevantQueueItems()
                .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                .OrderBy(item => item.NextAttemptAt)
                .FirstOrDefault();
            if (nextDeferred is null)
            {
                return TimeSpan.FromSeconds(Math.Min(fallbackSeconds, ContinuousLoopMaxSleepSliceSeconds));
            }

            var delay = nextDeferred.NextAttemptAt - now;
            if (delay < TimeSpan.FromSeconds(1))
            {
                return TimeSpan.FromSeconds(1);
            }

            var maxSlice = TimeSpan.FromSeconds(ContinuousLoopMaxSleepSliceSeconds);
            return delay <= maxSlice ? delay : maxSlice;
        }
        catch
        {
            return TimeSpan.FromSeconds(Math.Min(fallbackSeconds, ContinuousLoopMaxSleepSliceSeconds));
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
                    .Where(item => !item.IsRuntimeOnly && item.Status == QueueStatus.Pending)
                    .FirstOrDefault(item => item.NextAttemptAt > now);

                if (nextDeferredItem is null)
                {
                    nextDeferredItem = _botService
                        .GetQueueItemsForDisplay()
                        .Where(item => item.Status == QueueStatus.Pending && item.NextAttemptAt > now)
                        .OrderBy(item => item.NextAttemptAt)
                        .FirstOrDefault();
                }

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
                if (IsResourceUpgradeTask(next.TaskName))
                {
                    var fastUpdated = await TryApplyFastResourceLevelUpdateAsync(next.TaskName, terminalCountBefore);
                    if (!fastUpdated)
                    {
                        await LoadResourcesAfterUpgradeAsync(cancellationToken, resourceOnly: true);
                    }
                }
                if (NeedsConstructionStatusRefresh(next.TaskName))
                {
                    await RefreshConstructionStatusAsync(cancellationToken);
                }
                else if (string.Equals(next.TaskName, "hero_manage", StringComparison.OrdinalIgnoreCase))
                {
                    // Same post-run refresh the loop runner does — read the authoritative attributes-tab
                    // snapshot off the UI thread and marshal the UI write back via the dispatcher,
                    // so the Hero / Adventures card mirrors what Travian shows after the run.
                    try
                    {
                        var snapshot = await _botService.ReadHeroAttributesAsync(options, AppendLog, cancellationToken);
                        await Dispatcher.InvokeAsync(() => ApplyHeroAttributeSnapshotToUi(snapshot));
                    }
                    catch (Exception refreshEx)
                    {
                        AppendLog($"Hero stats refresh after run failed: {refreshEx.Message}");
                    }
                }
                else if (string.Equals(next.TaskName, "build_troops", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await RefreshTroopTrainingQueuesAsync(options, cancellationToken, _lastBuildingStatus?.Buildings, refreshBuildingsBeforeRead: true);
                    }
                    catch (Exception refreshEx)
                    {
                        AppendLog($"Troop queue refresh after run failed: {refreshEx.Message}");
                    }
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
                if (await TryHandleTroopsBlockedExecutionAsync(next, ex, $"[AUTOQ {runId}]"))
                {
                    continue;
                }

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
                    if (IsConstructionQueueTask(next.TaskName))
                    {
                        await Dispatcher.InvokeAsync(() => ApplyConstructionInlineWait(queueWaitDelay));
                    }

                    var deferred = _botService.MarkQueueItemDeferred(next.Id, queueWaitDelay);
                    if (deferred)
                    {
                        ScheduleDeferredBuildingsMidWaitRefresh(next, queueWaitDelay);
                        ScheduleDeferredResourcesMidWaitRefresh(next, queueWaitDelay);
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
                if (IsBuildingMutationTask(next.TaskName))
                {
                    try
                    {
                        await LoadBuildingsSnapshotIntoUiAsync(cancellationToken);
                    }
                    catch
                    {
                        // Ignore snapshot reload errors in finally — the UI keeps the previous state.
                    }
                }
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
            var logLinesForSessionLog = new List<string>(MaxLogLinesPerFlush * 2);
            var alarmLinesForSessionLog = new List<string>(MaxLogLinesPerFlush);
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
                    logLinesForSessionLog.Add(line);
                    TryApplyInlineResourceLevelUpdateFromLog(part);
                    TryApplyPlusStatusFromLog(part);
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
                        var isAcknowledgedAlarm = IsAutoAcknowledgedAlarmMessage(part);
                        _alarmEntries.Insert(0, new AlarmEntryRow
                        {
                            Text = line,
                            IsAcknowledged = isAcknowledgedAlarm,
                        });
                        if (!isAcknowledgedAlarm)
                        {
                            _unacknowledgedAlarmCount += 1;
                        }

                        alarmLinesForSessionLog.Add(line);
                    }

                    if (IsCaptchaSessionStartMessage(part) && !_captchaSessionActive)
                    {
                        _captchaSessionSeenCount += 1;
                        _captchaSessionActive = true;
                    }

                    if (IsCaptchaAutoSolveAttemptMessage(part))
                    {
                        ShowCaptchaAutoSolvePopup();
                    }

                    if (IsManualVerificationAlarmMessage(part))
                    {
                        _manualVerificationAlarmActive = true;
                    }

                    if (_manualVerificationAlarmActive && IsManualVerificationResolvedMessage(part))
                    {
                        AcknowledgeAllAlarmEntries();
                        _manualVerificationAlarmActive = false;
                    }

                    if (_captchaSessionActive && IsCaptchaSolvedAutomaticallyMessage(part))
                    {
                        _captchaSessionSolvedCount += 1;
                        _captchaSessionActive = false;
                        AcknowledgeAllAlarmEntries();
                        CloseCaptchaAutoSolvePopup();
                    }
                    else if (_captchaSessionActive && IsManualVerificationResolvedMessage(part))
                    {
                        _captchaSessionActive = false;
                        CloseCaptchaAutoSolvePopup();
                    }

                    if (part.Contains("manual verification appeared", StringComparison.OrdinalIgnoreCase)
                        || part.Contains("captcha/manual", StringComparison.OrdinalIgnoreCase)
                        || IsCaptchaAutoSolveFailedMessage(part))
                    {
                        CloseCaptchaAutoSolvePopup();
                        ShowManualVerificationPopup(_browserSessionLikelyOpen);
                    }
                }
            }

            TrimToMaxEntries(_terminalEntries, 1000);
            TrimToMaxEntries(_alarmEntries, 200);
            UpdateCaptchaStatsUi();
            TryAppendSessionLogLines(logLinesForSessionLog, alarmLinesForSessionLog);

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
                TrimOldSessionLogFiles(sessionLogDirectory);
            }

            var header = new[]
            {
                "=== Tbot Ultra Session Log ===",
                $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"ProjectRoot: {_projectRoot}",
                $"AppVersion: {ReadAppVersionForLog()}",
                $"MachineName: {Environment.MachineName}",
                $"UserName: {Environment.UserName}",
                $"OS: {RuntimeInformation.OSDescription}",
                $"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}",
                $"DotNet: {RuntimeInformation.FrameworkDescription}",
                $"CPU: {ReadCpuDescriptionForLog()}",
                $"LogicalProcessors: {Environment.ProcessorCount}",
                $"RAM: {ReadRamDescriptionForLog()}",
                $"Screen: {(int)SystemParameters.PrimaryScreenWidth}x{(int)SystemParameters.PrimaryScreenHeight}",
                string.Empty,
                "=== ALARMS ===",
                string.Empty,
                "=== LOGS ===",
                string.Empty,
            };
            lock (_sessionLogWriteSync)
            {
                File.WriteAllLines(_sessionLogPath, header);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not initialize session log file: {ex}");
        }
    }

    private void TrimOldSessionLogFiles(string sessionLogDirectory)
    {
        try
        {
            var oldFiles = new DirectoryInfo(sessionLogDirectory)
                .GetFiles("TbotUltra_Log_*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.CreationTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxSessionLogFiles - 1)
                .ToList();

            foreach (var file in oldFiles)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not delete old session log '{file.FullName}': {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not trim old session logs: {ex}");
        }
    }

    private string ReadAppVersionForLog()
    {
        try
        {
            var version = File.Exists(_versionPath)
                ? File.ReadAllText(_versionPath).Trim()
                : "dev";
            return string.IsNullOrWhiteSpace(version) ? "dev" : version;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ReadCpuDescriptionForLog()
    {
        try
        {
            var identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                return identifier.Trim();
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static string ReadRamDescriptionForLog()
    {
        try
        {
            var memoryStatus = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };

            if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.TotalPhys == 0)
            {
                return "unknown";
            }

            var totalBytes = memoryStatus.TotalPhys;
            if (totalBytes > 0)
            {
                var totalGb = totalBytes / (1024d * 1024d * 1024d);
                return $"{totalGb:F1} GB";
            }
        }
        catch
        {
        }

        return "unknown";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx memoryStatus);

    private void TryAppendSessionLogLines(IReadOnlyList<string> logLines, IReadOnlyList<string> alarmLines)
    {
        if (logLines.Count <= 0 && alarmLines.Count <= 0)
        {
            return;
        }

        try
        {
            lock (_sessionLogWriteSync)
            {
                if (alarmLines.Count > 0)
                {
                    _sessionAlarmLines.AddRange(alarmLines);
                }

                if (logLines.Count > 0)
                {
                    _sessionLogLines.AddRange(logLines);
                }

                var content = new List<string>(_sessionAlarmLines.Count + _sessionLogLines.Count + 8)
                {
                    "=== Tbot Ultra Session Log ===",
                    $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"ProjectRoot: {_projectRoot}",
                    string.Empty,
                    "=== ALARMS ===",
                };

                content.AddRange(_sessionAlarmLines);
                content.Add(string.Empty);
                content.Add("=== LOGS ===");
                content.AddRange(_sessionLogLines);
                content.Add(string.Empty);

                File.WriteAllLines(_sessionLogPath, content);
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
                AcknowledgeAllAlarmEntries();
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

    private void ShowCaptchaAutoSolvePopup()
    {
        if (_captchaAutoSolvePopup is { IsVisible: true })
        {
            return;
        }

        _captchaAutoSolvePopup = AppDialog.ShowModeless(
            this,
            "Captcha detected. Tbot Ultra is trying to solve it automatically. This can take up to about one minute.",
            "Solving captcha",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        _captchaAutoSolvePopup.Closed += (_, _) => _captchaAutoSolvePopup = null;
    }

    private void CloseCaptchaAutoSolvePopup()
    {
        if (_captchaAutoSolvePopup is null)
        {
            return;
        }

        var popup = _captchaAutoSolvePopup;
        _captchaAutoSolvePopup = null;
        popup.Close();
    }

    private static void TrimToMaxEntries<T>(ObservableCollection<T> entries, int max)
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

    private static bool IsCaptchaAutoSolveAttemptMessage(string message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("Captcha auto-solve attempt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCaptchaAutoSolveFailedMessage(string message)
    {
        return !string.IsNullOrWhiteSpace(message)
            && message.Contains("Captcha auto-solve failed", StringComparison.OrdinalIgnoreCase)
            && message.Contains("manual verification", StringComparison.OrdinalIgnoreCase);
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
        if (IsAutoAcknowledgedAlarmMessage(message))
        {
            return true;
        }

        if (value.Contains(" started]"))
        {
            return false;
        }

        if (value.Contains("[completed]"))
        {
            return false;
        }

        if (value.Contains("manual farming loop") && value.Contains(" restarting"))
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

    private static bool IsAutoAcknowledgedAlarmMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return value.Contains("chromium warmup")
            || value.Contains("captcha warmup")
            || (value.Contains("hero_adventure.php")
                && value.Contains("transient navigation context error")
                && value.Contains("retrying"))
            || value.Contains("the calling thread cannot access this object because a different thread owns it")
            || (value.Contains("ui sync snapshot failed")
                && value.Contains("execution context was destroyed"));
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

        AcknowledgeAllAlarmEntries();
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
        var selectedLines = alertsTabSelected
            ? list.SelectedItems.Cast<AlarmEntryRow>().Select(item => item.Text).ToList()
            : list.SelectedItems.Cast<string>().ToList();
        var source = alertsTabSelected ? _alarmEntries.Select(item => item.Text).ToList() : _terminalEntries.ToList();
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

    private void AcknowledgeAllAlarmEntries()
    {
        foreach (var entry in _alarmEntries)
        {
            entry.IsAcknowledged = true;
        }

        _unacknowledgedAlarmCount = 0;
        AlarmListBox.Items.Refresh();
        _logsPopupAlarmList?.Items.Refresh();
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
        popupAlarmList.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding(nameof(AlarmEntryRow.Text)));
        popupAlarmList.ItemTemplate.VisualTree.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var popupAlarmStyle = new Style(typeof(TextBlock));
        popupAlarmStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(252, 165, 165))));
        popupAlarmStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(nameof(AlarmEntryRow.IsAcknowledged)),
            Value = true,
            Setters =
            {
                new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(147, 197, 253))),
            }
        });
        popupAlarmList.ItemTemplate.VisualTree.SetValue(TextBlock.StyleProperty, popupAlarmStyle);

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
            AcknowledgeAllAlarmEntries();
            UpdateTerminalAlarmUi();
        };

        var copyButton = new Button { Content = "Copy", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), Height = 30 };
        copyButton.Click += (_, _) =>
        {
            var selected = popupTab.SelectedIndex == 1
                ? popupAlarmList.SelectedItems.Cast<AlarmEntryRow>().Select(item => item.Text).ToList()
                : popupLogList.SelectedItems.Cast<string>().ToList();
            var lines = selected.Count > 0
                ? selected
                : (popupTab.SelectedIndex == 1 ? _alarmEntries.Select(item => item.Text).ToList() : _terminalEntries.ToList());
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
            [BotOptionPayloadKeys.ResourceUpgradeName] = rowName,
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

    private void SetLoopStateBadge(string stateText, Color color, string startButtonText)
    {
        LoopStateTextBlock.Text = $"State: {stateText}";
        LoopStateBadge.Background = new SolidColorBrush(color);
        StartLoopButton.Content = startButtonText;
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
                StartLoopButton.Content = (loopRunning || _autoQueueRunning) ? "Pause bot" : "Start bot";
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
        if (UpdateTroopTrainingTroopOptions(status.Tribe))
        {
            PersistTroopTrainingConfig();
        }
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
        ApplyTroopTrainingStatusToUi(status);
        UpdateBuildQueueStatusText();
        UpdateAutomationLoopRunningIndicators();
        RefreshVillagePicker(status);
    }

    private void ApplyResourceStatusToUi(VillageStatus status)
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

            TribeInfoTextBlock.Text = $"Tribe: {status.Tribe}";
            VillagesInfoTextBlock.Text = $"Villages: {status.VillageCount}";
            RefreshVillagePickerFromVillages(status.Villages, selectedVillage.Name);
            UpdateDashboardVillageList(status.Villages);

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
        var items = BuildMergedVillageSelectionItems(villages)
            .OrderByDescending(v => v.IsCapital)
            .ThenByDescending(v => v.Population ?? -1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DashboardVillageList.ItemsSource = items;
    }

    private void RefreshVillagePickerFromVillages(IReadOnlyList<Village> villages, string? preferredVillageName)
    {
        var currentSelectedName = string.IsNullOrWhiteSpace(preferredVillageName)
            ? GetSelectedVillageName()
            : preferredVillageName;

        var items = BuildMergedVillageSelectionItems(villages);

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

    private List<VillageSelectionItem> BuildMergedVillageSelectionItems(IReadOnlyList<Village> villages)
    {
        var existingVillageData = Enumerable.Empty<VillageSelectionItem>()
            .Concat(VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem> ?? [])
            .Concat(DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem> ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return villages
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v =>
            {
                existingVillageData.TryGetValue(v.Name!, out var existing);
                return new VillageSelectionItem
                {
                    Name = v.Name!,
                    Url = string.IsNullOrWhiteSpace(v.Url) ? existing?.Url ?? string.Empty : v.Url,
                    IsCapital = v.IsCapital ?? existing?.IsCapital ?? false,
                    CoordX = v.CoordX ?? existing?.CoordX,
                    CoordY = v.CoordY ?? existing?.CoordY,
                    Population = v.Population ?? existing?.Population,
                    CropFields = v.CropFields ?? existing?.CropFields,
                };
            })
            .ToList();
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
