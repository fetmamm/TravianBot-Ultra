using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

// Built-in catalog of official Travian Legends game worlds for the account server picker,
// grouped by region. Official worlds follow the stable ts{N}.x{speed}.{region}.travian.com
// scheme; this list covers the recurring worlds (1x worlds 1-6 plus the 2x/3x/5x/10x
// specials). Not every listed world is running at all times — the picker is a convenience
// and login simply fails if a closed world is chosen. Custom servers (e.g. SS-Travi) still
// come from the user server list and are shown in the "Custom" group.
public static class OfficialServerCatalog
{
    public const string CustomGroupName = "Custom";

    private static readonly (string Group, string Subdomain)[] Regions =
    [
        ("America", "america"),
        ("Arabia", "arabics"),
        ("Asia", "asia"),
        ("Europe", "europe"),
        ("International", "international"),
    ];

    // (world number, speed). World numbers encode the speed tier on official servers:
    // 1-9 are the regular 1x worlds, 20 = 2x, 30/31 = 3x, 50 = 5x, 100 = 10x. Letter-coded
    // specials (nys, ttq, rof, ...) rotate during the year and are left to the custom list.
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
}
