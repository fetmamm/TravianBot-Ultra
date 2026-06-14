using System.Globalization;
using System.Text;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Core.Tasks;

/// <summary>
/// One troop selected for Smithy improvement. <see cref="Key"/> identifies the troop on the Smithy
/// page: preferred form is the Travian unit id ("u21"), falling back to the troop slot ("t1") when the
/// unit id is unknown (e.g. tribe not resolved). <see cref="TargetLevel"/> is the level the bot keeps
/// improving the troop up to (clamped to 1..<see cref="SmithyUpgradePayload.MaxLevel"/>). <see cref="Name"/>
/// is display-only and is never part of the serialized payload (the worker reads the name off the page).
/// </summary>
public sealed record SmithyTroopTarget(string Key, int TargetLevel, string? Name = null);

/// <summary>
/// Payload for the <c>upgrade_troops_at_smithy</c> task. Carries the user's per-troop selection as a
/// compact string under <see cref="BotOptionPayloadKeys.SmithyUpgradeTargets"/>, e.g. "u21=20;u24=10".
/// An empty/missing value means "no troops selected" and the task is a no-op (backward compatible: old
/// queued tasks have no key and therefore do nothing instead of blindly upgrading every troop).
/// </summary>
public sealed record SmithyUpgradePayload(IReadOnlyList<SmithyTroopTarget> Targets)
{
    public const int MaxLevel = 20;

    private const char EntrySeparator = ';';
    private const char KeyValueSeparator = '=';

    /// <summary>Parses the compact "u21=20;u24=10" form into targets. Invalid/empty entries are skipped.</summary>
    public static IReadOnlyList<SmithyTroopTarget> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var result = new List<SmithyTroopTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(KeyValueSeparator, 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0].Trim();
            if (key.Length == 0 || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            {
                continue;
            }

            if (!seen.Add(key))
            {
                continue; // first wins on duplicate keys
            }

            result.Add(new SmithyTroopTarget(key, Math.Clamp(level, 1, MaxLevel)));
        }

        return result;
    }

    /// <summary>Serializes targets to the compact "u21=20;u24=10" form (names are intentionally dropped).</summary>
    public string Serialize()
    {
        var builder = new StringBuilder();
        foreach (var target in Targets)
        {
            if (target is null || string.IsNullOrWhiteSpace(target.Key))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(EntrySeparator);
            }

            builder.Append(target.Key.Trim())
                .Append(KeyValueSeparator)
                .Append(Math.Clamp(target.TargetLevel, 1, MaxLevel).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public Dictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var serialized = Serialize();
        if (serialized.Length > 0)
        {
            result[BotOptionPayloadKeys.SmithyUpgradeTargets] = serialized;
        }

        return result;
    }
}
