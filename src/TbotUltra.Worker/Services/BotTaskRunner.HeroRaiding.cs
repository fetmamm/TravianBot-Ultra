using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services.Automation;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static async Task ExecuteHeroRaidAsync(TaskExecutionContext context)
    {
        if (!context.Options.HeroRaidingEnabled)
        {
            context.Log("[hero-raiding] global toggle is off; no raid will be sent.");
            return;
        }

        var client = context.Client;
        var root = context.Runner._projectContext.RootPath;
        var account = client.AccountName;
        var world = client.ServerUrl;
        var pendingStore = new HeroRaidingPendingOutcomeStore(root, context.Log);
        var schedule = new HeroRaidingScheduleStore(root, context.Log);
        var pending = pendingStore.Load(account, world);
        if (pending is not null)
        {
            // A persisted safety stop is released only after the user explicitly turned the toggle on again.
            // Never allow the next dispatch while the previous hero is still travelling or dead.
            if (pending.SafetyDisabled)
            {
                var acknowledged = await client.ReadHeroRaidingLiveOutcomeAsync(context.CancellationToken);
                if (!acknowledged.StateKnown)
                    throw new HeroRaidingSafetyException("Hero raiding remains disabled: hero status is unknown after user re-enable.");
                if (acknowledged.Dead || !acknowledged.Home)
                    throw new TaskWaitException(300, "Hero raiding waits for the hero to be alive and home after user re-enable.");
                pendingStore.ClearVerified(account, world);
                context.Log("[hero-raiding] user re-enabled raiding and the hero is alive and home; previous safety stop acknowledged.");
                pending = null;
            }
            else if (!pending.OutgoingVerified || pending.ExpectedHomeUtc is null)
            {
                pendingStore.MarkSafetyDisabled(account, world, pending);
                throw new HeroRaidingSafetyException("Hero raiding stopped: the previous final Confirm had an unknown outgoing result.");
            }
            else
            {
                var untilExpectedHome = pending.ExpectedHomeUtc.Value.AddSeconds(15) - DateTimeOffset.UtcNow;
                if (untilExpectedHome > TimeSpan.Zero)
                {
                    var waitSeconds = (int)Math.Clamp(Math.Ceiling(untilExpectedHome.TotalSeconds), 1, 604800);
                    throw new TaskWaitException(waitSeconds,
                        $"Hero oasis raid is travelling; checking live home status after {pending.ExpectedHomeUtc:O}.",
                        TaskWaitReasons.HeroAway);
                }

                var live = await client.ReadHeroRaidingLiveOutcomeAsync(context.CancellationToken);
                var decision = HeroRaidingOutcomePolicy.Evaluate(
                    new HeroRaidingOutcomeObservation(pending.OutgoingVerified,
                        live.StateKnown, live.Home, live.Dead, live.HpPercent),
                    pending.PredictedHpAfterRaid,
                    pending.MinimumHpPercent);
                context.Log($"[hero-raiding] return verification: {decision.Kind}; {decision.Reason}");
                if (decision.Kind == HeroRaidingOutcomeKind.Pending)
                    throw new TaskWaitException(60, decision.Reason, TaskWaitReasons.HeroAway);
                if (decision.Kind == HeroRaidingOutcomeKind.Disable)
                {
                    pendingStore.MarkSafetyDisabled(account, world, pending);
                    throw new HeroRaidingSafetyException($"Hero raiding stopped: {decision.Reason}");
                }
                pendingStore.ClearVerified(account, world);
                // One browser task verifies the previous combat; the next pass may discover another target.
                return;
            }
        }

        if (!schedule.IsDiscoveryDue(account, world, DateTimeOffset.UtcNow))
        {
            var next = schedule.LoadNextDiscoveryUtc(account, world)!.Value;
            var waitSeconds = (int)Math.Clamp(Math.Ceiling((next - DateTimeOffset.UtcNow).TotalSeconds), 1, 3600);
            throw new TaskWaitException(waitSeconds, $"Hero raiding quiet period until {next:O}.");
        }

        var types = context.Options.HeroRaidingSelectedTypes.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (types.Length == 0)
        {
            schedule.RecordNoCandidate(account, world, DateTimeOffset.UtcNow);
            context.Log("[hero-raiding] no oasis types selected; no raid sent.");
            return;
        }

        var readiness = await client.ReadHeroRaidingReadinessAsync(context.CancellationToken);
        if (readiness is null)
        {
            var wait = await client.ReadHeroRaidingAvailableWaitSecondsAsync(context.CancellationToken);
            throw new TaskWaitException(wait, "Hero raiding waits for the hero to return home.", TaskWaitReasons.HeroAway);
        }

        var exclusions = new HeroRaidingExclusionStore(root, context.Log).Load(account, world);
        await client.PrepareMapOasisScanAsync(context.CancellationToken);
        var discoveryOperation = new HeroRaidingDiscoveryOperation(client, root, account, world, context.Log);
        var discovery = await discoveryOperation
            .ExecuteAsync(new HeroRaidingDiscoveryInput(
                readiness.VillageX, readiness.VillageY,
                context.Options.HeroRaidingMaximumDistance,
                context.Options.HeroRaidingObservationMinutes,
                types, exclusions,
                readiness.AttackStrength,
                readiness.HpPercent,
                readiness.Mounted,
                context.Options.HeroRaidingMinimumHpPercent,
                MinimumCandidates: context.Options.HeroRaidingMinimumCandidates), context.CancellationToken);
        if (discovery.SafeCandidates.Count == 0 && context.Options.HeroAutoUseOintments)
        {
            var targetHp = HeroCalc.NormalizeOintmentTargetHp(context.Options.HeroOintmentTargetHpPercent);
            var canBecomeSafe = readiness.HpPercent < targetHp
                && HeroRaidingCandidateSelector.Select(
                    discovery.ObservedOases, readiness.VillageX, readiness.VillageY,
                    context.Options.HeroRaidingMaximumDistance,
                    context.Options.HeroRaidingObservationMinutes,
                    types, exclusions, DateTimeOffset.UtcNow)
                    .Any(candidate => HeroOasisRaidEstimator.TryEstimate(candidate.Garrison,
                            readiness.AttackStrength, targetHp, readiness.Mounted, out var estimate)
                        && estimate!.PredictedHpAfterRaid >= context.Options.HeroRaidingMinimumHpPercent);
            if (canBecomeSafe)
            {
                context.Log($"[hero-raiding] safe animal oasis requires healing; trying shared ointment setting toward {targetHp}% HP.");
                var use = await client.TryUseHeroOintmentsForAdventureAsync(
                    readiness.HpPercent, context.Options.HeroRaidingMinimumHpPercent,
                    targetHp, adventureCount: 0, context.CancellationToken);
                if (use.UsedCount > 0)
                {
                    readiness = await client.ReadHeroRaidingReadinessAsync(context.CancellationToken)
                        ?? throw new InvalidOperationException("Hero became unavailable after using ointments.");
                    await client.PrepareMapOasisScanAsync(context.CancellationToken);
                    discovery = await discoveryOperation.ExecuteAsync(new HeroRaidingDiscoveryInput(
                        readiness.VillageX, readiness.VillageY,
                        context.Options.HeroRaidingMaximumDistance,
                        context.Options.HeroRaidingObservationMinutes,
                        types, exclusions,
                        readiness.AttackStrength,
                        readiness.HpPercent,
                        readiness.Mounted,
                        context.Options.HeroRaidingMinimumHpPercent,
                        MinimumCandidates: context.Options.HeroRaidingMinimumCandidates), context.CancellationToken);
                }
            }
        }
        if (discovery.SafeCandidates.Count == 0)
        {
            schedule.RecordNoCandidate(account, world, DateTimeOffset.UtcNow);
            context.Log("[hero-raiding] no safe animal oasis found; no raid sent; retry in 60 minutes.");
            return;
        }
        schedule.ClearAfterSuccessfulScan(account, world);

        var selected = discovery.SafeCandidates[0];
        var target = selected.Candidate.Oasis;
        var latestReadiness = await client.ReadHeroRaidingReadinessAsync(context.CancellationToken);
        if (latestReadiness is null
            || latestReadiness.VillageX != readiness.VillageX
            || latestReadiness.VillageY != readiness.VillageY)
            throw new TaskWaitException(300, "Hero raiding waits: the hero or its home village changed during target discovery.", TaskWaitReasons.HeroAway);
        if (!HeroOasisRaidEstimator.TryEstimate(selected.Candidate.Garrison,
                latestReadiness.AttackStrength, latestReadiness.HpPercent,
                latestReadiness.Mounted, out var latestEstimate)
            || latestEstimate!.PredictedHpAfterRaid < context.Options.HeroRaidingMinimumHpPercent)
            throw new TaskWaitException(300, "Hero raiding waits: the selected oasis is no longer safe with current hero equipment and HP.");
        readiness = latestReadiness;
        context.Log($"[hero-raiding] selected ({target.X}|{target.Y}), animals='{target.Animals}', " +
            $"predicted HP={latestEstimate.PredictedHpAfterRaid}%, current HP={readiness.HpPercent}%.");
        await client.PrepareHeroRaidingSendFormAsync(readiness, target.X, target.Y, context.CancellationToken);

        HeroRaidingPendingOutcome? dispatched = null;
        try
        {
            var outgoingVerified = await client.SendHeroRaidAsync(readiness, target.X, target.Y,
                expectedHomeUtc =>
                {
                    dispatched = new HeroRaidingPendingOutcome(
                        readiness.VillageX, readiness.VillageY, target.X, target.Y,
                        readiness.HpPercent, latestEstimate.PredictedHpAfterRaid,
                        context.Options.HeroRaidingMinimumHpPercent,
                        DateTimeOffset.UtcNow, expectedHomeUtc);
                    pendingStore.SaveSent(account, world, dispatched);
                    return Task.CompletedTask;
                }, context.CancellationToken);
            if (!outgoingVerified || dispatched is null)
                throw new HeroRaidingSafetyException("Final Confirm did not yield a verified outgoing hero-only Raid.");
            pendingStore.SaveSent(account, world, dispatched with { OutgoingVerified = true });
            var wait = (int)Math.Clamp(Math.Ceiling(
                (dispatched.ExpectedHomeUtc!.Value.AddSeconds(15) - DateTimeOffset.UtcNow).TotalSeconds), 1, 604800);
            throw new TaskWaitException(wait,
                $"Hero-only oasis Raid confirmed; checking return after {dispatched.ExpectedHomeUtc:O}.",
                TaskWaitReasons.HeroAway);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskWaitException && dispatched is not null)
        {
            pendingStore.MarkSafetyDisabled(account, world, dispatched);
            throw new HeroRaidingSafetyException(
                $"Hero raiding stopped because the outgoing result is uncertain: {ex.Message}", ex);
        }
    }

    public async Task<HeroRaidingScanResult> ScanHeroRaidingCandidatesAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default,
        IProgress<HeroRaidingScanProgress>? progress = null)
    {
        HeroRaidingScanResult? result = null;
        await ExecuteWithClientAsync(options, log, accountName, interactive: true, cancellationToken,
            async client =>
            {
                var types = options.HeroRaidingSelectedTypes.Split(',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (types.Count == 0)
                {
                    result = new HeroRaidingScanResult(false, false, [], 0, 0,
                        "Select at least one oasis type before scanning.");
                    return;
                }

                var readiness = await client.ReadHeroRaidingReadinessAsync(cancellationToken);
                if (readiness is null)
                {
                    result = new HeroRaidingScanResult(false, false, [], 0, 0,
                        "The hero is not confirmed home and available. Scan again after the hero returns.");
                    return;
                }

                var exclusions = new HeroRaidingExclusionStore(_projectContext.RootPath, log)
                    .Load(client.AccountName, client.ServerUrl);
                var schedule = new HeroRaidingScheduleStore(_projectContext.RootPath, log);
                await client.PrepareMapOasisScanAsync(cancellationToken);
                var discovery = await new HeroRaidingDiscoveryOperation(
                    client, _projectContext.RootPath, client.AccountName, client.ServerUrl, log)
                    .ExecuteAsync(new HeroRaidingDiscoveryInput(
                        readiness.VillageX, readiness.VillageY,
                        options.HeroRaidingMaximumDistance,
                        options.HeroRaidingObservationMinutes,
                        types,
                        exclusions,
                        readiness.AttackStrength,
                        readiness.HpPercent,
                        readiness.Mounted,
                        options.HeroRaidingMinimumHpPercent,
                        ScanAllAreas: true), cancellationToken, progress);

                if (discovery.SafeCandidates.Count == 0)
                    schedule.RecordNoCandidate(client.AccountName, client.ServerUrl, DateTimeOffset.UtcNow);
                else
                    schedule.ClearAfterSuccessfulScan(client.AccountName, client.ServerUrl);

                var candidates = new List<HeroRaidingScanCandidate>();
                foreach (var oasis in discovery.ObservedOases)
                {
                    if (oasis.IsOccupied || string.IsNullOrWhiteSpace(oasis.Animals)
                        || !HeroRaidingCandidateSelector.MatchesSelectedType(oasis, types))
                        continue;
                    var distance = Math.Sqrt(Math.Pow(oasis.X - readiness.VillageX, 2)
                        + Math.Pow(oasis.Y - readiness.VillageY, 2));
                    if (distance <= 0 || distance > options.HeroRaidingMaximumDistance) continue;
                    var parsedGarrison = NatureTroopCatalog.TryParseGarrison(oasis.Animals, out var garrison);
                    HeroOasisRaidEstimate? estimate = null;
                    var estimated = parsedGarrison && HeroOasisRaidEstimator.TryEstimate(garrison,
                        readiness.AttackStrength, readiness.HpPercent, readiness.Mounted, out estimate);
                    candidates.Add(new HeroRaidingScanCandidate(
                        oasis.X, oasis.Y, oasis.OasisType, oasis.Animals,
                        estimated ? estimate!.PredictedHpAfterRaid : null,
                        estimated ? estimate!.EstimatedBountyPerResource : null,
                        distance,
                        exclusions.Contains((oasis.X, oasis.Y)),
                        estimated && estimate!.PredictedHpAfterRaid >= options.HeroRaidingMinimumHpPercent,
                        oasis.ObservedAtUtc ?? DateTimeOffset.UtcNow));
                }

                candidates = candidates.OrderByDescending(candidate => candidate.IsSafe && !candidate.IsExcluded)
                    .ThenByDescending(candidate => (candidate.EstimatedBountyPerResource ?? 0) / candidate.DistanceFields)
                    .ThenByDescending(candidate => candidate.PredictedHpAfterRaid)
                    .ToList();
                var message = $"Found {candidates.Count} animal oasis candidate(s); {discovery.SafeCandidates.Count} safe estimate(s).";
                if (discovery.SafeCandidates.Count == 0)
                    message += " The next automatic discovery is in 60 minutes; Scan now can retry earlier.";
                if (readiness.CagesEquipped)
                    message += " Cages are equipped and must be removed before raiding.";
                result = new HeroRaidingScanResult(true, readiness.CagesEquipped, candidates,
                    discovery.AreasRead, discovery.CachedAreasUsed, message);
                log($"[hero-raiding] scan complete: {message} areasRead={discovery.AreasRead}, cached={discovery.CachedAreasUsed}.");
            });

        return result ?? throw new InvalidOperationException("Hero raiding scan ended without a result.");
    }
}
