using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace TbotUltra.Worker.Infrastructure;

public sealed class BrowserTraceLogger
{
    public const string DomEventConsolePrefix = "__TBOT_BROWSER_TRACE__";
    internal sealed class RunState
    {
        public required string RunId { get; init; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public long Sequence;
        public int Navigations;
        public int Reloads;
        public int Actions;
        public int Reads;
        public int CacheHits;
        public long WaitMilliseconds;
    }

    internal sealed record TraceContext(
        RunState Run,
        string Task,
        string Account,
        string Village,
        TraceContext? Parent);

    private readonly Action<string>? _sink;
    private readonly AsyncLocal<TraceContext?> _current = new();
    private readonly object _pageGate = new();
    private readonly HashSet<IPage> _attachedPages = [];
    private readonly RunState _sessionRun = new() { RunId = $"session-{Guid.NewGuid():N}"[..16] };
    private TraceContext? _activeContext;
    private volatile bool _enabled;
    private string _latestUrl = "-";

    public BrowserTraceLogger(bool enabled, Action<string>? sink)
    {
        _enabled = enabled;
        _sink = sink;
    }

    public bool Enabled => _enabled;
    public string LatestUrl => _latestUrl;

    public void SetEnabled(bool enabled)
        => _enabled = enabled;

    public BrowserTraceFlow BeginFlow(
        string? runId,
        string task,
        string account,
        string? village,
        string action)
    {
        if (!_enabled)
        {
            return BrowserTraceFlow.Disabled;
        }

        var parent = _current.Value;
        var run = parent?.Run ?? new RunState
        {
            RunId = string.IsNullOrWhiteSpace(runId)
                ? Guid.NewGuid().ToString("N")[..12]
                : BrowserTraceSanitizer.SanitizeText(runId),
        };
        var context = new TraceContext(
            run,
            string.IsNullOrWhiteSpace(task) ? "unknown" : task,
            string.IsNullOrWhiteSpace(account) ? "-" : account,
            string.IsNullOrWhiteSpace(village) ? "-" : village,
            parent);
        _current.Value = context;
        Volatile.Write(ref _activeContext, context);
        Write(context, "FLOW_START", action, "started", null, LatestUrl, null);
        return new BrowserTraceFlow(this, context, action, Snapshot(run));
    }

    public BrowserTraceOperation BeginOperation(
        string eventPrefix,
        string action,
        string? detail = null,
        string? url = null)
    {
        if (!_enabled)
        {
            return BrowserTraceOperation.Disabled;
        }

        var context = ResolveContext();
        IncrementCounter(context.Run, eventPrefix, action);
        Write(context, eventPrefix + "_START", action, "started", detail, url ?? LatestUrl, null);
        return new BrowserTraceOperation(this, context, eventPrefix, action, Stopwatch.StartNew());
    }

    public void Event(
        string eventName,
        string action,
        string result = "observed",
        string? detail = null,
        string? url = null,
        long? durationMs = null)
    {
        if (!_enabled)
        {
            return;
        }

        var context = ResolveContext();
        IncrementCounter(context.Run, eventName, action);
        Write(context, eventName, action, result, detail, url ?? LatestUrl, durationMs);
    }

    public void AttachPage(IPage page, string source)
    {
        lock (_pageGate)
        {
            if (!_attachedPages.Add(page))
            {
                return;
            }
        }

        _latestUrl = page.Url;
        Event("PAGE_CONTEXT", "page-attached", detail: $"source={source}", url: page.Url);
        page.FrameNavigated += (_, frame) =>
        {
            if (frame == page.MainFrame)
            {
                _latestUrl = frame.Url;
                Event("NAV_OBSERVED", "frame-navigated", detail: $"source={source}", url: frame.Url);
            }
        };
        page.Popup += (_, popup) =>
        {
            AttachPage(popup, "popup");
            Event("PAGE_CONTEXT", "popup-opened", detail: $"source={source}", url: popup.Url);
        };
        page.Dialog += (_, dialog) =>
            Event("PAGE_CONTEXT", "dialog-opened", detail: $"type={dialog.Type} messageLength={dialog.Message.Length}", url: page.Url);
        page.PageError += (_, error) =>
            Event("ERROR", "page-error", "failed", error, page.Url);
        page.Console += (_, message) =>
        {
            if (message.Text.StartsWith(DomEventConsolePrefix, StringComparison.Ordinal))
            {
                TraceDomEvent(message.Text[DomEventConsolePrefix.Length..], page.Url);
            }
            else if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                Event("ERROR", "console-error", "failed", message.Text, page.Url);
            }
        };
        page.Close += (_, _) =>
        {
            Event("PAGE_CONTEXT", "page-closed", detail: $"source={source}", url: page.Url);
            lock (_pageGate)
            {
                _attachedPages.Remove(page);
            }
        };
    }

    private void TraceDomEvent(string json, string url)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var eventType = root.TryGetProperty("event", out var eventElement)
                ? eventElement.GetString() ?? "dom-event"
                : "dom-event";
            var target = root.TryGetProperty("target", out var targetElement)
                ? targetElement.GetString() ?? "-"
                : "-";
            var field = root.TryGetProperty("field", out var fieldElement)
                ? fieldElement.GetString() ?? "-"
                : "-";
            var trusted = root.TryGetProperty("trusted", out var trustedElement)
                && trustedElement.ValueKind == JsonValueKind.True;
            var valueLength = root.TryGetProperty("valueLength", out var lengthElement)
                && lengthElement.TryGetInt32(out var parsedLength)
                    ? parsedLength
                    : 0;
            var detail = $"target={target} field={field} valueLength={valueLength} trusted={trusted}";
            if (eventType is "change" or "input")
            {
                Event("INPUT", $"dom-{eventType}", "observed", detail, url);
            }
            else
            {
                using var action = BeginOperation("ACTION", $"dom-{eventType}", detail, url);
                action.Complete("observed", detail, url);
            }
        }
        catch (JsonException ex)
        {
            Event("ERROR", "dom-trace-parse", "failed", ex.Message, url);
        }
    }

    internal void CompleteOperation(
        TraceContext context,
        string eventPrefix,
        string action,
        string result,
        string? detail,
        string? url,
        long durationMs)
    {
        if (eventPrefix.StartsWith("WAIT", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Add(ref context.Run.WaitMilliseconds, durationMs);
        }

        Write(context, eventPrefix + "_END", action, result, detail, url ?? LatestUrl, durationMs);
    }

    internal void CompleteFlow(
        TraceContext context,
        string action,
        string result,
        string? detail,
        CounterSnapshot start,
        long durationMs)
    {
        var run = context.Run;
        var summary =
            $"navigations={run.Navigations - start.Navigations} reloads={run.Reloads - start.Reloads} " +
            $"actions={run.Actions - start.Actions} reads={run.Reads - start.Reads} " +
            $"cacheHits={run.CacheHits - start.CacheHits} waitMs={run.WaitMilliseconds - start.WaitMilliseconds}";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            summary += $" {detail}";
        }

        Write(context, "FLOW_END", action, result, summary, LatestUrl, durationMs);
        _current.Value = context.Parent;
        Volatile.Write(ref _activeContext, context.Parent);
    }

    private TraceContext ResolveContext()
        => _current.Value
           ?? Volatile.Read(ref _activeContext)
           ?? new TraceContext(_sessionRun, "browser-session", "-", "-", null);

    private void Write(
        TraceContext context,
        string eventName,
        string action,
        string result,
        string? detail,
        string? url,
        long? durationMs)
    {
        if (!_enabled || _sink is null)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref context.Run.Sequence);
        var elapsedMs = context.Run.Clock.ElapsedMilliseconds;
        _sink(
            $"[browser-trace:verbose] run={context.Run.RunId} seq={sequence} " +
            $"task='{BrowserTraceSanitizer.SanitizeText(context.Task)}' " +
            $"account='{BrowserTraceSanitizer.SanitizeText(context.Account)}' " +
            $"village='{BrowserTraceSanitizer.SanitizeText(context.Village)}' " +
            $"event={eventName} action='{BrowserTraceSanitizer.SanitizeText(action)}' " +
            $"result={BrowserTraceSanitizer.SanitizeText(result)} elapsedMs={elapsedMs} " +
            $"durationMs={durationMs ?? 0} url='{BrowserTraceSanitizer.SanitizeUrl(url)}' " +
            $"detail='{BrowserTraceSanitizer.SanitizeText(detail)}'");
    }

    private static void IncrementCounter(RunState run, string eventName, string action)
    {
        if (eventName.StartsWith("NAV", StringComparison.OrdinalIgnoreCase))
        {
            if (action.Contains("reload", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref run.Reloads);
            }
            else
            {
                Interlocked.Increment(ref run.Navigations);
            }
        }
        else if (eventName.StartsWith("ACTION", StringComparison.OrdinalIgnoreCase)
                 || eventName.StartsWith("INPUT", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref run.Actions);
        }
        else if (eventName.StartsWith("READ", StringComparison.OrdinalIgnoreCase)
                 || eventName.StartsWith("REFRESH", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref run.Reads);
        }
        else if (eventName.StartsWith("CACHE", StringComparison.OrdinalIgnoreCase)
                 && action.Contains("hit", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref run.CacheHits);
        }
    }

    private static CounterSnapshot Snapshot(RunState run)
        => new(run.Navigations, run.Reloads, run.Actions, run.Reads, run.CacheHits, run.WaitMilliseconds);

    internal readonly record struct CounterSnapshot(
        int Navigations,
        int Reloads,
        int Actions,
        int Reads,
        int CacheHits,
        long WaitMilliseconds);

    public sealed class BrowserTraceFlow : IDisposable
    {
        internal static BrowserTraceFlow Disabled { get; } = new();
        private readonly BrowserTraceLogger? _owner;
        private readonly TraceContext? _context;
        private readonly string _action = string.Empty;
        private readonly CounterSnapshot _start;
        private readonly Stopwatch? _timer;
        private int _completed;

        private BrowserTraceFlow()
        {
        }

        internal BrowserTraceFlow(
            BrowserTraceLogger owner,
            TraceContext context,
            string action,
            CounterSnapshot start)
        {
            _owner = owner;
            _context = context;
            _action = action;
            _start = start;
            _timer = Stopwatch.StartNew();
        }

        public void Complete(string result = "success", string? detail = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0 || _owner is null || _context is null)
            {
                return;
            }

            _owner.CompleteFlow(_context, _action, result, detail, _start, _timer?.ElapsedMilliseconds ?? 0);
        }

        public void Dispose()
            => Complete("incomplete", "flow scope disposed without an explicit outcome");
    }

    public sealed class BrowserTraceOperation : IDisposable
    {
        internal static BrowserTraceOperation Disabled { get; } = new();
        private readonly BrowserTraceLogger? _owner;
        private readonly TraceContext? _context;
        private readonly string _eventPrefix = string.Empty;
        private readonly string _action = string.Empty;
        private readonly Stopwatch? _timer;
        private int _completed;

        private BrowserTraceOperation()
        {
        }

        internal BrowserTraceOperation(
            BrowserTraceLogger owner,
            TraceContext context,
            string eventPrefix,
            string action,
            Stopwatch timer)
        {
            _owner = owner;
            _context = context;
            _eventPrefix = eventPrefix;
            _action = action;
            _timer = timer;
        }

        public void Complete(string result = "success", string? detail = null, string? url = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0 || _owner is null || _context is null)
            {
                return;
            }

            _owner.CompleteOperation(
                _context,
                _eventPrefix,
                _action,
                result,
                detail,
                url,
                _timer?.ElapsedMilliseconds ?? 0);
        }

        public void Dispose()
            => Complete("incomplete", "operation scope disposed without an explicit outcome");
    }
}
