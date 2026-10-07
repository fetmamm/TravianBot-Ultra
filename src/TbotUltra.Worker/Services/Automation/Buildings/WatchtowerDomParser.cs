using System.Net;
using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal sealed record WatchtowerPageSnapshot(
    bool ExtensionAvailable,
    WatchtowerStatus? Status,
    bool UpgradeActionAvailable,
    string? BlockingMessage);

internal static partial class WatchtowerDomParser
{
    internal static WatchtowerPageSnapshot Parse(string? html, DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new(false, null, false, "Watchtower page was empty.");
        }

        var extensionMatch = ExtensionStartRegex().Match(html);
        if (!extensionMatch.Success)
        {
            return new(false, null, false, "The wall page did not expose the Watchtowers extension.");
        }

        var extensionEnd = UnderConstructionHeadingRegex().Match(html, extensionMatch.Index + extensionMatch.Length);
        var extension = extensionEnd.Success
            ? html[extensionMatch.Index..extensionEnd.Index]
            : html[extensionMatch.Index..];
        var levelMatch = WatchtowerLevelRegex().Match(extension);
        var level = levelMatch.Success && int.TryParse(levelMatch.Groups["level"].Value, out var parsedLevel)
            ? parsedLevel
            : 0;

        var active = QueueRowRegex().Matches(html)
            .Select(match =>
            {
                var queueLevel = int.Parse(match.Groups["level"].Value);
                var seconds = int.TryParse(match.Groups["seconds"].Value, out var parsedSeconds)
                    ? parsedSeconds
                    : TravianParsing.ParseDurationToSeconds(Clean(match.Groups["time"].Value));
                return new WatchtowerConstruction(
                    queueLevel,
                    seconds,
                    Clean(match.Groups["finish"].Value),
                    seconds is > 0 ? TimerSnapshot.FromRemaining(seconds.Value, observedAtUtc) : null);
            })
            .OrderBy(item => item.Level)
            .ToList();

        var resourceValues = ResourceValueRegex().Matches(extension)
            .Select(match => TravianParsing.ParseNumericTextToInt(Clean(match.Groups["value"].Value)))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Take(4)
            .ToList();
        var durationMatch = DurationRegex().Match(extension);
        var duration = durationMatch.Success
            ? TravianParsing.ParseDurationToSeconds(Clean(durationMatch.Groups["value"].Value))
            : null;
        var blockerMatch = ErrorRegex().Match(extension);
        var blocker = blockerMatch.Success ? Clean(blockerMatch.Groups["value"].Value) : null;
        var actionAvailable = UpgradeButtonRegex().IsMatch(extension);

        return new WatchtowerPageSnapshot(
            true,
            new WatchtowerStatus(
                level,
                active,
                observedAtUtc,
                resourceValues.ElementAtOrDefault(0),
                resourceValues.ElementAtOrDefault(1),
                resourceValues.ElementAtOrDefault(2),
                resourceValues.ElementAtOrDefault(3),
                duration),
            actionAvailable,
            blocker);
    }

    private static string Clean(string? html) => WebUtility.HtmlDecode(
        Regex.Replace(html ?? string.Empty, "<[^>]+>", " "))
        .Replace("\u202D", string.Empty, StringComparison.Ordinal)
        .Replace("\u202C", string.Empty, StringComparison.Ordinal)
        .Replace("\u2212", "-", StringComparison.Ordinal)
        .Trim();

    [GeneratedRegex(@"<div\b[^>]*class=[\""'][^\""']*\bextension\b[^\""']*[\""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionStartRegex();

    [GeneratedRegex(@"<h4\b[^>]*>\s*Under construction\s*</h4>", RegexOptions.IgnoreCase)]
    private static partial Regex UnderConstructionHeadingRegex();

    [GeneratedRegex(@"<strong>\s*Watchtowers\s*</strong>.*?<span\b[^>]*class=[\""'][^\""']*\blevel\b[^\""']*[\""'][^>]*>\s*Level\s*(?<level>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WatchtowerLevelRegex();

    [GeneratedRegex(@"<tr[^>]*>\s*<td\b[^>]*class=[\""'][^\""']*\bdesc\b[^\""']*[\""'][^>]*>.*?Watchtowers.*?Level\s*(?<level>\d+).*?<td\b[^>]*class=[\""'][^\""']*\bdur\b[^\""']*[\""'][^>]*>.*?<span\b[^>]*data-value=[\""'](?<seconds>\d+)[\""'][^>]*>(?<time>.*?)</span>.*?<td\b[^>]*class=[\""'][^\""']*\bfin\b[^\""']*[\""'][^>]*>(?<finish>.*?)</td>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex QueueRowRegex();

    [GeneratedRegex(@"<div\b[^>]*class=[\""'][^\""']*\binlineIcon\s+resource\b[^\""']*[\""'][^>]*>.*?<span\b[^>]*class=[\""'][^\""']*\bvalue\b[^\""']*[\""'][^>]*>(?<value>.*?)</span>.*?</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResourceValueRegex();

    [GeneratedRegex(@"<div\b[^>]*class=[\""'][^\""']*\bduration\b[^\""']*[\""'][^>]*>.*?<span\b[^>]*class=[\""'][^\""']*\bvalue\b[^\""']*[\""'][^>]*>(?<value>.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"<button\b(?=[^>]*class=[\""'][^\""']*\bgreen\b[^\""']*[\""'])(?=[^>]*(?:value=[\""']Upgrade[\""']|action=build))[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UpgradeButtonRegex();

    [GeneratedRegex(@"<div\b[^>]*class=[\""'][^\""']*\berrorMessage\b[^\""']*[\""'][^>]*>(?<value>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ErrorRegex();
}
