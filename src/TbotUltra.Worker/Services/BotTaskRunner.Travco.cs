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
    public async Task OpenTravcoAndSearchAsync(
        BotOptions options,
        TravcoSearchRequest request,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (options.Headless)
        {
            throw new InvalidOperationException("Travco inactive search requires the visible browser session.");
        }

        var account = _accountProvider.LoadAccount();
        log("[travco] waiting for browser session.");
        if (!await _sessionGate.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken))
        {
            throw new InvalidOperationException(
                "Travco is waiting for another browser operation. Stop the bot or wait for the current task to finish, then try Analyze Travco again.");
        }

        log("[travco] browser session acquired.");
        try
        {
            var lease = await AcquireClientLeaseAsync(options, account, log, interactive: true, cancellationToken);
            try
            {
                if (_travcoPage is null || _travcoPage.IsClosed)
                {
                    if (_travcoPage is not null)
                    {
                        await CloseTravcoPageAsync(_travcoPage, log);
                        _travcoPage = null;
                    }

                    _travcoPage = await lease.Session.OpenIsolatedExternalPageAsync(cancellationToken);
                    // Travco can be slow to render; raise the default 15s context timeout for this tab
                    // so individual navigations don't trip the timeout on a sluggish load.
                    _travcoPage.SetDefaultTimeout(30000);
                    log("[travco] opened isolated browser tab.");
                }

                await TravcoInactiveSearch.RunSearchAsync(
                    _travcoPage,
                    new Uri(options.BaseUrl).Host,
                    request.X,
                    request.Y,
                    request.DaysInactive,
                    request.OrderBy,
                    resultsPerPage: 100,
                    log,
                    cancellationToken);
                await _travcoPage.BringToFrontAsync();
            }
            finally
            {
                await FinalizeLeaseAsync(lease, log);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<TravcoScrapeResult> ScrapeTravcoPageAsync(
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_travcoPage is null || _travcoPage.IsClosed)
            {
                throw new InvalidOperationException("Open and run a Travco inactive search first.");
            }

            return await TravcoInactiveSearch.ScrapePageAsync(_travcoPage, log, cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<TravcoScrapeResult> ScrapeAllTravcoPagesAsync(
        Action<string> log,
        IProgress<(int CurrentPage, int TotalPages)> progress,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_travcoPage is null || _travcoPage.IsClosed)
            {
                throw new InvalidOperationException("Open and run a Travco inactive search first.");
            }

            return await TravcoInactiveSearch.ScrapeAllPagesAsync(
                _travcoPage,
                log,
                progress,
                cancellationToken);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task CloseTravcoTabAsync(Action<string>? log = null)
    {
        if (_travcoPage is null)
        {
            log?.Invoke("[travco] no browser tab was open.");
            return;
        }

        await _sessionGate.WaitAsync();
        try
        {
            if (_travcoPage is null)
            {
                return;
            }

            try
            {
                await CloseTravcoPageAsync(_travcoPage, log);
                log?.Invoke("[travco] browser tab closed.");
            }
            finally
            {
                _travcoPage = null;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task CloseTravcoPageAsync(IPage page, Action<string>? log)
    {
        if (_sharedVisibleSession is not null)
        {
            await _sharedVisibleSession.CloseIsolatedExternalPageAsync(page);
            return;
        }

        try
        {
            await page.Context.CloseAsync();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[travco] could not close isolated browser context: {ex.Message}");
        }
    }

}
