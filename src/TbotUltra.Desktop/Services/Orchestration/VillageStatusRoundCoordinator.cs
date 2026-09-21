namespace TbotUltra.Desktop.Services.Orchestration;

internal sealed record VillageStatusRoundVillage(string Key, string Name, string? Url);

internal readonly record struct VillageStatusRoundVisitResult(
    bool ShouldContinue,
    bool InboxStatusChecked);

internal readonly record struct VillageStatusRoundResult(
    bool Completed,
    int VisitedVillages);

internal interface IVillageStatusRoundPort
{
    ValueTask PrepareAsync(CancellationToken cancellationToken);

    ValueTask<VillageStatusRoundVisitResult> VisitAsync(
        VillageStatusRoundVillage village,
        int villageNumber,
        int villageCount,
        bool inboxStatusChecked,
        CancellationToken cancellationToken);

    ValueTask DelayBeforeNextVillageAsync(CancellationToken cancellationToken);
}

internal sealed class VillageStatusRoundCoordinator(Func<int, int, int>? nextRandom = null)
{
    private readonly Func<int, int, int> _nextRandom = nextRandom ?? Random.Shared.Next;

    internal async ValueTask<VillageStatusRoundResult> RunAsync(
        IReadOnlyList<VillageStatusRoundVillage> villages,
        IVillageStatusRoundPort port,
        CancellationToken cancellationToken,
        bool preserveOrder = false,
        Action<VillageStatusRoundVillage>? onVisited = null,
        int startIndex = 0,
        int? totalCount = null)
    {
        if (villages.Count == 0)
        {
            return new VillageStatusRoundResult(Completed: false, VisitedVillages: 0);
        }

        var ordered = preserveOrder ? villages : Shuffle(villages);
        await port.PrepareAsync(cancellationToken).ConfigureAwait(false);
        var inboxStatusChecked = false;
        for (var index = 0; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visit = await port.VisitAsync(
                    ordered[index],
                    startIndex + index + 1,
                    totalCount ?? ordered.Count,
                    inboxStatusChecked,
                    cancellationToken)
                .ConfigureAwait(false);
            inboxStatusChecked |= visit.InboxStatusChecked;
            if (!visit.ShouldContinue)
            {
                return new VillageStatusRoundResult(Completed: false, VisitedVillages: index + 1);
            }

            onVisited?.Invoke(ordered[index]);

            if (index < ordered.Count - 1)
            {
                await port.DelayBeforeNextVillageAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new VillageStatusRoundResult(Completed: true, VisitedVillages: ordered.Count);
    }

    private IReadOnlyList<VillageStatusRoundVillage> Shuffle(
        IReadOnlyList<VillageStatusRoundVillage> villages)
    {
        var shuffled = villages.ToList();
        for (var index = shuffled.Count - 1; index > 0; index--)
        {
            var selected = _nextRandom(0, index + 1);
            (shuffled[index], shuffled[selected]) = (shuffled[selected], shuffled[index]);
        }
        return shuffled;
    }

    internal IReadOnlyList<VillageStatusRoundVillage> OrderStartingAt(
        IReadOnlyList<VillageStatusRoundVillage> villages,
        string? activeVillageKey)
    {
        var current = villages.FirstOrDefault(village =>
            string.Equals(village.Key, activeVillageKey, StringComparison.OrdinalIgnoreCase));
        var rest = Shuffle(villages.Where(village => village != current).ToList());
        return current is null ? rest : [current, .. rest];
    }
}
