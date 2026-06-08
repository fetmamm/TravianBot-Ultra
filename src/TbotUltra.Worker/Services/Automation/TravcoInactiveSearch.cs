using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

public static class TravcoInactiveSearch
{
    public const string InactiveSearchUrl = "https://travcotools.com/en/inactive-search/";

    public static async Task RunSearchAsync(
        IPage page,
        string serverHost,
        int x,
        int y,
        int daysInactive,
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
        await page.Locator("#id_x").FillAsync(x.ToString()).WaitAsync(cancellationToken);
        await page.Locator("#id_y").FillAsync(y.ToString()).WaitAsync(cancellationToken);
        var days = Math.Clamp(daysInactive, 1, 7).ToString();
        await page.Locator("#id_days").SelectOptionAsync(days).WaitAsync(cancellationToken);
        await page.Locator("#id_order_by").SelectOptionAsync("distance").WaitAsync(cancellationToken);

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
            || !string.Equals(formState.OrderBy, "distance", StringComparison.Ordinal)
            || !string.Equals(formState.PageSize, pageSize, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Travco search fields could not be filled correctly.");
        }

        log?.Invoke($"[travco] fields ready: server={normalizedHost}, coordinates=({x}|{y}), days={days}, order=distance, pageSize={pageSize}.");
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

        var rawPage = await page.EvaluateAsync<TravcoRawPage>(
            """
            () => {
              const table = document.querySelector('main table');
              if (!table) {
                throw new Error('Travco result table was not found.');
              }

              const headers = Array.from(table.querySelectorAll('thead th'))
                .map(cell => (cell.textContent || '').trim());
              const rows = Array.from(table.querySelectorAll('tbody tr')).map(row => {
                const cells = Array.from(row.querySelectorAll('td'))
                  .map(cell => (cell.textContent || '').trim());
                const villageCell = row.querySelectorAll('td')[3] || row.querySelectorAll('td')[2] || null;
                const villageLink = villageCell?.querySelector('a[href]') || null;
                return {
                  cells,
                  villageHref: villageLink?.href || null
                };
              });

              const activePage = document.querySelector('.pagination .page-item.active .page-link, .pagination .active');
              const pageNumber = Number.parseInt((activePage?.textContent || '1').trim(), 10);
              const pageCandidates = Array.from(document.querySelectorAll('.pagination a[href], .pagination .page-link'))
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

        var result = TravcoInactiveSearchParser.Parse(rawPage);
        log?.Invoke($"[travco] scraped page {result.PageNumber}/{result.TotalPages}: {result.Rows.Count} row(s).");
        return result;
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
