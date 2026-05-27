using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Infrastructure;

public sealed class BrowserSession : IAsyncDisposable
{
    private const string LocalPlaywrightBrowsersDirectoryName = "ms-playwright";
    private static readonly SemaphoreSlim WarmupGate = new(1, 1);
    private static readonly SemaphoreSlim StorageStateGate = new(1, 1);
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
        AccountStoragePaths.BrowserStatePath(_projectRoot, _account.Name);

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

        MigrateLegacyStorageStateIfNeeded();

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

        var directory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(StorageStatePath)}.{Guid.NewGuid():N}.tmp");

        await StorageStateGate.WaitAsync();
        try
        {
            await _context.StorageStateAsync(new BrowserContextStorageStateOptions
            {
                Path = tempPath,
            });

            await ReplaceStorageStateWithRetryAsync(tempPath, StorageStatePath);
        }
        finally
        {
            StorageStateGate.Release();
            TryDeleteFile(tempPath);
        }

        DeleteLegacyStorageStateIfPresent();
    }

    private static async Task ReplaceStorageStateWithRetryAsync(string sourcePath, string targetPath)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (IOException ex) when (IsTransientStorageStateWriteError(ex) && attempt < 5)
            {
                lastError = ex;
                await Task.Delay(150 * attempt);
            }
            catch (UnauthorizedAccessException ex) when (attempt < 5)
            {
                lastError = ex;
                await Task.Delay(150 * attempt);
            }
        }

        throw new IOException($"Could not replace browser state after retries: {lastError?.Message}", lastError);
    }

    private static bool IsTransientStorageStateWriteError(IOException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("user-mapped section", StringComparison.OrdinalIgnoreCase)
            || message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot access the file", StringComparison.OrdinalIgnoreCase)
            || message.Contains("begärda åtgärden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("användarmappat", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
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

    private void MigrateLegacyStorageStateIfNeeded()
    {
        var legacyPath = AccountStoragePaths.LegacyBrowserStatePath(_projectRoot, _account.Name);
        if (File.Exists(StorageStatePath) || !File.Exists(legacyPath))
        {
            return;
        }

        var targetDirectory = Path.GetDirectoryName(StorageStatePath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(targetDirectory);
        File.Copy(legacyPath, StorageStatePath, overwrite: false);
    }

    private void DeleteLegacyStorageStateIfPresent()
    {
        var legacyPath = AccountStoragePaths.LegacyBrowserStatePath(_projectRoot, _account.Name);
        if (File.Exists(legacyPath))
        {
            File.Delete(legacyPath);
        }
    }
}
