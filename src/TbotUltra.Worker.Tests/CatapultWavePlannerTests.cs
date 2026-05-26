using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class CatapultWavePlannerTests
{
    [Fact]
    public void BuildPlan_WaveCountZeroCreatesOnlyFirstAttackAndDoesNotRequireWaveTroops()
    {
        var request = Request(
            waveCount: 0,
            firstTroops: new Dictionary<string, int> { ["Clubswinger"] = 100 },
            waveTroops: new Dictionary<string, int>());

        var plan = CatapultWavePlanner.BuildPlan(request);

        var attack = Assert.Single(plan.Attacks);
        Assert.Equal(CatapultWavePlanner.FirstAttackLabel, attack.Label);
        Assert.Empty(plan.WaveTroops);
    }

    [Fact]
    public void BuildPlan_WaveCountOneCreatesFirstAttackThenWaveOne()
    {
        var request = Request(
            waveCount: 1,
            firstTroops: new Dictionary<string, int> { ["Clubswinger"] = 100 },
            waveTroops: new Dictionary<string, int> { ["Catapult"] = 1 });

        var plan = CatapultWavePlanner.BuildPlan(request);

        Assert.Collection(
            plan.Attacks,
            first => Assert.Equal(CatapultWavePlanner.FirstAttackLabel, first.Label),
            wave => Assert.Equal("Wave 1", wave.Label));
    }

    [Fact]
    public void BuildPlan_WaveCountFiftyIsAllowed()
    {
        var request = Request(
            waveCount: CatapultWaveLimits.MaxWaveCount,
            firstTroops: new Dictionary<string, int> { ["Clubswinger"] = 100 },
            waveTroops: new Dictionary<string, int> { ["Catapult"] = 1 });

        var plan = CatapultWavePlanner.BuildPlan(request);

        Assert.Equal(CatapultWaveLimits.MaxWaveCount + 1, plan.Attacks.Count);
    }

    [Fact]
    public void BuildPlan_WaveCountAboveMaximumThrows()
    {
        var request = Request(
            waveCount: CatapultWaveLimits.MaxWaveCount + 1,
            firstTroops: new Dictionary<string, int> { ["Clubswinger"] = 100 },
            waveTroops: new Dictionary<string, int> { ["Catapult"] = 1 });

        var exception = Assert.Throws<InvalidOperationException>(() => CatapultWavePlanner.BuildPlan(request));

        Assert.Contains(CatapultWaveLimits.MaxWaveCount.ToString(), exception.Message);
    }

    [Fact]
    public void BuildPlan_WaveCountAboveZeroRequiresWaveTroops()
    {
        var request = Request(
            waveCount: 1,
            firstTroops: new Dictionary<string, int> { ["Clubswinger"] = 100 },
            waveTroops: new Dictionary<string, int>());

        var exception = Assert.Throws<InvalidOperationException>(() => CatapultWavePlanner.BuildPlan(request));

        Assert.Contains("Wave attack", exception.Message);
    }

    [Fact]
    public void ValidateAvailability_UsesFirstPlusWaveTimesWaveCount()
    {
        var request = Request(
            waveCount: 3,
            firstTroops: new Dictionary<string, int> { ["Clubswinger"] = 10 },
            waveTroops: new Dictionary<string, int> { ["Clubswinger"] = 5 });
        var plan = CatapultWavePlanner.BuildPlan(request);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CatapultWavePlanner.ValidateAvailability(
                plan,
                request.WaveCount,
                new Dictionary<string, long> { ["Clubswinger"] = 24 }));

        Assert.Contains("required: 25", exception.Message);
        CatapultWavePlanner.ValidateAvailability(
            plan,
            request.WaveCount,
            new Dictionary<string, long> { ["Clubswinger"] = 25 });
    }

    private static CatapultWaveRequest Request(
        int waveCount,
        IReadOnlyDictionary<string, int> firstTroops,
        IReadOnlyDictionary<string, int> waveTroops)
    {
        return new CatapultWaveRequest(
            X: 1,
            Y: 2,
            WaveCount: waveCount,
            RaidAttack: false,
            FirstAttackTroops: firstTroops,
            WaveTroops: waveTroops,
            Target1: null,
            Target2: null);
    }
}
