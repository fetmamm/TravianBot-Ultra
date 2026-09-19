using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

public sealed record MainBuildingDurationAnomaly(
    int ActualSeconds,
    double ExpectedMaximumSeconds,
    double Ratio);

public static partial class MainBuildingDurationAnomalyDetector
{
    private const double AllowedRatio = 1.5;
    private const int MinimumExcessSeconds = 5;

    public static MainBuildingDurationAnomaly? Detect(
        int gid,
        int level,
        int actualSeconds,
        double serverSpeed)
    {
        // Building the Main Building itself must never be blocked by the signal that requested it.
        if (gid == 15 || level < 1 || actualSeconds <= 0 || serverSpeed <= 0)
        {
            return null;
        }

        var expectedMaximum = BuildingCatalogService.BuildSecondsFor(
            gid,
            level,
            serverSpeed,
            mainBuildingLevel: 1);
        if (expectedMaximum <= 0
            || actualSeconds < expectedMaximum * AllowedRatio
            || actualSeconds - expectedMaximum < MinimumExcessSeconds)
        {
            return null;
        }

        return new MainBuildingDurationAnomaly(
            actualSeconds,
            expectedMaximum,
            actualSeconds / expectedMaximum);
    }

    public static double ResolveServerSpeed(string? serverName, string? baseUrl)
    {
        var nameMatch = ServerSpeedNameRegex().Match(serverName ?? string.Empty);
        var rawNameSpeed = nameMatch.Groups[1].Success
            ? nameMatch.Groups[1].Value
            : nameMatch.Groups[2].Value;
        if (nameMatch.Success && double.TryParse(rawNameSpeed, out var nameSpeed) && nameSpeed > 0)
        {
            return nameSpeed;
        }

        var urlMatch = ServerSpeedUrlRegex().Match(baseUrl ?? string.Empty);
        return urlMatch.Success && double.TryParse(urlMatch.Groups[1].Value, out var urlSpeed) && urlSpeed > 0
            ? urlSpeed
            : 1.0;
    }

    [GeneratedRegex(@"(?:\b(\d+)\s*[xX]\b|\b[xX]\s*(\d+)\b)")]
    private static partial Regex ServerSpeedNameRegex();

    [GeneratedRegex(@"\.x(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ServerSpeedUrlRegex();
}
