using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

/// <summary>Persists account/server spending totals and rolls them over at server midnight.</summary>
public sealed class DailySpendingStore
{
    private static readonly ConcurrentDictionary<string, object> PathGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public DailySpendingStore(string path)
    {
        _path = path;
    }

    public bool TryReserveGold(DateOnly serverDate, int dailyLimit, int cost, out int spentAfterReservation)
    {
        if (cost <= 0)
        {
            spentAfterReservation = 0;
            return true;
        }

        lock (PathGates.GetOrAdd(_path, static _ => new object()))
        {
            var state = ReadState(serverDate);
            var normalizedLimit = Math.Max(0, dailyLimit);
            if (state.GoldSpent > normalizedLimit - cost)
            {
                spentAfterReservation = state.GoldSpent;
                return false;
            }

            state = state with { GoldSpent = state.GoldSpent + cost };
            WriteState(state);
            spentAfterReservation = state.GoldSpent;
            return true;
        }
    }

    public DailySpendingState Read(DateOnly serverDate)
    {
        lock (PathGates.GetOrAdd(_path, static _ => new object()))
        {
            return ReadState(serverDate);
        }
    }

    public void ResetGold()
    {
        ResetExistingState(static state => state with { GoldSpent = 0 });
    }

    public void ResetSilver()
    {
        ResetExistingState(static state => state with { SilverSpent = 0 });
    }

    private void ResetExistingState(Func<DailySpendingState, DailySpendingState> reset)
    {
        lock (PathGates.GetOrAdd(_path, static _ => new object()))
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<DailySpendingState>(File.ReadAllText(_path));
            if (stored is not null)
            {
                WriteState(reset(stored));
            }
        }
    }

    private DailySpendingState ReadState(DateOnly serverDate)
    {
        if (!File.Exists(_path))
        {
            return DailySpendingState.Empty(serverDate);
        }

        var stored = JsonSerializer.Deserialize<DailySpendingState>(File.ReadAllText(_path));
        if (stored is null
            || !DateOnly.TryParseExact(stored.ServerDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var storedDate)
            || storedDate != serverDate)
        {
            return DailySpendingState.Empty(serverDate);
        }

        return stored with
        {
            GoldSpent = Math.Max(0, stored.GoldSpent),
            SilverSpent = Math.Max(0, stored.SilverSpent),
        };
    }

    private void WriteState(DailySpendingState state)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Daily spending state path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed record DailySpendingState(string ServerDate, int GoldSpent, int SilverSpent)
{
    public static DailySpendingState Empty(DateOnly serverDate) =>
        new(serverDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0, 0);
}
