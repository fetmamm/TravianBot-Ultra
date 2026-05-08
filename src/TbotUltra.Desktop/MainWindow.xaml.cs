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
    private readonly LoopController _loopController;
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

        _loopController = App.Services.GetRequiredService<LoopController>();
        _loopController.Logger = AppendLog;

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
        _loopController.ClearLoopStopRequest();
        _loopController.ClearQueueStopRequest();
        _continuousLoopConstructionStatusNeedsSync = true;
        _loopCts = _loopController.CreateCts("loop");
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
            _loopController.RequestLoopStop();
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
