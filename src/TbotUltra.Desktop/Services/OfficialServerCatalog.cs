using System.Text.RegularExpressions;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

// Built-in catalog of official Travian Legends game worlds for the account server picker,
// grouped by region. Official worlds follow the stable ts{N}.x{speed}.{region}.travian.com
// scheme; this list covers the recurring worlds (1x worlds 1-6 plus the 2x/3x/5x/10x
// specials). Not every listed world is running at all times — the picker is a convenience
// and login simply fails if a closed world is chosen. Rotating special worlds are added from
// Travian's public calendar, while user-added servers remain in the "Custom" group.
public static class OfficialServerCatalog
{
    public const string SpecialGroupName = "Special";
    public const string CustomGroupName = "Custom";

    private static readonly Regex StandardWorldHostPattern = new(
        @"^ts\d+\.x\d+\.(america|arabics|asia|europe|international)\.travian\.com$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string Group, string Subdomain)[] Regions =
    [
        ("America", "america"),
        ("Arabia", "arabics"),
        ("Asia", "asia"),
        ("Europe", "europe"),
        ("International", "international"),
    ];

    // (world number, speed). World numbers encode the speed tier on official servers:
    // 1-9 are the regular 1x worlds, 20 = 2x, 30/31 = 3x, 50 = 5x, 100 = 10x.
    private static readonly (int World, int Speed)[] Worlds =
    [
        (1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1), (7, 1), (8, 1), (9, 1),
        (20, 2),
        (30, 3), (31, 3),
        (50, 5),
        (100, 10),
    ];

    public static List<ServerOption> GetOfficialServers()
    {
        var servers = new List<ServerOption>();
        foreach (var (group, subdomain) in Regions)
        {
            foreach (var (world, speed) in Worlds)
            {
                servers.Add(new ServerOption
                {
                    // Speed in parentheses so speed parsing (ResolveServerSpeed /
                    // ServerSpeedLabel) picks up "(10x)" and not the world number.
                    Name = $"{group} {world} ({speed}x)",
                    BaseUrl = $"https://ts{world}.x{speed}.{subdomain}.travian.com",
                    Group = group,
                });
            }
        }

        return servers;
    }

    internal static bool IsStandardWorldUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && StandardWorldHostPattern.IsMatch(uri.Host);
    }

    internal static List<ServerOption> BuildPickerServers(
        IEnumerable<ServerOption> customServers,
        IEnumerable<ServerOption> specialServers,
        IEnumerable<ServerOption> officialServers)
    {
        var combined = new List<ServerOption>();
        var specialUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in specialServers.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            option.Group = SpecialGroupName;
            var normalizedUrl = NormalizeUrl(option.BaseUrl);
            if (normalizedUrl.Length > 0 && specialUrls.Add(normalizedUrl))
            {
                combined.Add(option);
            }
        }

        foreach (var option in customServers.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            option.Group = CustomGroupName;
            if (!specialUrls.Contains(NormalizeUrl(option.BaseUrl)))
            {
                combined.Add(option);
            }
        }

        combined.AddRange(officialServers);
        return combined;
    }

    private static string NormalizeUrl(string value)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : (value ?? string.Empty).Trim().TrimEnd('/');
    }
}
