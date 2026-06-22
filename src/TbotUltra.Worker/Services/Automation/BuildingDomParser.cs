using System.Globalization;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless C# HTML/DOM parsing for the building/construction pages, extracted from
/// <see cref="TravianClient"/>. These are pure functions over raw HTML strings; the live bot
/// reads the same state JS-side, so this class exists as the unit-testable C# mirror.
/// <para>
/// Low-level token helpers (<see cref="ExtractBuildingSlotHtml"/>, <see cref="ReadAttribute"/>,
/// <see cref="CleanHtmlText"/>) are <c>internal</c> because the overview-scan shim
/// <c>TravianClient.ParseBuildingOverviewHtmlForTests</c> still uses them.
/// </para>
/// </summary>
internal static class BuildingDomParser
{
    internal sealed record BuildPageTitleInfo(string? Name, int? Level);

    internal sealed record HtmlButtonCandidate(
        string Text,
        string Classes,
        string OnClick,
        string? WrapperGid,
        bool Disabled,
        bool IsGold,
        bool IsSpeedup,
        bool InOfficialPrimarySection);

    internal static HtmlButtonCandidate? SelectUpgradeButtonCandidateFromHtmlForTests(string html, int nextLevel)
    {
        var candidates = ExtractButtonCandidates(html);
        var expectedText = $"Upgrade to level {nextLevel}";
        return candidates
            .Where(candidate => candidate.Text.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => !candidate.Disabled && !candidate.IsSpeedup && !candidate.IsGold)
            .OrderByDescending(candidate => candidate.InOfficialPrimarySection)
            .ThenByDescending(candidate => candidate.Classes.Contains("green", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    internal static IReadOnlyList<HtmlButtonCandidate> ExtractButtonCandidatesFromHtmlForTests(string html)
    {
        return ExtractButtonCandidates(html);
    }

    internal static BuildPageTitleInfo ParseBuildPageTitle(string? title)
    {
        var cleaned = CleanHtmlText(title ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return new BuildPageTitleInfo(null, null);
        }

        var levelMatch = Regex.Match(cleaned, @"\b(?:level|lvl)\s*(?<level>\d{1,3})\b", RegexOptions.IgnoreCase);
        var level = levelMatch.Success && int.TryParse(levelMatch.Groups["level"].Value, out var parsedLevel)
            ? parsedLevel
            : (int?)null;
        var name = levelMatch.Success
            ? cleaned[..levelMatch.Index].Trim()
            : cleaned;

        return new BuildPageTitleInfo(string.IsNullOrWhiteSpace(name) ? null : name, level);
    }

    /// <summary>
    /// C# mirror of the empty-construction-slot heuristic in <c>TravianClient.DetectBuildPageStateAsync</c>.
    /// A slot is empty when the page lists construction choices (<c>id="contract_building*"</c>) but has no
    /// real "Upgrade to level N" affordance. The construct-choice page reuses <c>.upgradeButtonsContainer</c>
    /// per building, so that container's presence must NOT count as an upgrade signal.
    /// </summary>
    internal static bool IsEmptyConstructionSlotHtmlForTests(string html)
    {
        var source = html ?? string.Empty;
        var hasConstructChoices = Regex.IsMatch(source, @"id=[""']contract_building", RegexOptions.IgnoreCase);
        var hasUpgrade = Regex.IsMatch(source, @"upgrade\s+to\s+level", RegexOptions.IgnoreCase);
        return hasConstructChoices && !hasUpgrade;
    }

    /// <summary>
    /// C# mirror of <c>TravianClient.ReadConstructRequirementErrorAsync</c>. Returns the missing-requirement text
    /// listed in a building's <c>#contract_building{gid}</c> wrapper (Official's span.buildingCondition.error),
    /// or null when the building is buildable (has a 'Construct building' button) or has no requirement error.
    /// </summary>
    internal static string? ReadConstructRequirementErrorFromHtmlForTests(string html, int gid)
    {
        var source = html ?? string.Empty;
        var startIdx = source.IndexOf($"id=\"contract_building{gid}\"", StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0)
        {
            return null;
        }

        var nextIdx = source.IndexOf("id=\"contract_building", startIdx + 21, StringComparison.OrdinalIgnoreCase);
        var wrapper = nextIdx < 0 ? source[startIdx..] : source[startIdx..nextIdx];
        if (Regex.IsMatch(wrapper, @"value=[""']Construct building[""']", RegexOptions.IgnoreCase))
        {
            return null;
        }

        if (!Regex.IsMatch(wrapper, @"buildingCondition\s+error", RegexOptions.IgnoreCase))
        {
            return null;
        }

        var containerIdx = wrapper.IndexOf("upgradeButtonsContainer", StringComparison.OrdinalIgnoreCase);
        var conditionsHtml = containerIdx < 0 ? wrapper : wrapper[containerIdx..];
        var text = CleanHtmlText(Regex.Replace(conditionsHtml, "<[^>]+>", " "));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    internal static HtmlButtonCandidate? SelectConstructButtonCandidateFromHtmlForTests(string html, int gid)
    {
        var gidText = gid.ToString(CultureInfo.InvariantCulture);
        return ExtractButtonCandidates(html)
            .Where(candidate => candidate.Text.Contains("Construct building", StringComparison.OrdinalIgnoreCase))
            .Where(candidate => !candidate.Disabled && !candidate.IsSpeedup && !candidate.IsGold)
            .Where(candidate => string.Equals(candidate.WrapperGid, gidText, StringComparison.Ordinal)
                || candidate.OnClick.Contains($"gid={gidText}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => string.Equals(candidate.WrapperGid, gidText, StringComparison.Ordinal))
            .ThenByDescending(candidate => candidate.InOfficialPrimarySection)
            .ThenByDescending(candidate => candidate.Classes.Contains("green", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    internal static IReadOnlyDictionary<string, long?> ReadConstructionCostFromHtmlForTests(string html)
    {
        var result = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, cssClass) in new[] { ("wood", "r1"), ("clay", "r2"), ("iron", "r3"), ("crop", "r4") })
        {
            var match = Regex.Match(
                html,
                $@"<i\b[^>]*class=[""'][^""']*\b{cssClass}Big\b[^""']*[""'][^>]*>\s*</i>\s*<span\b[^>]*class=[""'][^""']*\bvalue\b[^""']*[""'][^>]*>(?<value>.*?)</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            result[key] = match.Success ? TravianParsing.TryParseResourceValue(CleanHtmlText(match.Groups["value"].Value)) : null;
        }

        return result;
    }

    internal static int? ReadPrimaryBuildDurationSecondsFromHtmlForTests(string html)
    {
        var source = html ?? string.Empty;
        var section1Index = Regex.Match(
            source,
            @"<div\b[^>]*class=[""'][^""']*\bsection1\b[^""']*[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (section1Index.Success)
        {
            var section2Index = Regex.Match(
                source[section1Index.Index..],
                @"<div\b[^>]*class=[""'][^""']*\bsection2\b[^""']*[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            source = section2Index.Success
                ? source.Substring(section1Index.Index, section2Index.Index)
                : source[section1Index.Index..];
        }

        var match = Regex.Match(
            source,
            @"<div\b[^>]*class=[""'][^""']*\bduration\b[^""']*[""'][^>]*>.*?<span\b[^>]*class=[""'][^""']*\bvalue\b[^""']*[""'][^>]*>(?<value>.*?)</span>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? TravianParsing.ParseDurationToSeconds(CleanHtmlText(match.Groups["value"].Value)) : null;
    }

    private static IReadOnlyList<HtmlButtonCandidate> ExtractButtonCandidates(string html)
    {
        var candidates = new List<HtmlButtonCandidate>();
        var sourceHtml = html ?? string.Empty;
        foreach (Match match in Regex.Matches(sourceHtml, @"<button\b(?<attrs>[^>]*)>(?<text>.*?)</button>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = match.Groups["attrs"].Value;
            var text = CleanHtmlText(ReadAttribute(attrs, "value") ?? match.Groups["text"].Value);
            var classes = ReadAttribute(attrs, "class") ?? string.Empty;
            var onclick = System.Net.WebUtility.HtmlDecode(ReadAttribute(attrs, "onclick") ?? string.Empty);
            var before = sourceHtml[..match.Index];
            var afterLastWrapper = before.LastIndexOf("contract_building", StringComparison.OrdinalIgnoreCase);
            string? wrapperGid = null;
            if (afterLastWrapper >= 0)
            {
                var wrapperMatch = Regex.Match(before[afterLastWrapper..], @"contract_building(?<gid>\d{1,2})", RegexOptions.IgnoreCase);
                wrapperGid = wrapperMatch.Success ? wrapperMatch.Groups["gid"].Value : null;
            }

            var lastSection1 = LastSectionIndex(before, "section1");
            var lastSection2 = LastSectionIndex(before, "section2");
            var inPrimary = lastSection1 > lastSection2;
            var lowerCombined = $"{text} {classes} {onclick}".ToLowerInvariant();
            candidates.Add(new HtmlButtonCandidate(
                text,
                classes,
                onclick,
                wrapperGid,
                HasDisabledAttribute(attrs) || classes.Contains("disabled", StringComparison.OrdinalIgnoreCase),
                lowerCombined.Contains("gold") || lowerCombined.Contains("npc") || lowerCombined.Contains("instant")
                    || lowerCombined.Contains("openpaymentwizard") || lowerCombined.Contains("paymentwizard") || lowerCombined.Contains("open shop"),
                lastSection2 > lastSection1 || lowerCombined.Contains("purple") || lowerCombined.Contains("videofeature") || lowerCombined.Contains("faster"),
                inPrimary));
        }

        return candidates;
    }

    private static bool HasDisabledAttribute(string attributes)
    {
        return Regex.IsMatch(
            attributes ?? string.Empty,
            @"(?:^|\s)disabled(?:\s|=|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static int LastSectionIndex(string html, string sectionClass)
    {
        var matches = Regex.Matches(
            html,
            @$"<div\b[^>]*class=[""'][^""']*\b{Regex.Escape(sectionClass)}\b[^""']*[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return matches.Count == 0 ? -1 : matches[^1].Index;
    }

    internal static IReadOnlyList<string> ExtractBuildingSlotHtml(string html)
    {
        return Regex.Matches(
                html ?? string.Empty,
                @"<div\b[^>]*class=[""'][^""']*\bbuildingSlot\b[^""']*[""'][\s\S]*?(?=<div\b[^>]*class=[""'][^""']*\bbuildingSlot\b|<div\s+id=[""']sidebar|$)",
                RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToList();
    }

    internal static string? ReadAttribute(string htmlOrAttributes, string attributeName)
    {
        var match = Regex.Match(
            htmlOrAttributes ?? string.Empty,
            $@"\b{Regex.Escape(attributeName)}\s*=\s*([""'])(?<value>.*?)\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    internal static string CleanHtmlText(string value)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<.*?>", " ", RegexOptions.Singleline));
        return string.Join(" ", decoded.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
    }
}
