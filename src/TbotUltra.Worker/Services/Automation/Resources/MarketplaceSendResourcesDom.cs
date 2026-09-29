using System.Net;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

internal static partial class MarketplaceSendResourcesDom
{
    internal const string RootSelector = "#marketplaceSendResources";
    internal const string SendTabSelector = ".contentNavi.subNavi a.tabItem[href*='gid=17'][href$='t=5']";

    internal static bool IsReady(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)
            || !html.Contains("id=\"marketplaceSendResources\"", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var activeTab = AnchorTagRegex().Matches(html)
            .Select(match => match.Value)
            .FirstOrDefault(tag => HasClass(tag, "tabItem")
                && HasClass(tag, "active")
                && IsSendResourcesHref(ReadAttribute(tag, "href")));
        if (activeTab is null)
        {
            return false;
        }

        return ResourceInputNames.All(name =>
            Regex.IsMatch(
                html,
                $"""<input\b[^>]*\bname\s*=\s*["']{Regex.Escape(name)}["'][^>]*>""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    internal static string DescribeState(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "html=empty";
        }

        var rootPresent = html.Contains("id=\"marketplaceSendResources\"", StringComparison.OrdinalIgnoreCase);
        var sendTabPresent = false;
        var sendTabActive = false;
        foreach (Match match in AnchorTagRegex().Matches(html))
        {
            var tag = match.Value;
            if (!HasClass(tag, "tabItem") || !IsSendResourcesHref(ReadAttribute(tag, "href")))
            {
                continue;
            }

            sendTabPresent = true;
            sendTabActive |= HasClass(tag, "active");
        }

        var missingInputs = ResourceInputNames
            .Where(name => !Regex.IsMatch(
                html,
                $"""<input\b[^>]*\bname\s*=\s*["']{Regex.Escape(name)}["'][^>]*>""",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        return $"root={rootPresent.ToString().ToLowerInvariant()} "
            + $"send_tab={sendTabPresent.ToString().ToLowerInvariant()} "
            + $"send_tab_active={sendTabActive.ToString().ToLowerInvariant()} "
            + $"missing_inputs={(missingInputs.Length == 0 ? "none" : string.Join(',', missingInputs))}";
    }

    internal static string? FindSendResourcesTabHref(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        foreach (Match match in AnchorTagRegex().Matches(html))
        {
            var tag = match.Value;
            var href = ReadAttribute(tag, "href");
            if (HasClass(tag, "tabItem") && IsSendResourcesHref(href))
            {
                return WebUtility.HtmlDecode(href);
            }
        }

        return null;
    }

    private static readonly string[] ResourceInputNames = ["lumber", "clay", "iron", "crop"];

    private static bool IsSendResourcesHref(string? href)
    {
        var decoded = WebUtility.HtmlDecode(href ?? string.Empty);
        return Regex.IsMatch(decoded, @"(?:\?|&)gid=17(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(decoded, @"(?:\?|&)t=5(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasClass(string tag, string className)
    {
        var classes = ReadAttribute(tag, "class") ?? string.Empty;
        return classes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(className, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadAttribute(string tag, string attributeName)
    {
        var match = Regex.Match(
            tag,
            $"""\b{Regex.Escape(attributeName)}\s*=\s*["'](?<value>[^"']*)["']""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    [GeneratedRegex(@"<a\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnchorTagRegex();
}
