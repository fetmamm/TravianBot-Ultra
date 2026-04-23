using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

public sealed class ServerDiscoveryService
{
    private const string IndexUrl = "https://ss-travi.com/International/index.php";

    public async Task<List<ServerOption>> FetchServersAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tbot Ultra Desktop");

        var html = await client.GetStringAsync(IndexUrl, cancellationToken);
        return ParseServersFromHtml(html);
    }

    public static List<ServerOption> ParseServersFromHtml(string html)
    {
        var servers = new Dictionary<string, ServerOption>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in ParseServerCards(html))
        {
            servers[option.BaseUrl] = option;
        }

        if (servers.Count == 0)
        {
            foreach (var fallback in ParseAnchors(html))
            {
                servers[fallback.BaseUrl] = fallback;
            }
        }

        return servers.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<ServerOption> ParseServerCards(string html)
    {
        var articleRegex = new Regex(
            "<article[^>]*class=[\"'][^\"']*server-card[^\"']*[\"'][^>]*>(?<body>.*?)</article>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var hrefRegex = new Regex(
            "<a[^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var titleRegex = new Regex(
            "<h3[^>]*class=[\"'][^\"']*server-title[^\"']*[\"'][^>]*>(?<title>.*?)</h3>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var speedRegex = new Regex(
            @"(?:&times;|\u00D7|x)\s*(?<speed>[0-9][0-9,\.]*)",
            RegexOptions.IgnoreCase);

        foreach (Match article in articleRegex.Matches(html))
        {
            var body = article.Groups["body"].Value;
            var href = hrefRegex.Match(body).Groups["href"].Value;
            if (!TryBuildServerOption(href, string.Empty, out var option))
            {
                continue;
            }

            var titleRaw = titleRegex.Match(body).Groups["title"].Value;
            var titleDecoded = WebUtility.HtmlDecode(titleRaw);
            var titleText = StripTags(titleDecoded);
            titleText = Regex.Replace(titleText, @"[\u2605]", string.Empty).Trim();

            var resolvedName = option.Name;
            var speedMatch = speedRegex.Match(titleDecoded);
            if (speedMatch.Success)
            {
                var speed = speedMatch.Groups["speed"].Value.Replace(",", string.Empty).Replace(".", string.Empty).Trim();
                titleText = Regex.Replace(titleText, @"(?:\u00D7|x)\s*[0-9][0-9,\.]*", string.Empty, RegexOptions.IgnoreCase).Trim();
                if (titleText.Length > 0 && speed.Length > 0)
                {
                    resolvedName = $"{titleText} {speed}x";
                }
            }
            else if (titleText.Length > 0)
            {
                resolvedName = titleText;
            }

            yield return new ServerOption
            {
                Name = resolvedName,
                BaseUrl = option.BaseUrl,
            };
        }
    }

    private static IEnumerable<ServerOption> ParseAnchors(string html)
    {
        var anchorRegex = new Regex(
            "<a[^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in anchorRegex.Matches(html))
        {
            var href = match.Groups["href"].Value;
            var text = StripTags(match.Groups["text"].Value);
            if (TryBuildServerOption(href, text, out var option))
            {
                yield return option;
            }
        }
    }

    private static bool TryBuildServerOption(string href, string text, out ServerOption option)
    {
        option = new ServerOption();
        if (!Uri.TryCreate(new Uri(IndexUrl), href, out var absolute))
        {
            return false;
        }

        var host = absolute.Host.ToLowerInvariant();
        if (!host.EndsWith("ss-travi.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (host is "ss-travi.com" or "www.ss-travi.com")
        {
            return false;
        }

        var baseUrl = $"{absolute.Scheme}://{absolute.Host}".TrimEnd('/');
        var name = CleanName(text, host);

        option = new ServerOption
        {
            Name = name,
            BaseUrl = baseUrl,
        };
        return true;
    }

    private static string CleanName(string text, string host)
    {
        var cleaned = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (cleaned.Length >= 3 && !IsGenericCtaText(cleaned))
        {
            return cleaned;
        }

        var shortHost = host.Replace(".ss-travi.com", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return shortHost switch
        {
            "pro" => "SS-Travi PRO 1024x",
            "mga" => "SS-Travi MEGA 1000000x",
            "mega" => "SS-Travi MEGA 1000000x",
            "vip" => "SS-Travi VIP 200x",
            "elt" => "SS-Travi ELITE 50000x",
            "elite" => "SS-Travi ELITE 50000x",
            "ult" => "SS-Travi TOURNAMENT 8x",
            "tournament" => "SS-Travi TOURNAMENT 8x",
            _ => $"SS-Travi {shortHost.ToUpperInvariant()}",
        };
    }

    private static bool IsGenericCtaText(string text)
    {
        var normalized = text.Trim();
        return normalized.Equals("Play now", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Enter the Arena", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Join now", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Register", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripTags(string input)
    {
        var noTags = Regex.Replace(input ?? string.Empty, "<.*?>", string.Empty);
        return Regex.Replace(noTags, @"\s+", " ").Trim();
    }
}
