using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop.Services;

internal enum ProxyFailoverKind
{
    CurrentProxyHealthy,
    ReplacementProxy,
    DirectConnection,
    Unavailable,
}

internal sealed record ProxyFailoverResult(
    ProxyFailoverKind Kind,
    ProxyLibraryEntry? Proxy,
    string Message);

internal sealed class ProxyFailoverService
{
    internal static readonly TimeSpan FailedProxyCooldown = TimeSpan.FromMinutes(45);

    private readonly ProxyListTester _tester;

    internal ProxyFailoverService(ProxyListTester tester)
    {
        _tester = tester;
    }

    internal async Task<ProxyFailoverResult> FindRecoveryAsync(
        AccountEntry account,
        List<ProxyLibraryEntry> library,
        string targetUrl,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var current = ProxyLibraryStore.FindByServer(library, account.ProxyServer);
        var targetRejected = new List<ProxyLibraryEntry>();
        var currentProbe = await ProbeProxyAsync(account.ProxyServer, targetUrl, cancellationToken);
        if (currentProbe.Success)
        {
            MarkWorking(current, currentProbe.LatencyMs);
            return new ProxyFailoverResult(
                ProxyFailoverKind.CurrentProxyHealthy,
                current,
                "The active proxy passed the recovery test; keeping it.");
        }

        RecordFailedProbe(current, currentProbe, targetRejected);
        log($"[proxy-recovery] active proxy failed the stability and Travian reachability test: {ProxyParser.MaskForLog(account.ProxyServer)}.");

        foreach (var candidate in SelectCandidates(library, account, DateTime.UtcNow))
        {
            cancellationToken.ThrowIfCancellationRequested();
            log($"[proxy-recovery] testing replacement proxy {ProxyParser.MaskForLog(candidate.Server)}.");
            var probe = await ProbeProxyAsync(candidate.Server, targetUrl, cancellationToken);
            if (!probe.Success)
            {
                RecordFailedProbe(candidate, probe, targetRejected);
                continue;
            }

            MarkTargetRejectedFailed(targetRejected);
            MarkWorking(candidate, probe.LatencyMs);
            candidate.AssignedAccount ??= account.Name;
            ProxyLibraryStore.AddUsage(library, candidate.Id, account.Name);
            return new ProxyFailoverResult(
                ProxyFailoverKind.ReplacementProxy,
                candidate,
                $"Replacement proxy passed all checks: {ProxyParser.MaskForLog(candidate.Server)}.");
        }

        if (!account.NeverUseOwnIp
            && await _tester.TestDirectAgainstTargetAsync(targetUrl, cancellationToken).ConfigureAwait(false))
        {
            MarkTargetRejectedFailed(targetRejected);
            return new ProxyFailoverResult(
                ProxyFailoverKind.DirectConnection,
                null,
                "No replacement proxy passed; direct connection is available and allowed.");
        }

        var reason = account.NeverUseOwnIp
            ? "No working replacement proxy was found and Never use own IP is enabled."
            : "No working replacement proxy or direct connection was found.";
        return new ProxyFailoverResult(ProxyFailoverKind.Unavailable, null, reason);
    }

    internal static IReadOnlyList<ProxyLibraryEntry> SelectCandidates(
        IEnumerable<ProxyLibraryEntry> library,
        AccountEntry account,
        DateTime nowUtc)
    {
        var current = ProxyLibraryStore.FindByServer(library, account.ProxyServer);
        return library
            .Where(entry => !ReferenceEquals(entry, current))
            .Where(entry => string.IsNullOrWhiteSpace(entry.AssignedAccount)
                || string.Equals(entry.AssignedAccount, account.Name, StringComparison.OrdinalIgnoreCase))
            .Where(entry => entry.UsedByAccounts.All(usedBy =>
                string.Equals(usedBy, account.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(entry => entry.LastFailureUtc is null
                || nowUtc - entry.LastFailureUtc.Value.ToUniversalTime() >= FailedProxyCooldown)
            .OrderByDescending(entry => string.Equals(entry.AssignedAccount, account.Name, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.IsWorking == true)
            .ThenBy(entry => entry.IsWorking == false)
            .ThenBy(entry => entry.LatencyMs ?? long.MaxValue)
            .ThenBy(entry => entry.CreatedAtUtc)
            .ToList();
    }

    private async Task<ProxyProbeResult> ProbeProxyAsync(
        string server,
        string targetUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _tester.TestServerAgainstTargetAsync(server, targetUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProxyProbeResult(false, 0);
        }
    }

    private static void MarkFailed(ProxyLibraryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.IsWorking = false;
        entry.LastFailureUtc = DateTime.UtcNow;
    }

    private static void RecordFailedProbe(
        ProxyLibraryEntry? entry,
        ProxyProbeResult probe,
        List<ProxyLibraryEntry> targetRejected)
    {
        if (entry is null)
        {
            return;
        }

        if (probe.LatencyMs <= 0)
        {
            MarkFailed(entry);
            return;
        }

        targetRejected.Add(entry);
    }

    private static void MarkTargetRejectedFailed(IEnumerable<ProxyLibraryEntry> entries)
    {
        foreach (var entry in entries)
        {
            MarkFailed(entry);
        }
    }

    private static void MarkWorking(ProxyLibraryEntry? entry, long latencyMs)
    {
        if (entry is null)
        {
            return;
        }

        entry.IsWorking = true;
        entry.LatencyMs = latencyMs > 0 ? latencyMs : entry.LatencyMs;
        entry.LastFailureUtc = null;
    }
}
