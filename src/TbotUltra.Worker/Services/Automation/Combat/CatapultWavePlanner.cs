using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal static class CatapultWavePlanner
{
    public const string FirstAttackLabel = "First attack";

    public static CatapultWavePlan BuildPlan(CatapultWaveRequest request)
    {
        ValidateRequest(request);

        var firstTroops = NormalizeTroopSet(request.FirstAttackTroops);
        var waveTroops = request.WaveCount > 0
            ? NormalizeTroopSet(request.WaveTroops)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var attacks = new List<CatapultWaveAttackPlan>
        {
            new(FirstAttackLabel, firstTroops),
        };

        for (var i = 1; i <= request.WaveCount; i++)
        {
            attacks.Add(new CatapultWaveAttackPlan($"Wave {i}", waveTroops));
        }

        return new CatapultWavePlan(firstTroops, waveTroops, attacks);
    }

    public static void ValidateAvailability(CatapultWavePlan plan, int waveCount, IReadOnlyDictionary<string, long> availableTroops)
    {
        foreach (var troopName in plan.FirstAttackTroops.Keys.Concat(plan.WaveTroops.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var firstCount = plan.FirstAttackTroops.TryGetValue(troopName, out var first) ? first : 0;
            var waveTroopCount = plan.WaveTroops.TryGetValue(troopName, out var wave) ? wave : 0;
            var required = (long)firstCount + (long)waveTroopCount * Math.Max(0, waveCount);
            var available = availableTroops.TryGetValue(troopName, out var found) ? found : 0;
            if (required > available)
            {
                throw new InvalidOperationException($"ALARM: Not enough {troopName}. Available: {FormatLargeCount(available)}, required: {FormatLargeCount(required)}.");
            }
        }
    }

    private static void ValidateRequest(CatapultWaveRequest request)
    {
        if (request.X is < -400 or > 400 || request.Y is < -400 or > 400)
        {
            throw new InvalidOperationException("Target coordinates must be between -400 and 400.");
        }

        if (request.WaveCount < 0)
        {
            throw new InvalidOperationException("Wave count cannot be less than 0.");
        }

        if (request.WaveCount > CatapultWaveLimits.MaxWaveCount)
        {
            throw new InvalidOperationException($"Wave count cannot be greater than {CatapultWaveLimits.MaxWaveCount}.");
        }

        var firstTroops = NormalizeTroopSet(request.FirstAttackTroops);
        var waveTroops = NormalizeTroopSet(request.WaveTroops);
        if (firstTroops.Count == 0)
        {
            throw new InvalidOperationException("First attack must include at least one troop.");
        }

        if (request.WaveCount > 0 && waveTroops.Count == 0)
        {
            throw new InvalidOperationException("Wave attack must include at least one troop.");
        }

        foreach (var troopName in firstTroops.Keys.Concat(waveTroops.Keys))
        {
            if (TroopCatalog.ResolveTroopIndex(troopName) is null)
            {
                throw new InvalidOperationException($"Could not resolve troop slot for '{troopName}'.");
            }
        }
    }

    private static IReadOnlyDictionary<string, int> NormalizeTroopSet(IReadOnlyDictionary<string, int>? troops)
    {
        var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in troops ?? new Dictionary<string, int>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0)
            {
                continue;
            }

            normalized[pair.Key.Trim()] = Math.Max(1, pair.Value);
        }

        return normalized;
    }

    private static string FormatLargeCount(long value)
    {
        return Math.Max(0, value).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed record CatapultWavePlan(
    IReadOnlyDictionary<string, int> FirstAttackTroops,
    IReadOnlyDictionary<string, int> WaveTroops,
    IReadOnlyList<CatapultWaveAttackPlan> Attacks);

internal sealed record CatapultWaveAttackPlan(string Label, IReadOnlyDictionary<string, int> Troops);
