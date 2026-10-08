using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Infrastructure;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int OfficialFarmListCapacity = 100;
    private const int MaxFarmsPerFarmList = 120;
    private IPage _page;
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly bool _interactive;
    private readonly bool _browserVisible;
    private readonly string _projectRoot;
    private readonly string _capitalCachePath;
    private readonly HeroAttributeSnapshotStore _heroAttributeSnapshotStore;
    private readonly HeroInventorySnapshotStore _heroInventorySnapshotStore;
    private readonly HeroOintmentAvailabilityStore _heroOintmentAvailabilityStore;
    private readonly WatchtowerSnapshotStore _watchtowerSnapshotStore;
    private readonly Action<string>? _statusCallback;
    private readonly Action<VerifiedActiveVillage>? _activeVillageVerified;
    private readonly Action<ConstructionQueueObservation>? _constructionQueueObserved;
    private readonly BrowserTraceLogger _browserTrace;
    // Flips the browser session's consentmanager route block on/off; used only by the bonus-video flow,
    // which needs GDPR/TCF consent while the rest of the session keeps it blocked (no stray sync tabs).
    private readonly Action<bool>? _setConsentDomainsAllowed;
    private readonly Action<bool>? _setManualAuthenticationPopupsAllowed;
    private readonly Func<IPage, CancellationToken, Task>? _cleanupAfterBonusVideoAsync;
    private readonly IIsolatedBonusVideoRunner _isolatedBonusVideoRunner;
    private readonly Func<string, CancellationToken, Task<IPage>>? _rotateAfterLobbyLoginAsync;
    private readonly Func<LobbyWorldSelectionRequest, CancellationToken, Task<string?>>? _lobbyWorldSelectionRequested;
    private readonly Func<ManualLoginConfirmationRequest, CancellationToken, Task<bool>>? _manualLoginConfirmationRequested;
    private readonly Func<LobbyWorldServerResolution, CancellationToken, Task>? _lobbyWorldServerResolved;
    private string? _resolvedServerUrl;
    private DateTimeOffset? _serverTimeUtc;
    private DateTimeOffset? _serverTimeObservedAtUtc;
    private string? _cachedAccountTribe;
    private readonly TravianSessionCache _session;
    private static readonly TimeSpan ResourceReadLogInterval = TimeSpan.FromMinutes(2);
    // These caches are backed by the shared session cache (_session) so they survive across the
    // short-lived TravianClient instances created per operation for the same browser session.
    private bool? _cachedTravianPlusActive { get => _session.CachedTravianPlusActive; set => _session.CachedTravianPlusActive = value; }
    private DateTimeOffset _cachedTribePlusAt { get => _session.CachedTribePlusAt; set => _session.CachedTribePlusAt = value; }
    private bool? _cachedGoldClubEnabled { get => _session.CachedGoldClubEnabled; set => _session.CachedGoldClubEnabled = value; }
    private int? _cachedGold { get => _session.CachedGold; set => _session.CachedGold = value; }
    private int? _cachedSilver { get => _session.CachedSilver; set => _session.CachedSilver = value; }
    private DateTimeOffset _cachedCurrencyAt { get => _session.CachedCurrencyAt; set => _session.CachedCurrencyAt = value; }
    private string? _accountTribe { get => _session.AccountTribe; set => _session.AccountTribe = value; }
    private CityStatus _lastBuildingOverviewCityStatus = CityStatus.Unknown;
    private CityCapability KnownCityCapability => _session.CityCapability;

    // Short-lived cache for ReadActiveConstructionsAsync. One upgrade-to-max iteration makes
    // several pre-click reads of the SAME dorf2 page state (e.g. ReadHighestKnownQueuedBuildingLevel
    // then CheckQueueOrDefer/EvaluateConstructionSlots). At 800ms these missed on slightly slow pages
    // and re-fetched, doubling the network round-trips per upgraded level. The TTL is sized to span a
    // single iteration's pre-click window so those collapse into one read. This is safe: every caller
    // that needs FRESH state after a click calls InvalidateActiveConstructionsCache() first
    // (e.g. WaitForBuildingLevelAdvanceAsync), and GotoAsync/ReloadOrGotoAsync invalidate automatically
    // on every navigation — so the cache is always re-seeded fresh at the top of each iteration.
    private IReadOnlyList<ActiveConstruction>? _cachedActiveConstructions
    {
        get => _session.CachedActiveConstructions;
        set => _session.CachedActiveConstructions = value;
    }
    private DateTimeOffset _cachedActiveConstructionsAt
    {
        get => _session.CachedActiveConstructionsAt;
        set => _session.CachedActiveConstructionsAt = value;
    }
    private bool _cachedActiveConstructionsFromOverview
    {
        get => _session.CachedActiveConstructionsFromOverview;
        set => _session.CachedActiveConstructionsFromOverview = value;
    }
    private string? _cachedActiveConstructionsVillageKey
    {
        get => _session.CachedActiveConstructionsVillageKey;
        set => _session.CachedActiveConstructionsVillageKey = value;
    }
    private bool _lastActiveConstructionsFromOverview;
    private static readonly TimeSpan ActiveConstructionsMutationCacheTtl = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan ActiveConstructionsObservationCacheTtl = TimeSpan.FromSeconds(30);
    private ConstructionNavigationDiagnostics? _constructionNavDiagnostics;

    internal void InvalidateActiveConstructionsCache()
    {
        _cachedActiveConstructions = null;
        _cachedActiveConstructionsFromOverview = false;
        _cachedActiveConstructionsVillageKey = null;
        _lastActiveConstructionsFromOverview = false;
        _browserTrace?.Event("CACHE", "active-constructions-invalidate", detail: "reason=page state changed");
    }

    private void NotifyResourceRead(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _session.LastResourceReadLogAt < ResourceReadLogInterval)
        {
            return;
        }

        _session.LastResourceReadLogAt = now;
        Notify(message);
    }

    private IDisposable BeginConstructionNavigationDiagnostics(string label)
    {
        if (_constructionNavDiagnostics is not null)
        {
            return NoopDisposable.Instance;
        }

        _constructionNavDiagnostics = new ConstructionNavigationDiagnostics(label);
        Notify($"[construction-nav] START {label}");
        return new ConstructionNavigationScope(this, _constructionNavDiagnostics);
    }

    private void RecordConstructionNavigation(string operation, string target)
    {
        var diagnostics = _constructionNavDiagnostics;
        if (diagnostics is null)
        {
            return;
        }

        var bucket = diagnostics.Record(operation, target);
        Notify($"[construction-nav:verbose] {operation} bucket={bucket} target='{target}'");
    }

    private void EndConstructionNavigationDiagnostics(ConstructionNavigationDiagnostics diagnostics)
    {
        if (!ReferenceEquals(_constructionNavDiagnostics, diagnostics))
        {
            return;
        }

        Notify(diagnostics.FormatSummary());
        _constructionNavDiagnostics = null;
    }

    private sealed class ConstructionNavigationDiagnostics
    {
        private readonly Dictionary<string, int> _byBucket = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _byOperation = new(StringComparer.OrdinalIgnoreCase);

        public ConstructionNavigationDiagnostics(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public int Total { get; private set; }

        public string Record(string operation, string target)
        {
            Total++;
            _byOperation[operation] = _byOperation.GetValueOrDefault(operation) + 1;
            var bucket = Classify(target);
            _byBucket[bucket] = _byBucket.GetValueOrDefault(bucket) + 1;
            return bucket;
        }

        public string FormatSummary()
        {
            string Count(string key) => _byBucket.TryGetValue(key, out var count) ? count.ToString(CultureInfo.InvariantCulture) : "0";
            string OperationCount(string key) => _byOperation.TryGetValue(key, out var count) ? count.ToString(CultureInfo.InvariantCulture) : "0";
            return $"[construction-nav] END {Label}: total={Total}, goto={OperationCount("goto")}, reload={OperationCount("reload")}, dorf1={Count("dorf1")}, dorf2={Count("dorf2")}, build={Count("build")}, other={Count("other")}";
        }

        private static string Classify(string target)
        {
            var path = target;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                path = uri.AbsolutePath;
            }

            path = path.ToLowerInvariant();
            if (path.EndsWith("/dorf1.php", StringComparison.Ordinal) || path.Contains("/dorf1.php?", StringComparison.Ordinal))
            {
                return "dorf1";
            }

            if (path.EndsWith("/dorf2.php", StringComparison.Ordinal) || path.Contains("/dorf2.php?", StringComparison.Ordinal))
            {
                return "dorf2";
            }

            if (path.EndsWith("/build.php", StringComparison.Ordinal) || path.Contains("/build.php?", StringComparison.Ordinal))
            {
                return "build";
            }

            return "other";
        }
    }

    private sealed class ConstructionNavigationScope : IDisposable
    {
        private TravianClient? _owner;
        private readonly ConstructionNavigationDiagnostics _diagnostics;

        public ConstructionNavigationScope(TravianClient owner, ConstructionNavigationDiagnostics diagnostics)
        {
            _owner = owner;
            _diagnostics = diagnostics;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndConstructionNavigationDiagnostics(_diagnostics);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose()
        {
        }
    }

    // Session-level cache for the villages list. Spieler.php is expensive to load, but the
    // prefer-cache path only re-reads the lightweight current-page sidebar (no navigation), so a
    // The sidebar is checked by lightweight refresh paths so newly founded villages and active-village
    // renames are discovered without navigating to the profile page.
    private static readonly TimeSpan EnsureLoggedInMinInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UiSyncMinInterval = TimeSpan.FromSeconds(20);
    private static readonly object ResourceStatusCacheSync = new();
    private static readonly object HeroAttributeSnapshotCacheSync = new();
    private static readonly Dictionary<string, CachedVillageResourceSnapshot> CachedVillageResourceSnapshotsByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HeroAttributeSnapshot> CachedHeroAttributeSnapshotsByKey = new(StringComparer.OrdinalIgnoreCase);
    // Villages list + population cache are backed by the shared session cache (_session) so the
    // spieler.php read survives across the per-operation clients (no duplicate startup navigation)
    // and the population baseline persists between operations.
    private List<Village>? _cachedVillages { get => _session.CachedVillages; set => _session.CachedVillages = value; }
    private DateTimeOffset _cachedVillagesAt { get => _session.CachedVillagesAt; set => _session.CachedVillagesAt = value; }
    // Tracks whether the villages list was read from the server (spieler.php) WITH population.
    // Reset to MinValue on a real village switch to force the next ReadVillagesAsync to re-read
    // population from spieler; otherwise the cache (kept current by incremental updates) is served.
    private DateTimeOffset _cachedVillagesPopulationAt { get => _session.CachedVillagesPopulationAt; set => _session.CachedVillagesPopulationAt = value; }
    private bool _villageListRequiresAuthoritativeUiSync { get => _session.VillageListRequiresAuthoritativeUiSync; set => _session.VillageListRequiresAuthoritativeUiSync = value; }
    // True once the population baseline has been read from spieler.php this session. Re-armed (set
    // false) on a real village switch so the next active village can seed its own baseline.
    private bool _populationBaselineRead { get => _session.PopulationBaselineRead; set => _session.PopulationBaselineRead = value; }
    // Backed by the shared session cache so the logged-in throttle survives across per-operation clients.
    private DateTimeOffset _lastEnsureLoggedInAt { get => _session.LastEnsureLoggedInAt; set => _session.LastEnsureLoggedInAt = value; }
    private DateTimeOffset _lastUiSyncAt = DateTimeOffset.MinValue;
    private bool _lastEnsureLoggedInSucceeded { get => _session.LastEnsureLoggedInSucceeded; set => _session.LastEnsureLoggedInSucceeded = value; }
    private int _suppressEnsureUiSyncDepth;
    private string? _productionUiSnapshotVillageKey;
    private IReadOnlyDictionary<string, double?>? _productionUiSnapshot;
    private sealed class CachedVillageResourceSnapshot
    {
        public IReadOnlyDictionary<string, double?> ProductionByHour { get; init; } = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ResourceField> ResourceFields { get; init; } = [];
        public long? WarehouseCapacity { get; init; }
        public long? GranaryCapacity { get; init; }
    }

    public TravianClient(
        IPage page,
        BotOptions config,
        AccountOptions account,
        bool interactive = true,
        bool browserVisible = true,
        string? projectRoot = null,
        TravianSessionCache? sessionCache = null,
        TravianClientCallbacks? callbacks = null,
        BrowserTraceLogger? browserTrace = null)
    {
        callbacks ??= new TravianClientCallbacks();
        _page = page;
        _config = config;
        _setConsentDomainsAllowed = callbacks.SetConsentDomainsAllowed;
        _setManualAuthenticationPopupsAllowed = callbacks.SetManualAuthenticationPopupsAllowed;
        _cleanupAfterBonusVideoAsync = callbacks.CleanupAfterBonusVideoAsync;
        _isolatedBonusVideoRunner = callbacks.IsolatedBonusVideoRunner;
        _rotateAfterLobbyLoginAsync = callbacks.RotateAfterLobbyLoginAsync;
        _lobbyWorldSelectionRequested = callbacks.LobbyWorldSelectionRequested;
        _manualLoginConfirmationRequested = callbacks.ManualLoginConfirmationRequested;
        _lobbyWorldServerResolved = callbacks.LobbyWorldServerResolved;
        _account = account;
        _interactive = interactive;
        _browserVisible = browserVisible;
        // Shared across per-operation clients for the same browser session; falls back to a fresh
        // private cache when none is supplied (e.g. in tests).
        _session = sessionCache ?? new TravianSessionCache();
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? Directory.GetCurrentDirectory()
            : projectRoot;
        _capitalCachePath = AccountStoragePaths.CapitalStatePath(_projectRoot, _account.Name);
        _heroAttributeSnapshotStore = new HeroAttributeSnapshotStore(_projectRoot);
        _heroInventorySnapshotStore = new HeroInventorySnapshotStore(_projectRoot);
        _heroOintmentAvailabilityStore = new HeroOintmentAvailabilityStore(_projectRoot);
        _watchtowerSnapshotStore = new WatchtowerSnapshotStore(_projectRoot, callbacks.StatusCallback);
        _statusCallback = callbacks.StatusCallback;
        _activeVillageVerified = callbacks.ActiveVillageVerified;
        _constructionQueueObserved = callbacks.ConstructionQueueObserved;
        _browserTrace = browserTrace ?? new BrowserTraceLogger(config.DetailedBrowserLoggingEnabled, callbacks.StatusCallback);
        _browserTrace.AttachPage(page, "travian-client");
    }

    public string AccountName => _account.Name;
    public string ServerUrl => (_resolvedServerUrl ?? _config.BaseUrl).TrimEnd('/');
    public string? KnownAccountTribe => IsKnownTribe(_accountTribe) ? _accountTribe : IsKnownTribe(_cachedAccountTribe) ? _cachedAccountTribe : null;
    public bool? KnownGoldClubEnabled => _cachedGoldClubEnabled;

    internal BrowserTraceLogger.BrowserTraceFlow BeginBrowserTraceFlow(
        string? runId,
        string task,
        string? village,
        string action)
        => _browserTrace.BeginFlow(runId, task, _account.Name, village, action);
    
    private async Task CaptureFailureArtifactsAsync(string label, CancellationToken cancellationToken)
    {
        if (_page.IsClosed)
        {
            return;
        }

        var safeLabel = TravianUrls.SafePathSegment(label);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var diagnosticsRoot = Path.Combine(
            _projectRoot,
            "temp_build_out",
            "diagnostics",
            DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(diagnosticsRoot);

        var screenshotPath = Path.Combine(diagnosticsRoot, $"{stamp}-{safeLabel}.png");
        var htmlPath = Path.Combine(diagnosticsRoot, $"{stamp}-{safeLabel}.html");

        try
        {
            await _page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true,
            });
            var html = await _page.ContentAsync();
            await File.WriteAllTextAsync(htmlPath, html, cancellationToken);
            Notify($"Captured diagnostics: screenshot='{screenshotPath}', html='{htmlPath}'.");
        }
        catch (Exception ex)
        {
            Notify($"Could not capture diagnostics for '{label}': {ex.Message}");
        }
    }

    private void Notify(string message)
    {
        message = NormalizeStartedMessage(message);
        _statusCallback?.Invoke(message);
    }

    private void LogFunctionStarted([CallerMemberName] string? memberName = null)
    {
        // Intentionally a no-op. The per-function "[X] started" entry markers were pure noise — one
        // contentless line per call, emitted every loop/refresh tick. Decisions, results and errors are
        // logged explicitly where they happen. Kept as a no-op so existing call sites need no change.
        _ = memberName;
    }

    private static string NormalizeStartedMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.StartsWith('['))
        {
            return message;
        }

        var startedIndex = message.IndexOf(" started", StringComparison.Ordinal);
        if (startedIndex <= 0)
        {
            return message;
        }

        var tokenEnd = message.IndexOf(' ');
        if (tokenEnd <= 0)
        {
            tokenEnd = startedIndex;
        }

        var memberName = message[..tokenEnd].Trim();
        if (string.IsNullOrWhiteSpace(memberName))
        {
            return message;
        }

        return $"[{memberName}]{message[tokenEnd..]}";
    }

    private sealed class UpgradeActionabilityJs
    {
        [JsonPropertyName("outcome")]
        public string? Outcome { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("detectedMaxLevel")]
        public int? DetectedMaxLevel { get; init; }

        [JsonPropertyName("queueWaitSeconds")]
        public int? QueueWaitSeconds { get; init; }

        [JsonPropertyName("detectedTargetLevel")]
        public int? DetectedTargetLevel { get; init; }

        [JsonPropertyName("candidateIndex")]
        public int? CandidateIndex { get; init; }

        [JsonPropertyName("summary")]
        public List<UpgradeCandidateSummaryJs>? Summary { get; init; }
    }

    private sealed class UpgradeCandidateSummaryJs
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("classes")]
        public string? Classes { get; init; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; init; }

        [JsonPropertyName("inUpgradeContainer")]
        public bool InUpgradeContainer { get; init; }
    }

    private sealed class BuildingJs
    {
        [JsonPropertyName("slotId")]
        public int? SlotId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("gid")]
        public int? Gid { get; init; }

        [JsonPropertyName("href")]
        public string? Href { get; init; }
    }

    private sealed class BuildQueueJs
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("timeLeft")]
        public string? TimeLeft { get; init; }

        [JsonPropertyName("slotId")]
        public int? SlotId { get; init; }

        [JsonPropertyName("gid")]
        public int? Gid { get; init; }

        [JsonPropertyName("href")]
        public string? Href { get; init; }
    }

    private sealed class ActiveConstructionJs
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("timeLeftSeconds")]
        public int? TimeLeftSeconds { get; init; }

        [JsonPropertyName("finishAtText")]
        public string? FinishAtText { get; init; }

        [JsonPropertyName("slotId")]
        public int? SlotId { get; init; }

        [JsonPropertyName("gid")]
        public int? Gid { get; init; }

        [JsonPropertyName("href")]
        public string? Href { get; init; }
    }

    private sealed class FarmListRowJs
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("activeFarmCount")]
        public int? ActiveFarmCount { get; init; }

        [JsonPropertyName("totalFarmCount")]
        public int? TotalFarmCount { get; init; }

        [JsonPropertyName("capacity")]
        public int? Capacity { get; init; }

        [JsonPropertyName("farmCoordinates")]
        public string[]? FarmCoordinates { get; init; }

        [JsonPropertyName("timerText")]
        public string? TimerText { get; init; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; init; }

        [JsonPropertyName("lid")]
        public string? Lid { get; init; }

        [JsonPropertyName("villageName")]
        public string? VillageName { get; init; }

        [JsonPropertyName("villageIndex")]
        public int? VillageIndex { get; init; }
    }

    private sealed class FarmDispatchLimitStateJs
    {
        [JsonPropertyName("hasLimit")]
        public bool HasLimit { get; init; }

        [JsonPropertyName("minTimerSeconds")]
        public int? MinTimerSeconds { get; init; }
    }

    private sealed class SendableFarmListJs
    {
        [JsonPropertyName("lid")]
        public string? Lid { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class FarmListLossRowJs
    {
        [JsonPropertyName("rowIndex")]
        public int RowIndex { get; init; }

        [JsonPropertyName("slotId")]
        public string? SlotId { get; init; }

        [JsonPropertyName("listId")]
        public string? ListId { get; init; }

        [JsonPropertyName("listName")]
        public string? ListName { get; init; }

        [JsonPropertyName("targetName")]
        public string? TargetName { get; init; }

        [JsonPropertyName("rowClass")]
        public string? RowClass { get; init; }

        [JsonPropertyName("raidClass")]
        public string? RaidClass { get; init; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; init; }
    }

    private sealed class FarmListTargetStateJs
    {
        [JsonPropertyName("count")]
        public int? Count { get; init; }

        [JsonPropertyName("hasCoordinate")]
        public bool HasCoordinate { get; init; }
    }

    private sealed class ActiveVillageCoordJs
    {
        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }
    }

    private sealed class PlayerProfileVillageRowJs
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("isCapital")]
        public bool IsCapital { get; init; }

        [JsonPropertyName("isCity")]
        public bool IsCity { get; init; }

        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }

        [JsonPropertyName("population")]
        public int? Population { get; init; }

        [JsonPropertyName("cropFields")]
        public int? CropFields { get; init; }

        /// <summary>
        /// Travian tribe id from the row's tribe icon (class "tribe8_medium"), or null when the
        /// profile does not render one. Only special servers that allow a different tribe per
        /// village show this column; normal servers leave it absent.
        /// </summary>
        [JsonPropertyName("tribeId")]
        public int? TribeId { get; init; }
    }

    private sealed class SidebarVillageJs
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("isCapital")]
        public bool? IsCapital { get; init; }

        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }

        // Official only: population of the active village, read straight from the sidebar
        // (div.population > span). Null for non-active rows and when the cell is absent.
        [JsonPropertyName("population")]
        public int? Population { get; init; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; init; }
    }
}
