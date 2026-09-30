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

    internal sealed record UpgradePreClickSafetyResult(bool IsSafe, string Reason);

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

    internal static UpgradePreClickSafetyResult VerifyUpgradePreClickSafety(
        string html,
        int expectedSlotId,
        int expectedGid,
        string expectedName,
        int expectedCurrentLevel,
        int expectedOfferLevel,
        int targetLevel)
    {
        var source = html ?? string.Empty;
        if (expectedOfferLevel > targetLevel)
        {
            return new(false, $"offered level {expectedOfferLevel} exceeds target {targetLevel}");
        }

        var titleMatch = Regex.Match(
            source,
            @"<h1\b[^>]*class=[""'][^""']*\btitleInHeader\b[^""']*[""'][^>]*>(?<value>.*?)</h1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var title = ParseBuildPageTitle(titleMatch.Success ? titleMatch.Groups["value"].Value : null);
        if (string.IsNullOrWhiteSpace(title.Name) || !BuildingNames.Same(title.Name, expectedName))
        {
            return new(false, $"page title '{title.Name ?? "unknown"}' does not match '{expectedName}'");
        }
        if (title.Level != expectedCurrentLevel)
        {
            return new(false, $"page title level {title.Level?.ToString() ?? "unknown"} does not match live level {expectedCurrentLevel}");
        }

        var buildRoot = Regex.Match(
            source,
            @"<div\b(?=[^>]*\bid\s*=\s*[""']build[""'])[^>]*\bclass\s*=\s*[""'](?<classes>[^""']*)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var classes = buildRoot.Success ? buildRoot.Groups["classes"].Value : string.Empty;
        var gidMatch = Regex.Match(classes, @"(?:^|\s)gid(?<value>\d{1,2})(?:\s|$)", RegexOptions.IgnoreCase);
        var levelMatch = Regex.Match(classes, @"(?:^|\s)level(?<value>\d{1,3})(?:\s|$)", RegexOptions.IgnoreCase);
        var pageGid = gidMatch.Success && int.TryParse(gidMatch.Groups["value"].Value, out var parsedGid) ? parsedGid : (int?)null;
        var pageLevel = levelMatch.Success && int.TryParse(levelMatch.Groups["value"].Value, out var parsedLevel) ? parsedLevel : (int?)null;
        if (pageGid != expectedGid)
        {
            return new(false, $"page gid {pageGid?.ToString() ?? "unknown"} does not match expected gid {expectedGid}");
        }
        if (pageLevel != expectedCurrentLevel)
        {
            return new(false, $"page level {pageLevel?.ToString() ?? "unknown"} does not match live level {expectedCurrentLevel}");
        }

        var candidates = ExtractButtonCandidates(source)
            .Where(candidate => candidate.InOfficialPrimarySection)
            .Where(candidate => !candidate.Disabled && !candidate.IsSpeedup && !candidate.IsGold)
            .Select(candidate => new
            {
                Candidate = candidate,
                LevelMatch = Regex.Match(candidate.Text, @"^Upgrade\s+to\s+level\s+(?<value>\d{1,3})$", RegexOptions.IgnoreCase),
            })
            .Where(candidate => candidate.LevelMatch.Success)
            .ToList();
        if (candidates.Count != 1)
        {
            return new(false, $"expected exactly one primary upgrade button but found {candidates.Count}");
        }

        var offeredLevel = int.Parse(candidates[0].LevelMatch.Groups["value"].Value, CultureInfo.InvariantCulture);
        if (offeredLevel != expectedOfferLevel)
        {
            return new(false, $"button offers level {offeredLevel}, expected {expectedOfferLevel}");
        }
        if (offeredLevel > targetLevel)
        {
            return new(false, $"button offers level {offeredLevel}, above target {targetLevel}");
        }

        var onclick = candidates[0].Candidate.OnClick;
        var actionSlot = ReadQueryNumber(onclick, "id");
        var actionGid = ReadQueryNumber(onclick, "gid");
        if (actionSlot != expectedSlotId)
        {
            return new(false, $"button targets slot {actionSlot?.ToString() ?? "unknown"}, expected {expectedSlotId}");
        }
        if (actionGid != expectedGid)
        {
            return new(false, $"button targets gid {actionGid?.ToString() ?? "unknown"}, expected {expectedGid}");
        }

        return new(true, $"slot={expectedSlotId} gid={expectedGid} name='{expectedName}' current={expectedCurrentLevel} offer={offeredLevel} target={targetLevel}");
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

    internal static int? ReadUnderConstructionTargetLevelFromHtmlForTests(string html)
    {
        var row = Regex.Match(
            html ?? string.Empty,
            @"<(?:tr|div)\b[^>]*class=[""'][^""']*\bunderConstruction\b[^""']*[""'][^>]*>(?<content>[\s\S]*?)</(?:tr|div)>",
            RegexOptions.IgnoreCase);
        if (!row.Success)
        {
            return null;
        }

        var text = CleanHtmlText(Regex.Replace(row.Groups["content"].Value, "<[^>]+>", " "));
        var level = Regex.Match(text, @"\b(?:to\s+)?level\s*(?<level>\d{1,3})\b", RegexOptions.IgnoreCase);
        return level.Success && int.TryParse(level.Groups["level"].Value, out var parsedLevel)
            ? parsedLevel
            : null;
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

    internal static bool HasCropShortageBlockFromHtmlForTests(string html)
    {
        var source = html ?? string.Empty;
        return Regex.IsMatch(
            source,
            @"class=[""'][^""']*upgradeBlocked[^""']*[""'][\s\S]*?class=[""'][^""']*errorMessage[^""']*[""'][\s\S]*?(?:lack\s+of\s+food\s*:\s*extend\s+cropland\s+first!?|increase\s+crop\s+production\.?)",
            RegexOptions.IgnoreCase);
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

    private static int? ReadQueryNumber(string value, string key)
    {
        var match = Regex.Match(
            value ?? string.Empty,
            $@"(?:[?&]){Regex.Escape(key)}=(?<value>\d+)(?:&|['""\s]|$)",
            RegexOptions.IgnoreCase);
        return match.Success
               && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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
