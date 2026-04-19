using System.Net.Http;
using System.Net;
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
            @"(?:&times;|×|x)\s*(?<speed>[0-9][0-9,\.]*)",
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
            var titleText = StripTags(WebUtility.HtmlDecode(titleRaw));
            titleText = titleText.Replace("★", string.Empty).Trim();
            var resolvedName = option.Name;

            var speedMatch = speedRegex.Match(WebUtility.HtmlDecode(titleRaw));
            if (speedMatch.Success)
            {
                var speed = speedMatch.Groups["speed"].Value.Replace(",", string.Empty).Replace(".", string.Empty).Trim();
                titleText = Regex.Replace(titleText, @"(?:×|x)\s*[0-9][0-9,\.]*", string.Empty, RegexOptions.IgnoreCase).Trim();
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
        if (cleaned.Length >= 3)
        {
            return cleaned;
        }

        var shortHost = host.Replace(".ss-travi.com", string.Empty, StringComparison.OrdinalIgnoreCase);
        return shortHost.ToUpperInvariant();
    }

    private static string StripTags(string input)
    {
        var noTags = Regex.Replace(input ?? string.Empty, "<.*?>", string.Empty);
        return Regex.Replace(noTags, @"\s+", " ").Trim();
    }
}
