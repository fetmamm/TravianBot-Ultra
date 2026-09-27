using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal enum ProductionBonusActivationStatus
{
    Completed,
    Failed,
    CooldownActive,
    Unavailable,
}

internal sealed record ProductionBonusActivationResult(
    ProductionBonusActivationStatus Status,
    string Message,
    bool MayRetry = false,
    DateTimeOffset? RetryAtUtc = null);

internal interface IProductionBonusBrowser
{
    Task<ProductionBonusObservation> InspectAsync(bool afterActivationAttempt, CancellationToken cancellationToken);
    IProductionBonusActivationBatch BeginActivationBatch();
    Task ActivateInCurrentBrowserAsync(IReadOnlyList<string> resources, CancellationToken cancellationToken);
    Task RestoreMainPageAsync();
}

internal interface IProductionBonusActivationBatch
{
    Task<ProductionBonusActivationResult> ActivateAsync(string resource, CancellationToken cancellationToken);
}

internal sealed class ProductionBonusOperation
{
    private const int MaxAttemptsPerResource = 2;
    private readonly IProductionBonusBrowser _browser;
    private readonly Action<string> _log;

    internal ProductionBonusOperation(IProductionBonusBrowser browser, Action<string> log)
    {
        _browser = browser;
        _log = log;
    }

    internal async Task<ProductionBonusOutcome> RunAsync(
        ProductionBonusRunIntent intent,
        CancellationToken cancellationToken)
    {
        var initial = await _browser.InspectAsync(
            afterActivationAttempt: intent == ProductionBonusRunIntent.Activate,
            cancellationToken);
        if (initial.AccountDeletionPending)
        {
            return AccountDeletionPending();
        }

        if (intent == ProductionBonusRunIntent.Inspect)
        {
            _log($"[production-bonus] scan done — {FormatState(initial.Resources)}.");
            return ProductionBonusOutcome.Observed(
                "Production bonus: scanned.",
                initial.Resources,
                initial.ServerUtcOffset,
                initial.ActivatableResources.Count > 0);
        }

        var activatable = initial.ActivatableResources;
        if (activatable.Count == 0)
        {
            _log($"[production-bonus] nothing to activate — {FormatState(initial.Resources)}.");
            return ProductionBonusOutcome.Observed(
                "Production bonus: nothing to activate.",
                initial.Resources,
                initial.ServerUtcOffset,
                freeVideoAvailable: false);
        }

        _log($"[production-bonus] activatable resources: {string.Join(", ", activatable)}.");
        var batch = _browser.BeginActivationBatch();
        var usedCurrentBrowserFallback = false;

        for (var resourceIndex = 0; resourceIndex < activatable.Count; resourceIndex++)
        {
            var resource = activatable[resourceIndex];
            for (var attempt = 1; attempt <= MaxAttemptsPerResource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activation = new ProductionBonusActivationResult(
                    ProductionBonusActivationStatus.Failed,
                    "Unknown activation failure.",
                    MayRetry: true);
                try
                {
                    activation = await batch.ActivateAsync(resource, cancellationToken);
                    if (activation.Status == ProductionBonusActivationStatus.Unavailable)
                    {
                        await _browser.ActivateInCurrentBrowserAsync(activatable, cancellationToken);
                        usedCurrentBrowserFallback = true;
                        break;
                    }

                    if (activation.Status == ProductionBonusActivationStatus.CooldownActive)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var retryAt = activation.RetryAtUtc is { } candidate && candidate > now
                            ? candidate
                            : now.AddSeconds(1);
                        if (resourceIndex == 0 && attempt == 1)
                        {
                            return ProductionBonusOutcome.Deferred(
                                $"Production bonus: video cooldown active ({activation.Message}).",
                                retryAt.AddSeconds(5));
                        }

                        _log($"[production-bonus:verbose] {resource}: cooldown did not stop the current batch; continuing verification.");
                    }
                    else if (activation.Status != ProductionBonusActivationStatus.Completed)
                    {
                        _log($"[production-bonus:verbose] {resource}: isolated bonus video ended with {activation.Status}: {activation.Message}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log($"[production-bonus:verbose] {resource}: isolated bonus video failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (!usedCurrentBrowserFallback)
                    {
                        await _browser.RestoreMainPageAsync();
                    }
                }

                if (usedCurrentBrowserFallback)
                {
                    break;
                }

                var confirmed = false;
                try
                {
                    var verification = await _browser.InspectAsync(afterActivationAttempt: true, cancellationToken);
                    if (verification.AccountDeletionPending)
                    {
                        return AccountDeletionPending();
                    }

                    confirmed = verification.Resources.Any(state =>
                        string.Equals(state.Resource, resource, StringComparison.OrdinalIgnoreCase)
                        && state.Bonus is 15 or 25);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    activation = activation with { MayRetry = true };
                    _log($"[production-bonus:verbose] {resource}: fresh activation verification failed: {ex.GetType().Name}: {ex.Message}");
                }

                if (confirmed)
                {
                    _log($"[production-bonus] {resource}: activation verified after attempt {attempt}/{MaxAttemptsPerResource}.");
                    break;
                }

                if (attempt < MaxAttemptsPerResource && activation.MayRetry)
                {
                    _log($"[production-bonus] {resource}: activation not confirmed after attempt {attempt}/{MaxAttemptsPerResource}; retrying once.");
                    continue;
                }

                _log($"[production-bonus:verbose] {resource}: activation not confirmed after attempt {attempt}/{MaxAttemptsPerResource}; continuing the current batch.");
                break;
            }

            if (usedCurrentBrowserFallback)
            {
                break;
            }
        }

        var finalObservation = await _browser.InspectAsync(afterActivationAttempt: true, cancellationToken);
        if (finalObservation.AccountDeletionPending)
        {
            return AccountDeletionPending();
        }

        var confirmedResources = finalObservation.Resources
            .Where(state => state.Bonus is 15 or 25)
            .Select(state => state.Resource)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unconfirmed = activatable
            .Where(resource => !confirmedResources.Contains(resource))
            .ToList();
        if (unconfirmed.Count > 0)
        {
            _log($"[production-bonus] ALARM: activation was not confirmed for {string.Join(", ", unconfirmed)} after at most {MaxAttemptsPerResource} attempt(s) per resource; normal automation continues.");
        }

        _log($"[production-bonus] done — {FormatState(finalObservation.Resources)}.");
        return ProductionBonusOutcome.Observed(
            $"Production bonus: processed {activatable.Count} resource(s).",
            finalObservation.Resources,
            finalObservation.ServerUtcOffset,
            freeVideoAvailable: true,
            attemptedResources: activatable,
            unconfirmedResources: unconfirmed);
    }

    private ProductionBonusOutcome AccountDeletionPending()
    {
        _log("[production-bonus] disabled — the account is pending deletion and Shop is unavailable.");
        return ProductionBonusOutcome.AccountDeletionPending(
            "Production bonus: disabled because the account is pending deletion.");
    }

    private static string FormatState(IReadOnlyList<ProductionBonusResourceState> states)
        => string.Join(
            ", ",
            states.Select(state => state.Bonus > 0
                ? $"{state.Resource}=+{state.Bonus}%/{state.RemainingSeconds}s"
                : $"{state.Resource}=none"));
}
