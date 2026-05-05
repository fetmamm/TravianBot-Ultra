using Microsoft.Playwright;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed class BrowserSession : IAsyncDisposable
{
    private const string LocalPlaywrightBrowsersDirectoryName = "ms-playwright";
    private static readonly SemaphoreSlim WarmupGate = new(1, 1);
    private static bool _warmupCompleted;
    private readonly BotOptions _config;
    private readonly AccountOptions _account;
    private readonly bool? _headlessOverride;
    private readonly string _projectRoot;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public BrowserSession(
        BotOptions config,
        AccountOptions account,
        string projectRoot,
        bool? headlessOverride = null)
    {
        _config = config;
        _account = account;
        _projectRoot = projectRoot;
        _headlessOverride = headlessOverride;
    }

    public string StorageStatePath =>
        Path.Combine(_projectRoot, "playwright", ".auth", $"{_account.Name}.json");

    public string PlaywrightBrowsersPath =>
        Path.Combine(_projectRoot, LocalPlaywrightBrowsersDirectoryName);

    public static async Task<bool> WarmupAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        if (_warmupCompleted || !ChromiumAlreadyInstalled(projectRoot))
        {
            return false;
        }

        await WarmupGate.WaitAsync(cancellationToken);
        try
        {
            if (_warmupCompleted)
            {
                return false;
            }

            var playwrightBrowsersPath = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
            Directory.CreateDirectory(playwrightBrowsersPath);
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", playwrightBrowsersPath);

            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
            });

            await browser.CloseAsync();
            _warmupCompleted = true;
            return true;
        }
        finally
        {
            WarmupGate.Release();
        }
    }

    public async Task<IPage> OpenPageAsync(CancellationToken cancellationToken = default)
    {
        var authDirectory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(authDirectory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(authDirectory);
        Directory.CreateDirectory(PlaywrightBrowsersPath);

        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", PlaywrightBrowsersPath);

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _headlessOverride ?? _config.Headless,
        });

        var contextOptions = new BrowserNewContextOptions
        {
            BaseURL = _config.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        };

        if (File.Exists(StorageStatePath))
        {
            contextOptions.StorageStatePath = StorageStatePath;
        }

        _context = await _browser.NewContextAsync(contextOptions);
        _context.SetDefaultTimeout(_config.TimeoutMs);
        return await _context.NewPageAsync();
    }

    public async Task SaveStateAsync()
    {
        if (_context is null)
        {
            return;
        }

        await _context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = StorageStatePath,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.CloseAsync();
        }

        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
    }

    public static bool ChromiumAlreadyInstalled(string projectRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return false;
            }

            var playwrightRoot = Path.Combine(projectRoot, LocalPlaywrightBrowsersDirectoryName);
            if (!Directory.Exists(playwrightRoot))
            {
                return false;
            }

            var executables = Directory.GetFiles(playwrightRoot, "chrome.exe", SearchOption.AllDirectories);
            return executables.Any(path =>
                path.Contains("chromium-", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("chrome-win", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
