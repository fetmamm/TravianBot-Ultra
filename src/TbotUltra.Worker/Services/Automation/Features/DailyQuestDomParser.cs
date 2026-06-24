using System.Net;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

internal static class DailyQuestDomParser
{
    public static bool HasClaimableDailyQuests(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        foreach (Match anchor in Regex.Matches(
                     html,
                     @"<a\b(?<attrs>[^>]*)>(?<content>.*?)</a>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = anchor.Groups["attrs"].Value;
            if (!HasCssClass(attrs, "dailyQuests"))
            {
                continue;
            }

            var content = anchor.Groups["content"].Value;
            if (Regex.IsMatch(
                    content,
                    @"<div\b(?<attrs>[^>]*)>\s*!\s*</div>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)
                && HasCssClass(Regex.Match(
                    content,
                    @"<div\b(?<attrs>[^>]*)>\s*!\s*</div>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["attrs"].Value, "indicator"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCssClass(string attrs, string className)
    {
        var match = Regex.Match(attrs, @"\bclass\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        var classes = WebUtility.HtmlDecode(match.Groups["value"].Value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return classes.Any(value => string.Equals(value, className, StringComparison.OrdinalIgnoreCase));
    }
}
