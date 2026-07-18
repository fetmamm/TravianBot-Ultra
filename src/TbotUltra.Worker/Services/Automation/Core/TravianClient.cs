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
    private readonly Action<string>? _statusCallback;
    private readonly BrowserTraceLogger _browserTrace;
    // Flips the browser session's consentmanager route block on/off; used only by the bonus-video flow,
    // which needs GDPR/TCF consent while the rest of the session keeps it blocked (no stray sync tabs).
    private readonly Action<bool>? _setConsentDomainsAllowed;
    private readonly Func<IPage, CancellationToken, Task>? _cleanupAfterBonusVideoAsync;
    private readonly Func<Func<IPage, CancellationToken, Task<string>>, CancellationToken, Task<string>>? _runInIsolatedBonusVideoBrowserAsync;
    private readonly Func<CancellationToken, Task<IPage>>? _rotateAfterLobbyLoginAsync;
    private readonly Func<LobbyWorldSelectionRequest, CancellationToken, Task<string?>>? _lobbyWorldSelectionRequested;
    private DateTimeOffset? _serverTimeUtc;
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
    private bool _lastActiveConstructionsFromOverview;
    private static readonly TimeSpan ActiveConstructionsMutationCacheTtl = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan ActiveConstructionsObservationCacheTtl = TimeSpan.FromSeconds(30);
    private ConstructionNavigationDiagnostics? _constructionNavDiagnostics;

    internal void InvalidateActiveConstructionsCache()
    {
        _cachedActiveConstructions = null;
        _cachedActiveConstructionsFromOverview = false;
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
    // one-minute TTL keeps rare village additions reasonably fresh without re-reading the unchanged
    // sidebar on every ~20s dashboard tick. Active-village renames use coordinate reconciliation on
    // every tick and therefore do not depend on this TTL.
    // Village identity changes rarely. Keep the account list long enough that periodic resource/UI
    // reads do not scrape the same sidebar every minute. A failed switch invalidates it immediately;
    // the five-minute observation refresh still detects founded/renamed villages during a session.
    private static readonly TimeSpan VillagesCacheTtl = TimeSpan.FromMinutes(5);
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
    // True once the population baseline has been read from spieler.php this session. Re-armed (set
    // false) on a real village switch so the next active village can seed its own baseline.
    private bool _populationBaselineRead { get => _session.PopulationBaselineRead; set => _session.PopulationBaselineRead = value; }
    // Backed by the shared session cache so the logged-in throttle survives across per-operation clients.
    private DateTimeOffset _lastEnsureLoggedInAt { get => _session.LastEnsureLoggedInAt; set => _session.LastEnsureLoggedInAt = value; }
    private DateTimeOffset _lastUiSyncAt = DateTimeOffset.MinValue;
    private bool _lastEnsureLoggedInSucceeded { get => _session.LastEnsureLoggedInSucceeded; set => _session.LastEnsureLoggedInSucceeded = value; }
    private int _suppressEnsureUiSyncDepth;
    private string? _productionUiSnapshotVillage;
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
        Action<string>? statusCallback = null,
        TravianSessionCache? sessionCache = null,
        Action<bool>? setConsentDomainsAllowed = null,
        Func<IPage, CancellationToken, Task>? cleanupAfterBonusVideoAsync = null,
        Func<Func<IPage, CancellationToken, Task<string>>, CancellationToken, Task<string>>? runInIsolatedBonusVideoBrowserAsync = null,
        Func<CancellationToken, Task<IPage>>? rotateAfterLobbyLoginAsync = null,
        Func<LobbyWorldSelectionRequest, CancellationToken, Task<string?>>? lobbyWorldSelectionRequested = null,
        BrowserTraceLogger? browserTrace = null)
    {
        _page = page;
        _config = config;
        _setConsentDomainsAllowed = setConsentDomainsAllowed;
        _cleanupAfterBonusVideoAsync = cleanupAfterBonusVideoAsync;
        _runInIsolatedBonusVideoBrowserAsync = runInIsolatedBonusVideoBrowserAsync;
        _rotateAfterLobbyLoginAsync = rotateAfterLobbyLoginAsync;
        _lobbyWorldSelectionRequested = lobbyWorldSelectionRequested;
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
        _statusCallback = statusCallback;
        _browserTrace = browserTrace ?? new BrowserTraceLogger(config.DetailedBrowserLoggingEnabled, statusCallback);
        _browserTrace.AttachPage(page, "travian-client");
    }

    public string AccountName => _account.Name;
    public string ServerUrl => _config.BaseUrl.TrimEnd('/');
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
    }

    private sealed class FarmDispatchLimitStateJs
    {
        [JsonPropertyName("hasLimit")]
        public bool HasLimit { get; init; }

        [JsonPropertyName("minTimerSeconds")]
        public int? MinTimerSeconds { get; init; }
    }

    private sealed class FarmListSendAllClickStateJs
    {
        [JsonPropertyName("clicked")]
        public bool Clicked { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("listIds")]
        public string[]? ListIds { get; init; }

        [JsonPropertyName("listCount")]
        public int ListCount { get; init; }
    }

    private sealed class FarmListLossRowJs
    {
        [JsonPropertyName("rowIndex")]
        public int RowIndex { get; init; }

        [JsonPropertyName("slotId")]
        public string? SlotId { get; init; }

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

        [JsonPropertyName("x")]
        public int? X { get; init; }

        [JsonPropertyName("y")]
        public int? Y { get; init; }

        [JsonPropertyName("population")]
        public int? Population { get; init; }

        [JsonPropertyName("cropFields")]
        public int? CropFields { get; init; }
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
