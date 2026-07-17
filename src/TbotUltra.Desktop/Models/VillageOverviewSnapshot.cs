using System;
using System.Collections.Generic;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Models;

public sealed record VillageOverviewSnapshot(
    string RunningTask,
    IReadOnlyList<UpcomingTaskRow> UpcomingTasks,
    IReadOnlyList<VillageOverviewRow> Villages,
    DateTimeOffset CapturedAtUtc);

public sealed record UpcomingTaskRow(
    string Position,
    string Task,
    string Village,
    string Group,
    string Timing,
    string Source);

public sealed record VillageOverviewRow(
    string Village,
    string Population,
    string NextTask,
    string Construction,
    string Smithy,
    string BuildTroops,
    string Farming,
    string Hero,
    string TownHall,
    string Brewery,
    string ResourceTransfer,
    string Reinforcements);

internal sealed record VillageOverviewSource(
    string VillageKey,
    string Name,
    string Population,
    string Tribe,
    bool IsEnabled,
    IReadOnlySet<string> EnabledGroups,
    bool IsHeroHome,
    string? TownHallMode,
    DateTimeOffset? TownHallEndsAtUtc,
    VillageStatus? Status);

internal sealed record PipelineTaskSource(
    QueueItem Item,
    string DisplayName,
    string? VillageKey,
    string VillageName,
    bool IsAllowed);
