using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Combat operations exposed by <see cref="TravianClient"/>: catapult-wave reads
/// and dispatch, and reinforcement sends. Seam introduced ahead of extracting a
/// dedicated combat collaborator (#7); <see cref="TravianClient"/> implements it
/// directly for now, so behavior is unchanged.
/// </summary>
public interface ICombatClient
{
    Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(bool forceRefresh, CancellationToken cancellationToken = default);

    Task<CatapultWaveSetupInfo> ReadCatapultWaveSetupInfoAsync(bool forceRefresh, CancellationToken cancellationToken = default);

    Task<CatapultWaveRunResult> StartCatapultWavesAsync(
        CatapultWaveRequest request,
        CancellationToken cancellationToken = default);

    Task<string> SendReinforcementsBetweenOwnVillagesAsync(CancellationToken cancellationToken = default);

    Task<string> TestSendReinforcementsBetweenOwnVillagesAsync(CancellationToken cancellationToken = default);
}
