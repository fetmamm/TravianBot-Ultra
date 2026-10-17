using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroOintmentInventoryDomTests
{
    [Fact]
    public async Task InventoryRead_DoesNotTreatSlotFiveAsOintment()
    {
        var script = ExtractInventoryReadScript();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = ResolveChromiumExecutable(),
        });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(
            """
            <style>
              .heroItem { display: block; width: 50px; height: 50px; }
              .item { display: block; width: 24px; height: 24px; }
            </style>
            <div class="heroItems">
              <div class="heroItem inventory" data-refkey="inventory_5">
                <div class="item item114"></div><div class="count">7</div>
              </div>
              <div class="heroItem inventory" data-refkey="inventory_17">
                <div class="item item109"></div><div class="count">3</div>
              </div>
            </div>
            """);

        var rawJson = await page.EvaluateAsync<string>(script);
        using var result = JsonDocument.Parse(rawJson);

        Assert.False(result.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task InventoryRead_FindsOintmentAtAnyPosition()
    {
        var script = ExtractInventoryReadScript();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = ResolveChromiumExecutable(),
        });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(
            """
            <style>
              .heroItem { display: block; width: 50px; height: 50px; }
              .item { display: block; width: 24px; height: 24px; }
            </style>
            <div class="heroItems">
              <div class="heroItem inventory" data-id="other" data-refkey="inventory_5">
                <div class="item item114"></div><div class="count">7</div>
              </div>
              <div class="heroItem inventory" data-id="ointment" data-refkey="inventory_17">
                <div class="item item106"></div><div class="count">23</div>
              </div>
            </div>
            """);

        var rawJson = await page.EvaluateAsync<string>(script);
        using var result = JsonDocument.Parse(rawJson);

        Assert.True(result.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal(23, result.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("ointment", result.RootElement.GetProperty("itemId").GetString());
        Assert.Equal("inventory_17", result.RootElement.GetProperty("itemRefKey").GetString());
    }

    private static string ExtractInventoryReadScript()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectRootLocator.FindProjectRoot(),
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Hero",
            "TravianClient.Hero.Inventory.cs"));
        var start = source.IndexOf("private async Task<HeroOintmentInventoryInfo> ReadHeroOintmentInventoryInfoAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<bool> ClickHeroOintmentItemAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var match = Regex.Match(
            source[start..end],
            "EvaluateAsync<string>\\(\\s*\"\"\"(?<script>[\\s\\S]*?)\"\"\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success);
        return match.Groups["script"].Value;
    }

    private static string ResolveChromiumExecutable()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var configuredRoot = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        var browserRoots = new[]
        {
            configuredRoot,
            Path.Combine(projectRoot, "temp_build_out", "playwright-browsers"),
            Path.Combine(projectRoot, "ms-playwright"),
        };

        foreach (var browserRoot in browserRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!Directory.Exists(browserRoot))
            {
                continue;
            }

            var executable = Directory.EnumerateFiles(browserRoot!, "chrome.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains("chromium-", StringComparison.OrdinalIgnoreCase));
            if (executable is not null)
            {
                return executable;
            }
        }

        throw new Xunit.Sdk.XunitException(
            "Test Chromium is missing. Run scripts/Install-TestChromium.ps1 before the Worker tests.");
    }
}
