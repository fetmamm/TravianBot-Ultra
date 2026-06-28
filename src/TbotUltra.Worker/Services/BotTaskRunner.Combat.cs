using TbotUltra.Core.Configuration;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;
using TbotUltra.Worker.Services.Automation;
using Microsoft.Playwright;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(
        BotOptions options,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        return await ReadAvailableTroopsForCatapultWavesAsync(
            options,
            log,
            forceRefresh: false,
            accountName,
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> ReadAvailableTroopsForCatapultWavesAsync(
        BotOptions options,
        Action<string> log,
        bool forceRefresh,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, long> result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.ReadAvailableTroopsForCatapultWavesAsync(forceRefresh, cancellationToken);
            });

        return result;
    }

    public async Task<CatapultWaveSetupInfo> ReadCatapultWaveSetupInfoAsync(
        BotOptions options,
        Action<string> log,
        bool forceRefresh,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        CatapultWaveSetupInfo? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.ReadCatapultWaveSetupInfoAsync(forceRefresh, cancellationToken);
            });

        return result ?? new CatapultWaveSetupInfo(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), null);
    }

    public async Task<CatapultWaveRunResult> StartCatapultWavesAsync(
        BotOptions options,
        CatapultWaveRequest request,
        Action<string> log,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        CatapultWaveRunResult? result = null;
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: false,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                result = await client.StartCatapultWavesAsync(request, cancellationToken);
            });

        return result ?? throw new InvalidOperationException("Could not start catapult waves.");
    }

}