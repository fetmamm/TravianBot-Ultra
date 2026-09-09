using System.Net;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

internal static class AccountDeletionDomParser
{
    public static bool IsPending(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        foreach (Match item in Regex.Matches(
                     html,
                     @"<li\b(?<attrs>[^>]*)>(?<content>.*?)</li>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (!HasCssClass(item.Groups["attrs"].Value, "infoType_22"))
            {
                continue;
            }

            foreach (Match timer in Regex.Matches(
                         item.Groups["content"].Value,
                         @"<span\b(?<attrs>[^>]*)>",
                         RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var attributes = timer.Groups["attrs"].Value;
                if (HasCssClass(attributes, "timer")
                    && Regex.IsMatch(attributes, @"\bcounting\s*=\s*[""']down[""']", RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasCssClass(string attributes, string className)
    {
        var match = Regex.Match(attributes, @"\bclass\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        return WebUtility.HtmlDecode(match.Groups["value"].Value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => string.Equals(value, className, StringComparison.OrdinalIgnoreCase));
    }
}
