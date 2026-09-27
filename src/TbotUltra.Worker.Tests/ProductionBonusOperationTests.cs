using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ProductionBonusOperationTests
{
    [Fact]
    public async Task RunAsync_ActivatesEveryResourceInOneBatch()
    {
        var browser = new FakeBrowser(
            [
                Observation(("lumber", 0, true), ("clay", 0, true)),
                Observation(("lumber", 15, false), ("clay", 0, true)),
                Observation(("lumber", 15, false), ("clay", 15, false)),
                Observation(("lumber", 15, false), ("clay", 15, false)),
            ],
            [Completed(), Completed()]);

        var outcome = await new ProductionBonusOperation(browser, _ => { })
            .RunAsync(ProductionBonusRunIntent.Activate, CancellationToken.None);

        Assert.Equal(ProductionBonusOutcomeStatus.Observed, outcome.Status);
        Assert.Equal(["lumber", "clay"], outcome.AttemptedResources);
        Assert.Empty(outcome.UnconfirmedResources);
        Assert.Equal(["lumber", "clay"], browser.ActivatedResources);
    }

    [Fact]
    public async Task RunAsync_ReportsOnlyUnconfirmedResourceAndContinuesBatch()
    {
        var browser = new FakeBrowser(
            [
                Observation(("lumber", 0, true), ("clay", 0, true)),
                Observation(("lumber", 15, false), ("clay", 0, true)),
                Observation(("lumber", 15, false), ("clay", 0, true)),
                Observation(("lumber", 15, false), ("clay", 0, true)),
            ],
            [Completed(), Failed()]);

        var outcome = await new ProductionBonusOperation(browser, _ => { })
            .RunAsync(ProductionBonusRunIntent.Activate, CancellationToken.None);

        Assert.Equal(["lumber", "clay"], browser.ActivatedResources);
        Assert.Equal(["clay"], outcome.UnconfirmedResources);
    }

    [Fact]
    public async Task RunAsync_DefersOnlyWhenCooldownPrecedesTheBatch()
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var beforeBatch = new FakeBrowser(
            [Observation(("lumber", 0, true), ("clay", 0, true))],
            [Cooldown(retryAt)]);

        var deferred = await new ProductionBonusOperation(beforeBatch, _ => { })
            .RunAsync(ProductionBonusRunIntent.Activate, CancellationToken.None);

        Assert.Equal(ProductionBonusOutcomeStatus.Deferred, deferred.Status);
        Assert.Equal(["lumber"], beforeBatch.ActivatedResources);

        var duringBatch = new FakeBrowser(
            [
                Observation(("lumber", 0, true), ("clay", 0, true)),
                Observation(("lumber", 15, false), ("clay", 0, true)),
                Observation(("lumber", 15, false), ("clay", 0, true)),
                Observation(("lumber", 15, false), ("clay", 0, true)),
            ],
            [Completed(), Cooldown(retryAt)]);

        var completedBatch = await new ProductionBonusOperation(duringBatch, _ => { })
            .RunAsync(ProductionBonusRunIntent.Activate, CancellationToken.None);

        Assert.Equal(ProductionBonusOutcomeStatus.Observed, completedBatch.Status);
        Assert.Equal(["lumber", "clay"], duringBatch.ActivatedResources);
        Assert.Equal(["clay"], completedBatch.UnconfirmedResources);
    }

    private static ProductionBonusObservation Observation(
        params (string Resource, int Bonus, bool CanActivate)[] resources)
        => new(
            resources
                .Select(resource => new ProductionBonusResourceState(
                    resource.Resource,
                    resource.Bonus,
                    resource.Bonus > 0 ? 3600 : 0,
                    resource.Bonus > 0
                        ? ProductionBonusNextAttemptKind.DailyReset
                        : ProductionBonusNextAttemptKind.Immediate,
                    0,
                    resource.CanActivate))
                .ToList(),
            TimeSpan.Zero);

    private static ProductionBonusActivationResult Completed()
        => new(ProductionBonusActivationStatus.Completed, "completed");

    private static ProductionBonusActivationResult Failed()
        => new(ProductionBonusActivationStatus.Failed, "failed");

    private static ProductionBonusActivationResult Cooldown(DateTimeOffset retryAtUtc)
        => new(ProductionBonusActivationStatus.CooldownActive, "cooldown", RetryAtUtc: retryAtUtc);

    private sealed class FakeBrowser : IProductionBonusBrowser, IProductionBonusActivationBatch
    {
        private readonly Queue<ProductionBonusObservation> _observations;
        private readonly Queue<ProductionBonusActivationResult> _activations;

        internal FakeBrowser(
            IEnumerable<ProductionBonusObservation> observations,
            IEnumerable<ProductionBonusActivationResult> activations)
        {
            _observations = new Queue<ProductionBonusObservation>(observations);
            _activations = new Queue<ProductionBonusActivationResult>(activations);
        }

        internal List<string> ActivatedResources { get; } = [];

        public Task<ProductionBonusObservation> InspectAsync(
            bool afterActivationAttempt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_observations.Dequeue());
        }

        public IProductionBonusActivationBatch BeginActivationBatch() => this;

        public Task<ProductionBonusActivationResult> ActivateAsync(
            string resource,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivatedResources.Add(resource);
            return Task.FromResult(_activations.Dequeue());
        }

        public Task ActivateInCurrentBrowserAsync(
            IReadOnlyList<string> resources,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RestoreMainPageAsync() => Task.CompletedTask;
    }
}
