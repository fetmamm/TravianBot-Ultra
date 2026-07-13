using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

public static class TravcoInactiveSearch
{
    public const string InactiveSearchUrl = "https://travcotools.com/en/inactive-search/";
    public const string SiteOrigin = "https://travcotools.com";

    public static async Task RunSearchAsync(
        IPage page,
        string serverHost,
        int x,
        int y,
        int daysInactive,
        string orderBy,
        int resultsPerPage,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var normalizedHost = NormalizeHost(serverHost);
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            throw new InvalidOperationException("Travco search requires a valid official Travian server host.");
        }

        log?.Invoke("[travco] opening inactive search.");
        await page.GotoAsync(InactiveSearchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
            .WaitAsync(cancellationToken);

        var pageSize = Math.Clamp(resultsPerPage, 10, 100).ToString();
        var pageSizeSelect = page.Locator("#id_page_size");
        var selectedPageSize = await pageSizeSelect.InputValueAsync().WaitAsync(cancellationToken);
        if (!string.Equals(selectedPageSize, pageSize, StringComparison.Ordinal))
        {
            log?.Invoke($"[travco] setting results per page to {pageSize}.");
            await pageSizeSelect.SelectOptionAsync(pageSize).WaitAsync(cancellationToken);
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded).WaitAsync(cancellationToken);
            await page.WaitForFunctionAsync(
                "value => document.readyState !== 'loading' && document.querySelector('#id_page_size')?.value === value",
                pageSize,
                new PageWaitForFunctionOptions { Timeout = 15000 }).WaitAsync(cancellationToken);
        }

        var serverValue = await page.EvaluateAsync<string?>(
            """
            host => {
              const normalized = String(host || '').trim().toLowerCase();
              const option = Array.from(document.querySelectorAll('#id_travian_server option'))
                .find(item => (item.textContent || '').trim().toLowerCase() === normalized);
              return option?.value || null;
            }
            """,
            normalizedHost).WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(serverValue))
        {
            throw new InvalidOperationException(
                $"Server '{normalizedHost}' is not available on Travco. Travco inactive search supports official servers only.");
        }

        log?.Invoke($"[travco] matched server '{normalizedHost}'.");
        await page.Locator("#id_travian_server").SelectOptionAsync(serverValue).WaitAsync(cancellationToken);
        await WaitForValueAsync(page, "#id_travian_server", serverValue, cancellationToken);
        await page.Locator("#id_x").FillAsync(x.ToString()).WaitAsync(cancellationToken);
        await WaitForValueAsync(page, "#id_x", x.ToString(), cancellationToken);
        await page.Locator("#id_y").FillAsync(y.ToString()).WaitAsync(cancellationToken);
        await WaitForValueAsync(page, "#id_y", y.ToString(), cancellationToken);
        var days = Math.Clamp(daysInactive, 1, 7).ToString();
        await page.Locator("#id_days").SelectOptionAsync(days).WaitAsync(cancellationToken);
        await WaitForValueAsync(page, "#id_days", days, cancellationToken);
        var normalizedOrderBy = NormalizeOrderBy(orderBy);
        await page.Locator("#id_order_by").SelectOptionAsync(normalizedOrderBy).WaitAsync(cancellationToken);
        await WaitForValueAsync(page, "#id_order_by", normalizedOrderBy, cancellationToken);

        var formState = await page.EvaluateAsync<TravcoFormState>(
            """
            () => ({
              server: document.querySelector('#id_travian_server')?.value || '',
              x: document.querySelector('#id_x')?.value || '',
              y: document.querySelector('#id_y')?.value || '',
              days: document.querySelector('#id_days')?.value || '',
              orderBy: document.querySelector('#id_order_by')?.value || '',
              pageSize: document.querySelector('#id_page_size')?.value || ''
            })
            """).WaitAsync(cancellationToken);
        if (!string.Equals(formState.Server, serverValue, StringComparison.Ordinal)
            || !string.Equals(formState.X, x.ToString(), StringComparison.Ordinal)
            || !string.Equals(formState.Y, y.ToString(), StringComparison.Ordinal)
            || !string.Equals(formState.Days, days, StringComparison.Ordinal)
            || !string.Equals(formState.OrderBy, normalizedOrderBy, StringComparison.Ordinal)
            || !string.Equals(formState.PageSize, pageSize, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Travco search fields could not be filled correctly.");
        }

        log?.Invoke($"[travco] fields ready: server={normalizedHost}, coordinates=({x}|{y}), days={days}, order={normalizedOrderBy}, pageSize={pageSize}.");
        await page.Locator("button.btn.btn-light.primary[type='submit']")
            .ClickAsync()
            .WaitAsync(cancellationToken);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded).WaitAsync(cancellationToken);
        await page.WaitForFunctionAsync(
            "value => new URLSearchParams(window.location.search).get('travian_server') === value",
            serverValue,
            new PageWaitForFunctionOptions { Timeout = 20000 }).WaitAsync(cancellationToken);
        await page.Locator("main table").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 20000,
        }).WaitAsync(cancellationToken);
        await WaitForResultsSettledAsync(page, cancellationToken);

        log?.Invoke("[travco] inactive search results loaded.");
    }

    public static async Task<TravcoScrapeResult> ScrapePageAsync(
        IPage page,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.IsClosed)
        {
            throw new InvalidOperationException("The Travco browser tab is closed.");
        }

        log?.Invoke("[travco] reading result rows from the current page.");
        var payload = await page.EvaluateAsync<JsonElement>(
            """
            () => {
              const table = document.querySelector('main table');
              if (!table) {
                throw new Error('Travco result table was not found.');
              }

              const headers = ['Checkbox', 'Distance', 'Account', 'Village', 'Population'];
              const rows = Array.from(table.querySelectorAll('tbody tr')).map(row => {
                const cells = Array.from(row.querySelectorAll('td'));
                const distance = (cells[1]?.textContent || '').trim();
                const account = (cells[2]?.querySelector('.detail-button')?.textContent || '').trim();
                const villageLink = cells[3]?.querySelector('a.js-travian_village_url, a[href*="karte.php"]') || null;
                const village = (villageLink?.getAttribute('data-original-title')
                  || villageLink?.getAttribute('title')
                  || villageLink?.childNodes?.[0]?.textContent
                  || '').trim();
                const coordinates = (villageLink?.querySelector('.text-muted.small')?.textContent || '').trim();
                const populationElement = cells[3]?.querySelector(
                  '[data-original-title="Population"], [title="Population"]');
                const latestPopulation = (populationElement?.textContent || '').trim();
                return {
                  cells: ['', distance, account, village, latestPopulation],
                  villageHref: coordinates || villageLink?.href || null
                };
              });

              const activePage = document.querySelector('main a.btn.active[href*="page="]');
              const pageNumber = Number.parseInt((activePage?.textContent || '1').trim(), 10);
              const pageCandidates = Array.from(document.querySelectorAll('main a[href*="page="]'))
                .flatMap(node => {
                  const textValue = Number.parseInt((node.textContent || '').trim(), 10);
                  const href = node.getAttribute('href') || '';
                  const hrefMatch = href.match(/[?&]page=(\d+)/i);
                  return [
                    Number.isFinite(textValue) ? textValue : 0,
                    hrefMatch ? Number.parseInt(hrefMatch[1], 10) : 0
                  ];
                });
              const totalPages = Math.max(1, ...pageCandidates.filter(Number.isFinite));
              return {
                pageNumber: Number.isFinite(pageNumber) ? pageNumber : 1,
                totalPages,
                headers,
                rows
              };
            }
            """).WaitAsync(cancellationToken);

        var rawPage = ParseRawPagePayload(payload);
        var result = TravcoInactiveSearchParser.Parse(rawPage);
        log?.Invoke($"[travco] scraped page {result.PageNumber}/{result.TotalPages}: {result.Rows.Count} row(s).");
        return result;
    }

    public static async Task<TravcoScrapeResult> ScrapeAllPagesAsync(
        IPage page,
        Action<string>? log,
        IProgress<(int CurrentPage, int TotalPages)>? progress,
        CancellationToken cancellationToken)
    {
        var totalPages = await ResolveTotalPagesAsync(page, log, cancellationToken);
        var rows = new List<TravcoRow>();
        try
        {
            // No anti-bot pacing between pages: this is travcotools.com, not Travian. NavigateToPageAsync
            // already waits for the new page to load and settle, which is the only delay needed here.
            for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((pageNumber, totalPages));
                var result = await ScrapePageWithRetryAsync(page, pageNumber, log, cancellationToken);
                rows.AddRange(result.Rows);
            }
        }
        finally
        {
            try
            {
                await ReturnToFirstPageAsync(page, cancellationToken);
                log?.Invoke("[travco] returned to result page 1.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[travco] could not return to result page 1: {ex.Message}");
            }
        }

        log?.Invoke($"[travco] scraped all {totalPages} page(s): {rows.Count} row(s).");
        return new TravcoScrapeResult(1, totalPages, rows);
    }


    // Navigates to and scrapes a single page, retrying up to 3 times when the page is slow to load.
    // A reload between attempts lets a stalled request recover before the next try. Only after all
    // attempts fail does the error bubble up and abort the whole "save all pages" run.
    private const int PageScrapeMaxAttempts = 3;

    private static async Task<TravcoScrapeResult> ScrapePageWithRetryAsync(
        IPage page,
        int pageNumber,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= PageScrapeMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await NavigateToPageAsync(page, pageNumber, cancellationToken);
                return await ScrapePageAsync(page, log, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < PageScrapeMaxAttempts)
            {
                var backoff = TimeSpan.FromSeconds(attempt);
                log?.Invoke(
                    $"[travco] page {pageNumber} attempt {attempt}/{PageScrapeMaxAttempts} failed: {ex.Message}. " +
                    $"Reloading and retrying in {backoff.TotalSeconds:0}s.");
                try
                {
                    await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded })
                        .WaitAsync(cancellationToken);
                }
                catch (Exception reloadEx)
                {
                    log?.Invoke($"[travco] page {pageNumber} reload failed: {reloadEx.Message}.");
                }

                await Task.Delay(backoff, cancellationToken);
            }
        }

        // Unreachable: the final attempt either returns or throws inside the loop.
        throw new InvalidOperationException($"Travco page {pageNumber} could not be scraped.");
    }

    private static async Task<int> ResolveTotalPagesAsync(
        IPage page,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        // Make sure the results table (and thus the pagination links) is rendered before reading the
        // page count; otherwise a slow load returns 1 and only page 1 would be saved silently.
        await WaitForResultsSettledAsync(page, cancellationToken);
        var totalPages = await page.EvaluateAsync<int>(
            """
            () => Math.max(1, ...Array.from(document.querySelectorAll('main a[href*="page="]'))
              .map(link => Number.parseInt(new URL(link.href, location.href).searchParams.get('page') || '0', 10))
              .filter(Number.isFinite))
            """).WaitAsync(cancellationToken);
        log?.Invoke($"[travco] detected {totalPages} result page(s).");
        return Math.Max(1, totalPages);
    }

    private static async Task NavigateToPageAsync(IPage page, int pageNumber, CancellationToken cancellationToken)
    {
        var currentPage = await page.EvaluateAsync<int>(
            "() => Number.parseInt((document.querySelector('main a.btn.active[href*=\"page=\"]')?.textContent || '1').trim(), 10)")
            .WaitAsync(cancellationToken);
        if (currentPage == pageNumber)
        {
            return;
        }

        var link = page.Locator(
            $"xpath=//main//a[contains(@href,'page=') and normalize-space(.)='{pageNumber}']").First;
        await link.ClickAsync().WaitAsync(cancellationToken);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded).WaitAsync(cancellationToken);
        await page.WaitForFunctionAsync(
            "pageNumber => Number.parseInt((document.querySelector('main a.btn.active[href*=\"page=\"]')?.textContent || '0').trim(), 10) === pageNumber",
            pageNumber,
            new PageWaitForFunctionOptions { Timeout = 20000 }).WaitAsync(cancellationToken);
        await WaitForResultsSettledAsync(page, cancellationToken);
    }

    private static async Task ReturnToFirstPageAsync(IPage page, CancellationToken cancellationToken)
    {
        var firstPageLink = page.Locator(
            "main a[data-original-title='first page'][href*='page=1'], main a[title='first page'][href*='page=1']")
            .First;
        if (await firstPageLink.CountAsync() > 0)
        {
            await firstPageLink.ClickAsync().WaitAsync(cancellationToken);
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded).WaitAsync(cancellationToken);
            await page.WaitForFunctionAsync(
                "() => Number.parseInt((document.querySelector('main a.btn.active[href*=\"page=\"]')?.textContent || '0').trim(), 10) === 1",
                null,
                new PageWaitForFunctionOptions { Timeout = 20000 }).WaitAsync(cancellationToken);
            await WaitForResultsSettledAsync(page, cancellationToken);
            return;
        }

        await NavigateToPageAsync(page, 1, cancellationToken);
    }

    private static Task WaitForValueAsync(
        IPage page,
        string selector,
        string expectedValue,
        CancellationToken cancellationToken)
    {
        return page.WaitForFunctionAsync(
                "args => document.querySelector(args.selector)?.value === args.expectedValue",
                new { selector, expectedValue },
                new PageWaitForFunctionOptions { Timeout = 5000 })
            .WaitAsync(cancellationToken);
    }

    private static async Task WaitForResultsSettledAsync(IPage page, CancellationToken cancellationToken)
    {
        await page.WaitForFunctionAsync(
            "() => document.readyState === 'complete' && document.querySelector('main table tbody') !== null",
            null,
            new PageWaitForFunctionOptions { Timeout = 20000 }).WaitAsync(cancellationToken);
        await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
    }

    private static string NormalizeOrderBy(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "population" or "-population" => "-population",
            "tribe" or "tid" => "tid",
            _ => "distance",
        };
    }

    private static TravcoRawPage ParseRawPagePayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Travco result data has unexpected type '{payload.ValueKind}'.");
        }

        var pageNumber = payload.TryGetProperty("pageNumber", out var pageElement)
            && pageElement.TryGetInt32(out var parsedPage)
                ? parsedPage
                : 1;
        var totalPages = payload.TryGetProperty("totalPages", out var totalElement)
            && totalElement.TryGetInt32(out var parsedTotal)
                ? parsedTotal
                : 1;
        var headers = payload.TryGetProperty("headers", out var headersElement)
            && headersElement.ValueKind == JsonValueKind.Array
                ? headersElement.EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToList()
                : [];
        var rows = new List<TravcoRawRow>();
        if (payload.TryGetProperty("rows", out var rowsElement)
            && rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var rowElement in rowsElement.EnumerateArray())
            {
                if (rowElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var cells = rowElement.TryGetProperty("cells", out var cellsElement)
                    && cellsElement.ValueKind == JsonValueKind.Array
                        ? cellsElement.EnumerateArray()
                            .Select(item => item.GetString() ?? string.Empty)
                            .ToList()
                        : [];
                var villageHref = rowElement.TryGetProperty("villageHref", out var hrefElement)
                    && hrefElement.ValueKind == JsonValueKind.String
                        ? hrefElement.GetString()
                        : null;
                rows.Add(new TravcoRawRow(cells, villageHref));
            }
        }

        return new TravcoRawPage(pageNumber, totalPages, headers, rows);
    }

    private static string NormalizeHost(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant();
        }

        return trimmed.Split('/')[0].ToLowerInvariant();
    }

    private sealed class TravcoFormState
    {
        public string Server { get; set; } = string.Empty;
        public string X { get; set; } = string.Empty;
        public string Y { get; set; } = string.Empty;
        public string Days { get; set; } = string.Empty;
        public string OrderBy { get; set; } = string.Empty;
        public string PageSize { get; set; } = string.Empty;
    }

}
