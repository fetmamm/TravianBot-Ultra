using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

internal static class AccountProxyBindingResolver
{
    internal static ProxyLibraryEntry? Resolve(AccountEntry account, IEnumerable<ProxyLibraryEntry> entries)
    {
        var candidates = entries.ToList();
        if (!string.IsNullOrWhiteSpace(account.ProxyId))
        {
            var bound = candidates.FirstOrDefault(entry =>
                string.Equals(entry.Id, account.ProxyId, StringComparison.OrdinalIgnoreCase));
            if (bound is not null)
            {
                return bound;
            }
        }

        var exact = ProxyLibraryStore.FindByServer(candidates, account.ProxyServer);
        if (exact is not null)
        {
            return exact;
        }

        if (!ProxyLibraryStore.TryCanonicalize(account.ProxyServer, out var scheme, out var host, out var port))
        {
            return null;
        }

        var endpointMatches = candidates.Where(entry =>
            string.Equals(ProxyLibraryEntry.NormalizeScheme(entry.Scheme), scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Host.Trim(), host, StringComparison.OrdinalIgnoreCase)
            && entry.Port == port).ToList();
        var assignedMatches = endpointMatches.Where(entry =>
            string.Equals(entry.AssignedAccount, account.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (assignedMatches.Count == 1)
        {
            return assignedMatches[0];
        }

        return endpointMatches.Count == 1 && string.IsNullOrWhiteSpace(endpointMatches[0].AssignedAccount)
            ? endpointMatches[0]
            : null;
    }
}
