using Microsoft.Playwright;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed class BrowserSession : IAsyncDisposable
{
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

    public async Task<IPage> OpenPageAsync(CancellationToken cancellationToken = default)
    {
        var authDirectory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(authDirectory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(authDirectory);

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
}
