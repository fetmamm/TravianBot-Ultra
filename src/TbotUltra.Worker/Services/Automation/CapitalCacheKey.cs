namespace TbotUltra.Worker.Services;

/// <summary>
/// Builds the composite key that identifies a village in the capital cache
/// (account | server | village). Stateless string normalization extracted from
/// <see cref="TravianClient"/> so it can be unit-tested in isolation.
/// </summary>
internal static class CapitalCacheKey
{
    internal static string Build(string accountName, string serverUrl, string villageName)
    {
        return string.Join(
            "|",
            NormalizePart(accountName),
            NormalizePart(serverUrl),
            NormalizePart(villageName));
    }

    internal static string NormalizePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }
}
