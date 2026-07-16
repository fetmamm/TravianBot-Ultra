namespace TbotUltra.Desktop.Models;

internal sealed record BrowserActivityStatisticsRow(
    string Metric,
    string Session,
    string Lifetime);

internal sealed record BrowserDestinationStatisticsRow(
    string Destination,
    long SessionNavigations,
    long SessionReloads,
    long LifetimeNavigations,
    long LifetimeReloads);
