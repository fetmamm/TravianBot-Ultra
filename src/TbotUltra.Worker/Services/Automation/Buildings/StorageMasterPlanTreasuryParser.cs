using System.Net;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

internal static class StorageMasterPlanTreasuryParser
{
    internal static bool? ParseActiveVillageEffect(string html)
    {
        if (string.IsNullOrWhiteSpace(html)
            || !Regex.IsMatch(html, @"<h1\b[^>]*>\s*Treasury\b", RegexOptions.IgnoreCase))
        {
            return null;
        }

        var heading = Regex.Match(
            html,
            @"<h4\b[^>]*>\s*Regions secured by your alliance\s*</h4>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!heading.Success)
        {
            return null;
        }

        var afterHeading = html[(heading.Index + heading.Length)..];
        var table = Regex.Match(
            afterHeading,
            @"^\s*<table\b[^>]*\bshow_artefacts\b[^>]*>.*?</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!table.Success)
        {
            return null;
        }

        foreach (Match row in Regex.Matches(table.Value, @"<tr\b[^>]*>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (!Regex.IsMatch(row.Value, @"\bartefactIcon\b[^>]*\btype9\b", RegexOptions.IgnoreCase)
                || !Regex.IsMatch(row.Value, @"storage\s+masterplan", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var activity = Regex.Match(
                row.Value,
                @"<td\b[^>]*\bclass\s*=\s*['""][^'""]*\bal\b[^'""]*['""][^>]*>(.*?)</td>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (activity.Success
                && WebUtility.HtmlDecode(Regex.Replace(activity.Groups[1].Value, "<[^>]+>", " "))
                    .Contains("Ancient power activated for this village", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
