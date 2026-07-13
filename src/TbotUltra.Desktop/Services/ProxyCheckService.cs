using System.Diagnostics;
using Microsoft.Playwright;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Owns the temporary Playwright browser used for direct and proxied IP checks.
/// Progress is reported to the account editor without depending on WPF controls.
/// </summary>
internal static class ProxyCheckService
{
    internal static async Task<string> CheckIpAsync(
        string mode,
        string? proxyServer,
        Proxy? proxy,
        Action<string> status,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        status("Starting temporary browser...");
        cancellationToken.ThrowIfCancellationRequested();
        using var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        using var registration = cancellationToken.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (browser is not null)
                    {
                        await browser.CloseAsync();
                    }
                }
                catch
                {
                    // Browser may already be closing.
                }
            });
        });

        try
        {
            status(proxy is null ? "Launching browser..." : "Launching browser through proxy...");
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 20000,
            };
            if (proxy is not null)
            {
                launchOptions.Proxy = proxy;
            }

            browser = await playwright.Chromium.LaunchAsync(launchOptions);

            cancellationToken.ThrowIfCancellationRequested();
            status("Requesting public IP...");
            var page = await browser.NewPageAsync();
            await page.GotoAsync(
                "https://ipwho.is/",
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 20000 });
            cancellationToken.ThrowIfCancellationRequested();

            status("Reading proxy details...");
            var raw = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 5000 });
            stopwatch.Stop();

            var info = ProxyCheckResultCodec.ParseLookupResponse(raw);
            var route = string.IsNullOrWhiteSpace(proxyServer)
                ? mode
                : $"{mode} ({ProxyParser.MaskForLog(proxyServer)})";
            return ProxyCheckResultCodec.BuildSuccess(info, route, $"{stopwatch.ElapsedMilliseconds} ms");
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync();
                }
                catch
                {
                    // Browser may already have been closed by cancellation.
                }
            }
        }
    }
}
