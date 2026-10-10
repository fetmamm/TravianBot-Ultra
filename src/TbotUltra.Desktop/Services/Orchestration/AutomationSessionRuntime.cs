namespace TbotUltra.Desktop.Services.Orchestration;

internal enum KeepAlivePlan
{
    Disabled,
    Scheduled,
    NotDue,
    RecentFailure,
    SkipSleeping,
    SkipRefreshRunning,
    SkipNoWorkDueSoon,
    SkipImminentWork,
    Refresh,
}

internal readonly record struct GoldClubCheckPlan(bool Enabled, bool ShouldRefresh);

internal sealed class AutomationSessionRuntime(
    TimeProvider? timeProvider = null,
    Func<int, int, int>? nextRandom = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<int, int, int> _nextRandom = nextRandom ?? Random.Shared.Next;
    private DateTimeOffset _lastInboxCheckUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBrowserActivityUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextKeepAliveAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastKeepAliveFailureUtc = DateTimeOffset.MinValue;
    private bool? _keepAliveEnabledLastApplied;
    private string _goldClubAccount = string.Empty;
    private bool? _goldClubEnabled;
    private DateTimeOffset _lastGoldClubCheckUtc = DateTimeOffset.MinValue;
    private int _constructionStatusNeedsSync = 1;
    private long _constructionStatusSyncGeneration;
    private readonly object _constructionStatusSyncGate = new();
    private string? _lastWarningSignature;
    private DateTimeOffset _lastIdleHeartbeatUtc = DateTimeOffset.MinValue;
    private bool _idleDorf2ParkingEvaluated;
    private readonly object _diagnosticGate = new();
    private readonly Dictionary<string, DateTimeOffset> _verboseLogAtByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _constructionSummaryByVillage = new(StringComparer.OrdinalIgnoreCase);

    internal DateTimeOffset NextKeepAliveAtUtc => _nextKeepAliveAtUtc;

    internal bool ShouldCheckInbox(bool enabled, TimeSpan interval)
    {
        var now = _timeProvider.GetUtcNow();
        if (!enabled || now - _lastInboxCheckUtc < interval)
        {
            return false;
        }

        _lastInboxCheckUtc = now;
        return true;
    }

    internal void RecordBrowserActivity(bool keepAliveEnabled, int minMinutes, int maxMinutes)
    {
        var now = _timeProvider.GetUtcNow();
        _lastBrowserActivityUtc = now;
        _keepAliveEnabledLastApplied = keepAliveEnabled;
        _nextKeepAliveAtUtc = keepAliveEnabled
            ? now.Add(ResolveKeepAliveDelay(minMinutes, maxMinutes))
            : DateTimeOffset.MinValue;
    }

    internal KeepAlivePlan PlanKeepAlive(
        bool enabled,
        int minMinutes,
        int maxMinutes,
        bool sessionSleeping,
        bool refreshRunning,
        bool workDueSoon,
        DateTimeOffset? nextPendingAt)
    {
        var now = _timeProvider.GetUtcNow();
        if (!enabled)
        {
            _nextKeepAliveAtUtc = DateTimeOffset.MinValue;
            _keepAliveEnabledLastApplied = false;
            return KeepAlivePlan.Disabled;
        }

        if (_keepAliveEnabledLastApplied == false)
        {
            _keepAliveEnabledLastApplied = true;
            _nextKeepAliveAtUtc = now.Add(ResolveKeepAliveDelay(minMinutes, maxMinutes));
            return KeepAlivePlan.Scheduled;
        }

        _keepAliveEnabledLastApplied = true;
        if (_nextKeepAliveAtUtc == DateTimeOffset.MinValue)
        {
            var anchor = _lastBrowserActivityUtc == DateTimeOffset.MinValue
                ? now
                : _lastBrowserActivityUtc;
            _nextKeepAliveAtUtc = anchor.Add(ResolveKeepAliveDelay(minMinutes, maxMinutes));
        }

        if (now < _nextKeepAliveAtUtc)
        {
            return KeepAlivePlan.NotDue;
        }

        if (now - _lastKeepAliveFailureUtc < TimeSpan.FromMinutes(2))
        {
            return KeepAlivePlan.RecentFailure;
        }

        if (sessionSleeping)
        {
            RecordBrowserActivity(true, minMinutes, maxMinutes);
            return KeepAlivePlan.SkipSleeping;
        }

        if (refreshRunning)
        {
            RecordBrowserActivity(true, minMinutes, maxMinutes);
            return KeepAlivePlan.SkipRefreshRunning;
        }

        if (!workDueSoon)
        {
            _nextKeepAliveAtUtc = now.Add(ResolveKeepAliveDelay(minMinutes, maxMinutes));
            return KeepAlivePlan.SkipNoWorkDueSoon;
        }

        if (nextPendingAt is DateTimeOffset pendingAt
            && pendingAt >= now
            && pendingAt - now <= TimeSpan.FromSeconds(30))
        {
            _nextKeepAliveAtUtc = pendingAt.AddSeconds(5);
            return KeepAlivePlan.SkipImminentWork;
        }

        RecordBrowserActivity(true, minMinutes, maxMinutes);
        return KeepAlivePlan.Refresh;
    }

    internal void MarkKeepAliveFailure() => _lastKeepAliveFailureUtc = _timeProvider.GetUtcNow();

    internal GoldClubCheckPlan PlanGoldClubCheck(
        string? accountName,
        bool? storedEnabled,
        TimeSpan inactiveRecheckInterval)
    {
        if (!string.Equals(_goldClubAccount, accountName, StringComparison.OrdinalIgnoreCase))
        {
            _goldClubAccount = accountName ?? string.Empty;
            _goldClubEnabled = storedEnabled;
            _lastGoldClubCheckUtc = DateTimeOffset.MinValue;
        }

        if (_goldClubEnabled == true)
        {
            return new GoldClubCheckPlan(true, false);
        }

        var now = _timeProvider.GetUtcNow();
        if (now - _lastGoldClubCheckUtc < inactiveRecheckInterval)
        {
            return new GoldClubCheckPlan(false, false);
        }

        _lastGoldClubCheckUtc = now;
        return new GoldClubCheckPlan(false, true);
    }

    internal bool ApplyGoldClubStatus(bool enabled)
    {
        _goldClubEnabled = enabled;
        return enabled;
    }

    internal void RequestConstructionStatusSync()
    {
        lock (_constructionStatusSyncGate)
        {
            _constructionStatusSyncGeneration++;
            _constructionStatusNeedsSync = 1;
        }
    }

    internal bool ConstructionStatusNeedsSync =>
        Volatile.Read(ref _constructionStatusNeedsSync) == 1;

    internal long ConstructionStatusSyncGeneration
    {
        get { lock (_constructionStatusSyncGate) return _constructionStatusSyncGeneration; }
    }

    internal bool TryMarkConstructionStatusSynchronized(long observedGeneration)
    {
        lock (_constructionStatusSyncGate)
        {
            if (_constructionStatusNeedsSync == 0
                || _constructionStatusSyncGeneration != observedGeneration)
            {
                return false;
            }

            Volatile.Write(ref _constructionStatusNeedsSync, 0);
            return true;
        }
    }

    internal void MarkConstructionStatusSynchronized()
    {
        lock (_constructionStatusSyncGate)
            Volatile.Write(ref _constructionStatusNeedsSync, 0);
    }

    internal bool ShouldPublishWarnings(string signature)
    {
        if (string.Equals(signature, _lastWarningSignature, StringComparison.Ordinal))
        {
            return false;
        }

        _lastWarningSignature = signature;
        return true;
    }

    internal bool ShouldPublishIdleHeartbeat(TimeSpan interval)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _lastIdleHeartbeatUtc < interval)
        {
            return false;
        }

        _lastIdleHeartbeatUtc = now;
        return true;
    }

    internal void MarkActivePass()
    {
        _lastIdleHeartbeatUtc = _timeProvider.GetUtcNow();
        _idleDorf2ParkingEvaluated = false;
    }

    internal bool TryBeginIdleDorf2ParkingEvaluation(bool enabled, TimeSpan remainingIdle)
    {
        if (!enabled
            || remainingIdle < TimeSpan.FromSeconds(60)
            || _idleDorf2ParkingEvaluated)
        {
            return false;
        }

        _idleDorf2ParkingEvaluated = true;
        return true;
    }

    internal void BeginAutomationRun() => _idleDorf2ParkingEvaluated = false;

    internal bool ShouldPublishVerbose(string key, TimeSpan interval)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_diagnosticGate)
        {
            if (_verboseLogAtByKey.TryGetValue(key, out var lastLogAt)
                && now - lastLogAt < interval)
            {
                return false;
            }

            _verboseLogAtByKey[key] = now;
            return true;
        }
    }

    internal bool TrySetConstructionSummary(string villageKey, string state)
    {
        lock (_diagnosticGate)
        {
            if (_constructionSummaryByVillage.TryGetValue(villageKey, out var existing)
                && string.Equals(existing, state, StringComparison.Ordinal))
            {
                return false;
            }

            _constructionSummaryByVillage[villageKey] = state;
            return true;
        }
    }

    internal void ClearConstructionSummary(string villageKey)
    {
        lock (_diagnosticGate)
        {
            _constructionSummaryByVillage.Remove(villageKey);
        }
    }

    internal void Reset()
    {
        _lastInboxCheckUtc = DateTimeOffset.MinValue;
        _lastBrowserActivityUtc = DateTimeOffset.MinValue;
        _nextKeepAliveAtUtc = DateTimeOffset.MinValue;
        _lastKeepAliveFailureUtc = DateTimeOffset.MinValue;
        _keepAliveEnabledLastApplied = null;
        _goldClubAccount = string.Empty;
        _goldClubEnabled = null;
        _lastGoldClubCheckUtc = DateTimeOffset.MinValue;
        _lastWarningSignature = null;
        _lastIdleHeartbeatUtc = DateTimeOffset.MinValue;
        _idleDorf2ParkingEvaluated = false;
        lock (_diagnosticGate)
        {
            _verboseLogAtByKey.Clear();
            _constructionSummaryByVillage.Clear();
        }
        RequestConstructionStatusSync();
    }

    private TimeSpan ResolveKeepAliveDelay(int minMinutes, int maxMinutes)
    {
        var min = Math.Clamp(minMinutes, 1, 1440);
        var max = Math.Clamp(maxMinutes, min, 1440);
        return TimeSpan.FromMinutes(_nextRandom(min, max + 1));
    }
}
