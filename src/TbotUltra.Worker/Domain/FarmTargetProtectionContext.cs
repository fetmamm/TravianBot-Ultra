using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace TbotUltra.Worker.Domain;

public sealed record FarmTargetIdentity(
    [property: JsonPropertyName("isResolved")] bool IsResolved,
    [property: JsonPropertyName("playerName")] string? PlayerName,
    [property: JsonPropertyName("alliance")] string? Alliance);

public enum FarmTargetProtectionDecision
{
    Allowed = 0,
    ExcludedPlayer = 1,
    ExcludedAlliance = 2,
    IdentityUnavailable = 3,
}

/// <summary>
/// Holds one Add farms run's protection rules and short-lived target identity cache.
/// </summary>
public sealed class FarmTargetProtectionContext
{
    private readonly HashSet<string> _excludedPlayerKeys;
    private readonly HashSet<string> _excludedAllianceKeys;
    private readonly Dictionary<string, FarmTargetProtectionDecision> _decisionsByCoordinate = new(StringComparer.Ordinal);

    public FarmTargetProtectionContext(
        string ownPlayerName,
        string? ownAlliance,
        bool excludeOwnAlliance,
        IEnumerable<string> excludedPlayers,
        IEnumerable<string> excludedAlliances)
    {
        OwnPlayerName = Clean(ownPlayerName);
        OwnAlliance = CleanOptional(ownAlliance);
        ExcludeOwnAlliance = excludeOwnAlliance && OwnAlliance is not null;
        _excludedPlayerKeys = ToKeySet(excludedPlayers);
        _excludedAllianceKeys = ToKeySet(excludedAlliances);

        var ownPlayerKey = NormalizeKey(OwnPlayerName);
        if (ownPlayerKey.Length > 0)
        {
            _excludedPlayerKeys.Add(ownPlayerKey);
        }

        if (ExcludeOwnAlliance)
        {
            _excludedAllianceKeys.Add(NormalizeKey(OwnAlliance));
        }
    }

    public string OwnPlayerName { get; }
    public string? OwnAlliance { get; }
    public bool ExcludeOwnAlliance { get; }

    public bool TryGetCachedDecision(int x, int y, out FarmTargetProtectionDecision decision)
        => _decisionsByCoordinate.TryGetValue(CoordinateKey(x, y), out decision);

    public FarmTargetProtectionDecision EvaluateAndCache(int x, int y, bool isOasis, FarmTargetIdentity identity)
    {
        var decision = Evaluate(isOasis, identity);
        if (decision != FarmTargetProtectionDecision.IdentityUnavailable)
        {
            _decisionsByCoordinate[CoordinateKey(x, y)] = decision;
        }

        return decision;
    }

    public FarmTargetProtectionDecision Evaluate(bool isOasis, FarmTargetIdentity identity)
    {
        if (!identity.IsResolved)
        {
            return FarmTargetProtectionDecision.IdentityUnavailable;
        }

        var playerKey = NormalizeKey(identity.PlayerName);
        if (playerKey.Length == 0)
        {
            return isOasis
                ? FarmTargetProtectionDecision.Allowed
                : FarmTargetProtectionDecision.IdentityUnavailable;
        }

        if (_excludedPlayerKeys.Contains(playerKey))
        {
            return FarmTargetProtectionDecision.ExcludedPlayer;
        }

        var allianceKey = NormalizeKey(identity.Alliance);
        return allianceKey.Length > 0 && _excludedAllianceKeys.Contains(allianceKey)
            ? FarmTargetProtectionDecision.ExcludedAlliance
            : FarmTargetProtectionDecision.Allowed;
    }

    public static string NormalizeKey(string? value)
        => Regex.Replace(Clean(value), @"\s+", " ").ToLowerInvariant();

    private static HashSet<string> ToKeySet(IEnumerable<string> values)
        => values
            .Select(NormalizeKey)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static string Clean(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string? CleanOptional(string? value)
    {
        var clean = Clean(value);
        return clean.Length == 0 || clean is "-" or "–" or "—" ? null : clean;
    }

    private static string CoordinateKey(int x, int y) => $"{x}|{y}";
}
