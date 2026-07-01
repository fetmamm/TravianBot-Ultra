using System.Text.Json.Nodes;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private const string ActivityStateWaiting = "waiting";
    private const string ActivityStateTask = "task";
    private const string ActivityStateSleeping = "sleeping";
    private const int SessionActivityHistoryDaysToKeep = 180;

    private sealed record SessionActivityHistoryEntry(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string State,
        string? Label);

    private sealed record SessionActivityDaySummary(
        DateOnly Date,
        TimeSpan Task,
        TimeSpan Waiting,
        TimeSpan Sleeping);

    private string? _sessionActivityAccountName;
    private string? _sessionActivityState;
    private string? _sessionActivityLabel;
    private DateTimeOffset _sessionActivityStartedUtc;
    private DateTimeOffset _sessionActivityLastPersistUtc;

    private void UpdateSessionActivityState(bool forcePersist = false)
    {
        if (_botConfigStore is null || _accountStore is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_shutdownInProgress || _shutdownCompleted)
        {
            CloseCurrentSessionActivityInterval(now);
            return;
        }

        var accountName = _accountStore.ActiveAccountName();
        if (!string.Equals(_sessionActivityAccountName, accountName, StringComparison.OrdinalIgnoreCase))
        {
            CloseCurrentSessionActivityInterval(now);
            _sessionActivityAccountName = accountName;
        }

        var (state, label) = ResolveCurrentSessionActivityState();
        if (state is null)
        {
            CloseCurrentSessionActivityInterval(now);
            return;
        }

        if (!string.Equals(_sessionActivityState, state, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_sessionActivityLabel, label, StringComparison.Ordinal))
        {
            CloseCurrentSessionActivityInterval(now);
            _sessionActivityAccountName = accountName;
            _sessionActivityState = state;
            _sessionActivityLabel = label;
            _sessionActivityStartedUtc = now;
            _sessionActivityLastPersistUtc = DateTimeOffset.MinValue;
        }

        if (forcePersist || now - _sessionActivityLastPersistUtc >= TimeSpan.FromMinutes(1))
        {
            PersistSessionActivityInterval(accountName, _sessionActivityStartedUtc, now, state, label);
            _sessionActivityLastPersistUtc = now;
        }
    }

    private void CloseCurrentSessionActivityInterval(DateTimeOffset nowUtc)
    {
        if (_sessionActivityState is not string state || _sessionActivityStartedUtc == default)
        {
            _sessionActivityState = null;
            _sessionActivityLabel = null;
            _sessionActivityStartedUtc = default;
            return;
        }

        var accountName = string.IsNullOrWhiteSpace(_sessionActivityAccountName)
            ? _accountStore.ActiveAccountName()
            : _sessionActivityAccountName;
        PersistSessionActivityInterval(accountName, _sessionActivityStartedUtc, nowUtc, state, _sessionActivityLabel);
        _sessionActivityState = null;
        _sessionActivityLabel = null;
        _sessionActivityStartedUtc = default;
        _sessionActivityLastPersistUtc = DateTimeOffset.MinValue;
    }

    private (string? State, string? Label) ResolveCurrentSessionActivityState()
    {
        if (IsSessionSleeping)
        {
            return (ActivityStateSleeping, SleepReasonLabel(_sessionPacer.SleepReason));
        }

        if (!string.IsNullOrWhiteSpace(_activeFunctionDisplayName))
        {
            return (ActivityStateTask, _activeFunctionDisplayName);
        }

        if (_uiBusy)
        {
            return (ActivityStateTask, ResolveBusyActivityLabel());
        }

        try
        {
            var running = _botService.GetQueueItemsForDisplay()
                .FirstOrDefault(item => item.Status == QueueStatus.Running);
            if (running is not null)
            {
                return (ActivityStateTask, string.IsNullOrWhiteSpace(running.DisplayName)
                    ? HumanizeTaskNameForStats(running.TaskName)
                    : running.DisplayName);
            }
        }
        catch
        {
            // Best effort only. The UI still has _activeFunctionDisplayName during normal task execution.
        }

        return _isLoggedIn || _browserSessionLikelyOpen
            ? (ActivityStateWaiting, "Waiting for task")
            : (null, null);
    }

    private string ResolveBusyActivityLabel()
    {
        if (!string.IsNullOrWhiteSpace(_pendingManualOperationId)
            && _operationNamesById.TryGetValue(_pendingManualOperationId, out var operationName)
            && !string.IsNullOrWhiteSpace(operationName))
        {
            return operationName;
        }

        return "Manual action";
    }

    private void PersistSessionActivityInterval(
        string accountName,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string state,
        string? label)
    {
        if (string.IsNullOrWhiteSpace(accountName) || endUtc <= startUtc)
        {
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-SessionActivityHistoryDaysToKeep);
            var config = _botConfigStore.LoadForAccount(accountName);
            var entries = ReadSessionActivityHistory(config)
                .Where(entry => entry.EndUtc >= cutoff && entry.StartUtc != startUtc)
                .Append(new SessionActivityHistoryEntry(startUtc, endUtc, state, label))
                .OrderBy(entry => entry.StartUtc)
                .ToList();

            var array = new JsonArray();
            foreach (var entry in entries)
            {
                array.Add(new JsonObject
                {
                    ["start_utc"] = entry.StartUtc.UtcDateTime.ToString("O"),
                    ["end_utc"] = entry.EndUtc.UtcDateTime.ToString("O"),
                    ["state"] = entry.State,
                    ["label"] = entry.Label,
                });
            }

            config[BotOptionPayloadKeys.SessionActivityHistory] = array;
            _botConfigStore.SaveForAccount(accountName, config);
        }
        catch (Exception ex)
        {
            AppendLog($"[activity] could not save timeline: {ex.Message}");
        }
    }

    private IReadOnlyList<SessionActivityHistoryEntry> ReadSessionActivityHistoryWithCurrent(DateTimeOffset nowUtc)
    {
        var entries = new List<SessionActivityHistoryEntry>();
        try
        {
            entries.AddRange(ReadSessionActivityHistory(_botConfigStore.Load()));
        }
        catch (Exception ex)
        {
            AppendLog($"[activity] could not load timeline: {ex.Message}");
        }

        if (_sessionActivityState is string state && _sessionActivityStartedUtc != default && nowUtc > _sessionActivityStartedUtc)
        {
            entries.RemoveAll(entry => entry.StartUtc == _sessionActivityStartedUtc);
            entries.Add(new SessionActivityHistoryEntry(
                _sessionActivityStartedUtc,
                nowUtc,
                state,
                _sessionActivityLabel));
        }

        return entries
            .Where(entry => entry.EndUtc > entry.StartUtc)
            .OrderBy(entry => entry.StartUtc)
            .ToList();
    }

    private static IReadOnlyList<SessionActivityHistoryEntry> ReadSessionActivityHistory(JsonObject config)
    {
        if (config[BotOptionPayloadKeys.SessionActivityHistory] is not JsonArray array)
        {
            return [];
        }

        var entries = new List<SessionActivityHistoryEntry>();
        foreach (var node in array.OfType<JsonObject>())
        {
            if (!DateTimeOffset.TryParse(node["start_utc"]?.GetValue<string>(), out var startUtc)
                || !DateTimeOffset.TryParse(node["end_utc"]?.GetValue<string>(), out var endUtc)
                || endUtc <= startUtc)
            {
                continue;
            }

            var state = node["state"]?.GetValue<string>() ?? string.Empty;
            if (state is not (ActivityStateWaiting or ActivityStateTask or ActivityStateSleeping))
            {
                continue;
            }

            entries.Add(new SessionActivityHistoryEntry(
                startUtc.ToUniversalTime(),
                endUtc.ToUniversalTime(),
                state,
                node["label"]?.GetValue<string>()));
        }

        return entries;
    }

    private SessionActivityDaySummary GetSessionActivityDaySummary(DateOnly date)
    {
        var summaries = BuildSessionActivityDaySummaries(date, date, DateTimeOffset.UtcNow);
        return summaries.TryGetValue(date, out var summary)
            ? summary
            : new SessionActivityDaySummary(date, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
    }

    private Dictionary<DateOnly, SessionActivityDaySummary> BuildSessionActivityDaySummaries(
        DateOnly firstDate,
        DateOnly lastDate,
        DateTimeOffset nowUtc)
    {
        var totals = new Dictionary<DateOnly, (TimeSpan Task, TimeSpan Waiting, TimeSpan Sleeping)>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            totals[date] = (TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        }

        foreach (var entry in ReadSessionActivityHistoryWithCurrent(nowUtc))
        {
            AddActivityDurationByDay(totals, entry);
        }

        return totals.ToDictionary(
            pair => pair.Key,
            pair => new SessionActivityDaySummary(pair.Key, pair.Value.Task, pair.Value.Waiting, pair.Value.Sleeping));
    }

    private static void AddActivityDurationByDay(
        Dictionary<DateOnly, (TimeSpan Task, TimeSpan Waiting, TimeSpan Sleeping)> totals,
        SessionActivityHistoryEntry entry)
    {
        var cursor = entry.StartUtc.ToLocalTime();
        var end = entry.EndUtc.ToLocalTime();
        while (cursor < end)
        {
            var date = DateOnly.FromDateTime(cursor.DateTime);
            var nextMidnight = new DateTimeOffset(cursor.Date.AddDays(1), cursor.Offset);
            var segmentEnd = end < nextMidnight ? end : nextMidnight;
            var duration = segmentEnd - cursor;

            if (totals.TryGetValue(date, out var total))
            {
                total = entry.State switch
                {
                    ActivityStateTask => (total.Task + duration, total.Waiting, total.Sleeping),
                    ActivityStateWaiting => (total.Task, total.Waiting + duration, total.Sleeping),
                    ActivityStateSleeping => (total.Task, total.Waiting, total.Sleeping + duration),
                    _ => total,
                };
                totals[date] = total;
            }

            cursor = segmentEnd;
        }
    }

    private IReadOnlyList<DailyPacingTimelineRow> BuildDailyPacingTimelineRows(DateOnly firstDate, DateOnly lastDate)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = nowUtc.ToLocalTime();
        var entries = ReadSessionActivityHistoryWithCurrent(nowUtc);
        var rows = new List<DailyPacingTimelineRow>();

        for (var date = lastDate; date >= firstDate; date = date.AddDays(-1))
        {
            var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), nowLocal.Offset);
            var dayEnd = date == DateOnly.FromDateTime(nowLocal.DateTime)
                ? nowLocal
                : dayStart.AddDays(1);
            var daySegments = entries
                .Select(entry => ToLocalSegment(entry, dayStart, dayEnd))
                .Where(segment => segment is not null)
                .Select(segment => segment!.Value)
                .OrderBy(segment => segment.Start)
                .ToList();

            var cursor = dayStart;
            foreach (var segment in daySegments)
            {
                if (segment.Start > cursor)
                {
                    rows.Add(BuildTimelineRow(date, cursor, segment.Start, "Offline", "Program closed or logged out"));
                }

                rows.Add(BuildTimelineRow(
                    date,
                    segment.Start,
                    segment.End,
                    HumanizeActivityState(segment.State),
                    segment.Label ?? string.Empty));
                cursor = segment.End > cursor ? segment.End : cursor;
            }

            if (dayEnd > cursor)
            {
                rows.Add(BuildTimelineRow(date, cursor, dayEnd, "Offline", "Program closed or logged out"));
            }
        }

        return rows.Where(row => row.Duration != "0min").ToList();
    }

    private static (DateTimeOffset Start, DateTimeOffset End, string State, string? Label)? ToLocalSegment(
        SessionActivityHistoryEntry entry,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd)
    {
        var start = entry.StartUtc.ToLocalTime();
        var end = entry.EndUtc.ToLocalTime();
        if (end <= dayStart || start >= dayEnd)
        {
            return null;
        }

        return (start < dayStart ? dayStart : start, end > dayEnd ? dayEnd : end, entry.State, entry.Label);
    }

    private static DailyPacingTimelineRow BuildTimelineRow(
        DateOnly date,
        DateTimeOffset start,
        DateTimeOffset end,
        string state,
        string details)
    {
        return new DailyPacingTimelineRow(
            date.ToString("yyyy-MM-dd"),
            $"{start:HH:mm}-{end:HH:mm}",
            state,
            FormatDailyDetailsDuration(end - start),
            details);
    }

    private static string HumanizeActivityState(string state) => state switch
    {
        ActivityStateTask => "Task",
        ActivityStateWaiting => "Waiting",
        ActivityStateSleeping => "Sleeping",
        _ => "Offline",
    };

    private static string SleepReasonLabel(Services.Orchestration.SessionSleepReason reason) => reason switch
    {
        Services.Orchestration.SessionSleepReason.Schedule => "Scheduled off-hours",
        Services.Orchestration.SessionSleepReason.DailyLimit => "Daily runtime limit",
        Services.Orchestration.SessionSleepReason.Manual => "Manual sleep",
        Services.Orchestration.SessionSleepReason.SessionPacing => "Session pacing",
        _ => "Sleeping",
    };
}
