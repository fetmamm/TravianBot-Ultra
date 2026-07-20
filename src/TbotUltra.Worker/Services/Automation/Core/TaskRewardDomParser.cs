using System.Net;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

internal static class TaskRewardDomParser
{
    public static bool HasClaimableTasks(string? html, bool isTasksPage)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (isTasksPage)
        {
            return Regex.Matches(html, @"<button\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)
                .Select(match => match.Groups["attrs"].Value)
                .Any(attrs => HasCssClass(attrs, "collect")
                    && !HasCssClass(attrs, "collected")
                    && !HasCssClass(attrs, "disabled")
                    && !Regex.IsMatch(attrs, @"(?:^|\s)disabled(?:\s|=|$)", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(attrs, @"\baria-disabled\s*=\s*[""']true[""']", RegexOptions.IgnoreCase));
        }

        return Regex.Matches(html, @"<(?<tag>[a-z0-9]+)\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)
            .Select(match => match.Groups["attrs"].Value)
            .Any(attrs => HasCssClass(attrs, "newQuestSpeechBubble")
                || (HasAttributeValue(attrs, "id", "questmasterButton") && HasCssClass(attrs, "claimable")));
    }

    private static bool HasCssClass(string attrs, string className)
    {
        var match = Regex.Match(attrs, @"\bclass\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        return WebUtility.HtmlDecode(match.Groups["value"].Value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => string.Equals(value, className, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAttributeValue(string attrs, string attributeName, string expectedValue)
    {
        var match = Regex.Match(
            attrs,
            $@"\b{Regex.Escape(attributeName)}\s*=\s*[""'](?<value>[^""']*)[""']",
            RegexOptions.IgnoreCase);
        return match.Success
            && string.Equals(WebUtility.HtmlDecode(match.Groups["value"].Value), expectedValue, StringComparison.OrdinalIgnoreCase);
    }
}
