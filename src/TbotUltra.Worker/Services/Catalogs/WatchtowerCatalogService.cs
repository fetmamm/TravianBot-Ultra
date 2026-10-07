namespace TbotUltra.Worker.Services;

public sealed record WatchtowerCatalogLevel(
    int Level,
    int Wood,
    int Clay,
    int Iron,
    int Crop,
    int BaseBuildSeconds,
    int Population,
    int CulturePoints);

public static class WatchtowerCatalogService
{
    private static readonly (int Wood, int Clay, int Iron, int Crop)[] Costs =
    [
        (240, 110, 275, 100), (305, 140, 350, 130), (395, 180, 450, 165),
        (505, 230, 575, 210), (645, 295, 740, 270), (825, 380, 945, 345),
        (1055, 485, 1210, 440), (1350, 620, 1550, 565), (1730, 795, 1980, 720),
        (2215, 1015, 2535, 920), (2835, 1300, 3245, 1180), (3625, 1660, 4155, 1510),
        (4640, 2130, 5320, 1935), (5940, 2725, 6810, 2475), (7605, 3485, 8715, 3170),
        (9735, 4460, 11155, 4055), (12460, 5710, 14280, 5190), (15950, 7310, 18275, 6645),
        (20415, 9360, 23395, 8505), (26135, 11980, 29945, 10890),
    ];

    public static IReadOnlyList<WatchtowerCatalogLevel> Levels { get; } = Costs
        .Select((cost, index) =>
        {
            var level = index + 1;
            return new WatchtowerCatalogLevel(
                level,
                cost.Wood,
                cost.Clay,
                cost.Iron,
                cost.Crop,
                (int)(Math.Round(2800d * Math.Pow(1.16d, level - 1) / 10d, MidpointRounding.AwayFromZero) * 10d),
                (2 * level) + 2,
                level + 2);
        })
        .ToList();

    public static WatchtowerCatalogLevel? Level(int level) =>
        level is >= 1 and <= 20 ? Levels[level - 1] : null;

    public static double BuildSecondsFor(int level, double serverSpeed, int mainBuildingLevel)
    {
        var entry = Level(level);
        if (entry is null)
        {
            return 0;
        }

        var speed = serverSpeed > 0 ? serverSpeed : 1d;
        var mainBuildingFactor = Math.Pow(0.964d, Math.Max(0, mainBuildingLevel - 1));
        var scaledSeconds = entry.BaseBuildSeconds * mainBuildingFactor / speed;
        return Math.Max(10d, Math.Round(scaledSeconds / 10d, MidpointRounding.AwayFromZero) * 10d);
    }
}
