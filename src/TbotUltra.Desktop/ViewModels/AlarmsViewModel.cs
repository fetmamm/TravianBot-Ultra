using System;
using System.Collections.ObjectModel;
using System.Linq;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the alarm log: owns the alarm-entry collection, the
/// 30-minute dedup/coalescing rule and the unacknowledged counter. List
/// refresh, theming and clipboard stay in MainWindow code-behind.
/// </summary>
public sealed class AlarmsViewModel : BaseViewModel
{
    /// <summary>
    /// Identical alarms within this window update the existing row's count
    /// instead of adding another alarm line (ENGINEERING_NOTES alarm rule).
    /// </summary>
    private static readonly TimeSpan CoalescingWindow = TimeSpan.FromMinutes(30);

    private int _unacknowledgedCount;

    /// <summary>
    /// Alarm entries. Created once and mutated in place so the default
    /// collection view bound to the alarm list stays stable.
    /// </summary>
    public ObservableCollection<AlarmEntryRow> Entries { get; } = [];

    public int UnacknowledgedCount
    {
        get => _unacknowledgedCount;
        private set => SetProperty(ref _unacknowledgedCount, value);
    }

    /// <summary>
    /// Records an alarm line. A row with the same signature seen within the
    /// coalescing window is updated in place (occurrence count, last-seen, and
    /// re-raised acknowledged state); otherwise a new row is inserted first.
    /// Returns true when a NEW row was added — only new rows go to the session log.
    /// </summary>
    public bool RecordAlarm(string line, string signature, bool isAcknowledgedAlarm, DateTimeOffset nowUtc)
    {
        var existingAlarm = Entries.FirstOrDefault(entry =>
            string.Equals(entry.Signature, signature, StringComparison.Ordinal)
            && nowUtc - entry.LastSeenUtc <= CoalescingWindow);
        if (existingAlarm is not null)
        {
            existingAlarm.OccurrenceCount++;
            existingAlarm.LastSeenUtc = nowUtc;
            existingAlarm.Text =
                $"{line} (x{existingAlarm.OccurrenceCount}, first {existingAlarm.FirstSeenUtc.ToLocalTime():HH:mm:ss})";
            if (existingAlarm.IsAcknowledged && !isAcknowledgedAlarm)
            {
                existingAlarm.IsAcknowledged = false;
                UnacknowledgedCount += 1;
            }

            return false;
        }

        Entries.Insert(0, new AlarmEntryRow
        {
            Text = line,
            Signature = signature,
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
            IsAcknowledged = isAcknowledgedAlarm,
        });
        if (!isAcknowledgedAlarm)
        {
            UnacknowledgedCount += 1;
        }

        return true;
    }

    public void AcknowledgeAll()
    {
        foreach (var entry in Entries)
        {
            entry.IsAcknowledged = true;
        }

        UnacknowledgedCount = 0;
    }

    /// <summary>
    /// Acknowledges every unacknowledged entry matching the predicate and
    /// recomputes the counter. Returns true when anything changed.
    /// </summary>
    public bool AcknowledgeWhere(Func<AlarmEntryRow, bool> predicate)
    {
        var changed = false;
        foreach (var entry in Entries)
        {
            if (entry.IsAcknowledged || !predicate(entry))
            {
                continue;
            }

            entry.IsAcknowledged = true;
            changed = true;
        }

        if (changed)
        {
            UnacknowledgedCount = Entries.Count(entry => !entry.IsAcknowledged);
        }

        return changed;
    }

    public void Clear()
    {
        Entries.Clear();
        UnacknowledgedCount = 0;
    }
}
