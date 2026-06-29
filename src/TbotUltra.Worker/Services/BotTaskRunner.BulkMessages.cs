using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class BotTaskRunner
{
    private static readonly HttpClient BulkMessageHttpClient = CreateBulkMessageHttpClient();

    public async Task<BulkMessageAnalyzeResult> AnalyzeBulkMessagePlayersAsync(
        BotOptions options,
        BulkMessageAnalyzeRequest request,
        Action<string> log,
        IProgress<BulkMessageProgress>? progress = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        EnsureBulkMessagesSupported(options);
        var account = _accountProvider.LoadAccount(accountName);
        progress?.Report(new BulkMessageProgress("Analyzing players", 0, 0));

        var mapSql = await DownloadMapSqlAsync(options, log, cancellationToken);
        var parsed = MapSqlPlayerParser.Parse(mapSql);
        var sentPlayers = _bulkMessageSentCacheStore.LoadSentPlayerNames(account.Name, options.BaseUrl);
        var result = MapSqlPlayerParser.Analyze(
            parsed,
            sentPlayers,
            request.ExcludedPlayers,
            request.ExcludedAlliances,
            request.SortOrder);

        log($"[bulk-messages] analyzed map.sql: players={result.PlayersAnalyzed}, eligible={result.EligiblePlayers}, cachedSent={result.SentCachedCount}.");
        progress?.Report(new BulkMessageProgress("Analysis complete", 0, result.EligiblePlayers));
        return result;
    }

    public async Task<BulkMessageSendResult> SendBulkMessagesAsync(
        BotOptions options,
        BulkMessageRequest request,
        Action<string> log,
        IProgress<BulkMessageProgress>? progress = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        EnsureBulkMessagesSupported(options);
        ValidateBulkMessageRequest(request);

        var account = _accountProvider.LoadAccount(accountName);
        progress?.Report(new BulkMessageProgress("Analyzing players", 0, request.MaxRecipients));

        var mapSql = await DownloadMapSqlAsync(options, log, cancellationToken);
        var parsed = MapSqlPlayerParser.Parse(mapSql);
        var sentPlayers = _bulkMessageSentCacheStore.LoadSentPlayerNames(account.Name, options.BaseUrl);
        var analysis = MapSqlPlayerParser.Analyze(
            parsed,
            sentPlayers,
            request.ExcludedPlayers,
            request.ExcludedAlliances,
            request.SortOrder);

        var targets = analysis.Players
            .Take(Math.Max(0, request.MaxRecipients))
            .Where(player => !MapSqlPlayerParser.IsProtectedPlayerName(player.Name))
            .ToList();

        if (targets.Count == 0)
        {
            log("[bulk-messages] no eligible players to message.");
            progress?.Report(new BulkMessageProgress("No eligible players", 0, 0));
            return new BulkMessageSendResult(
                analysis.PlayersAnalyzed,
                TargetCount: 0,
                SentCount: 0,
                analysis.SentCachedCount);
        }

        var batches = targets
            .Select(player => player.Name)
            .Chunk(25)
            .Select(chunk => chunk.ToList())
            .ToList();

        var sentCount = 0;
        progress?.Report(new BulkMessageProgress("Sending messages", sentCount, targets.Count, 0, batches.Count));
        await ExecuteWithClientAsync(
            options,
            log,
            accountName,
            interactive: true,
            cancellationToken,
            async client =>
            {
                await client.LoginAsync(cancellationToken);
                for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = batches[batchIndex];
                    var batchNumber = batchIndex + 1;
                    var currentPlayers = string.Join("; ", batch);
                    log($"[bulk-messages] sending batch {batchNumber}/{batches.Count} ({batch.Count} players).");
                    progress?.Report(new BulkMessageProgress(
                        "Sending messages",
                        sentCount,
                        targets.Count,
                        batchNumber,
                        batches.Count,
                        currentPlayers));

                    var sentBatch = await client.SendBulkMessageBatchAsync(batch, request.Subject, request.Message, cancellationToken);
                    _bulkMessageSentCacheStore.AddSentPlayers(account.Name, options.BaseUrl, sentBatch, DateTimeOffset.UtcNow);
                    sentCount += sentBatch.Count;
                    progress?.Report(new BulkMessageProgress(
                        "Sending messages",
                        sentCount,
                        targets.Count,
                        batchNumber,
                        batches.Count,
                        currentPlayers));
                }
            });

        log($"[bulk-messages] sent {sentCount}/{targets.Count} player message(s).");
        progress?.Report(new BulkMessageProgress("Complete", sentCount, targets.Count, batches.Count, batches.Count));
        return new BulkMessageSendResult(
            analysis.PlayersAnalyzed,
            targets.Count,
            sentCount,
            analysis.SentCachedCount);
    }

    public void ClearBulkMessageSentCache(
        BotOptions options,
        Action<string> log,
        string? accountName = null)
    {
        EnsureBulkMessagesSupported(options);
        var account = _accountProvider.LoadAccount(accountName);
        _bulkMessageSentCacheStore.Clear(account.Name, options.BaseUrl);
        log($"[bulk-messages] sent-player cache cleared for account '{account.Name}' server '{options.BaseUrl.TrimEnd('/')}'.");
    }

    private static async Task<string> DownloadMapSqlAsync(
        BotOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        var mapSqlUri = new Uri(baseUri, "map.sql");
        log($"[bulk-messages] downloading {mapSqlUri}.");
        using var response = await BulkMessageHttpClient.GetAsync(mapSqlUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("map.sql was empty.");
        }

        return content;
    }

    private static HttpClient CreateBulkMessageHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TbotUltra/1.0");
        return client;
    }

    private static void EnsureBulkMessagesSupported(BotOptions options)
    {
        if (options.IsPrivateServer)
        {
            throw new NotSupportedException("Bulk messages currently supports Official servers only.");
        }
    }

    private static void ValidateBulkMessageRequest(BulkMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            throw new ArgumentException("Subject is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Message is required.", nameof(request));
        }

        if (request.MaxRecipients <= 0)
        {
            throw new ArgumentException("Recipient count must be greater than 0.", nameof(request));
        }
    }
}
