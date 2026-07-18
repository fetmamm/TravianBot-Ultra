using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan SlowUiWorkThreshold = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SlowUiWorkLogInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTimeOffset> _lastSlowUiWorkLogUtc = new(StringComparer.Ordinal);

    private void MeasureUiWork(string operation, Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            stopwatch.Stop();
            ReportSlowUiWork(operation, stopwatch.Elapsed);
        }
    }

    private void ReportSlowUiWork(string operation, TimeSpan elapsed)
    {
        if (elapsed < SlowUiWorkThreshold)
        {
            return;
        }

        Debug.WriteLine($"[ui-performance] {operation} took {elapsed.TotalMilliseconds:F1} ms");
        var nowUtc = DateTimeOffset.UtcNow;
        if (_lastSlowUiWorkLogUtc.TryGetValue(operation, out var lastLogUtc)
            && nowUtc - lastLogUtc < SlowUiWorkLogInterval)
        {
            return;
        }

        _lastSlowUiWorkLogUtc[operation] = nowUtc;
        AppendLog($"[ui-performance] {operation} took {elapsed.TotalMilliseconds:F1} ms");
    }
}
