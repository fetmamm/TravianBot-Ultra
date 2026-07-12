using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public const string ExpectedLanguage = TravianLanguageDetector.ExpectedLanguage;

    public async Task<string?> ReadCurrentLanguageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var language = await _page.EvaluateAsync<string?>(
                """
                () => {
                    // Chromium renders network/proxy failures as a localized internal document while
                    // keeping the requested Travian URL. Its html[lang] is the browser UI language,
                    // never the account's Travian language.
                    if (document.body?.classList.contains('neterror')
                        || document.querySelector('#main-frame-error, .error-code')) {
                        return null;
                    }
                    const gameLanguage = window.Travian?.Game?.language;
                    const bodyLanguage = document.body?.getAttribute('data-language');
                    const htmlLanguage = document.documentElement?.getAttribute('lang');
                    return gameLanguage || bodyLanguage || htmlLanguage || null;
                }
                """).WaitAsync(cancellationToken);
            return TravianLanguageDetector.Normalize(language);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"[language:verbose] current language read hit a transient navigation race: {ex.Message}");
            return null;
        }
    }

    public async Task EnsureExpectedLanguageAsync(CancellationToken cancellationToken = default)
    {
        string? language = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            language = await ReadCurrentLanguageAsync(cancellationToken);
            if (TravianLanguageDetector.IsExpected(language))
            {
                if (_session.LogValueChanged("language", TravianLanguageDetector.ExpectedLanguage))
                {
                    Notify($"[language] Travian language confirmed: {TravianLanguageDetector.ExpectedLanguage}.");
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                break;
            }

            if (attempt < 2)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            Notify("[language] Travian language unavailable because the current document is not a readable Travian page; retrying later.");
            throw new TransientNavigationException("Travian page is unavailable; language could not be verified.");
        }

        var display = language;
        Notify($"[language] ALARM: Travian language is '{display}', expected '{TravianLanguageDetector.ExpectedLanguage}'.");
        throw new UnexpectedTravianLanguageException(language);
    }

    public async Task<string?> SetLanguageToEnglishAsync(CancellationToken cancellationToken = default)
    {
        Notify("[language] setting Travian language to English.");
        await GotoAsync(Paths.Options, cancellationToken);

        await SetOptionsCheckboxStateAsync(
            "input#hideContextualHelp[name='hideContextualHelp']",
            shouldBeChecked: true,
            "hide contextual help",
            cancellationToken);
        await SetOptionsCheckboxStateAsync(
            "input#option_night_mode[name='option_night_mode']",
            shouldBeChecked: false,
            "disable night mood images",
            cancellationToken);

        var languageSelect = _page.Locator("form#settings select[name='language']").First;
        if (await languageSelect.CountAsync() == 0)
        {
            throw new InvalidOperationException("Could not find Travian language dropdown on the options page.");
        }

        await DelayBeforeClickAsync(cancellationToken, "set language dropdown");
        await languageSelect.SelectOptionAsync(
            [new SelectOptionValue { Value = TravianLanguageDetector.ExpectedLanguage }],
            new LocatorSelectOptionOptions { Timeout = _config.TimeoutMs });
        await DelayBeforeClickAsync(cancellationToken, "save language settings");

        var submitButton = _page.Locator("form#settings button[type='submit'], form#settings input[type='submit']").First;
        if (await submitButton.CountAsync() > 0)
        {
            await submitButton.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
        }
        else
        {
            await _page.Locator("form#settings").EvaluateAsync(
                "form => form.requestSubmit ? form.requestSubmit() : form.submit()");
        }

        await WaitForExpectedLanguageAfterSaveAsync(cancellationToken);
        await EnsureExpectedLanguageAsync(cancellationToken);
        Notify("[language] Travian language changed to English.");
        return await ReadCurrentLanguageAsync(cancellationToken);
    }

    private async Task SetOptionsCheckboxStateAsync(
        string selector,
        bool shouldBeChecked,
        string label,
        CancellationToken cancellationToken)
    {
        var checkbox = _page.Locator($"form#settings {selector}").First;
        if (await checkbox.CountAsync() == 0)
        {
            throw new InvalidOperationException($"Could not find Travian options checkbox '{label}' on the options page.");
        }

        var isChecked = await checkbox.IsCheckedAsync(new LocatorIsCheckedOptions { Timeout = _config.TimeoutMs });
        if (isChecked == shouldBeChecked)
        {
            Notify($"[language] options checkbox '{label}' already {(shouldBeChecked ? "checked" : "unchecked")}.");
            return;
        }

        await checkbox.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = _config.TimeoutMs });
        await DelayBeforeClickAsync(cancellationToken, label);
        await checkbox.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
        Notify($"[language] options checkbox '{label}' set to {(shouldBeChecked ? "checked" : "unchecked")}.");
    }

    private async Task WaitForExpectedLanguageAfterSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                expected => {
                    if (document.body?.classList.contains('neterror')
                        || document.querySelector('#main-frame-error, .error-code')) {
                        return false;
                    }
                    const gameLanguage = window.Travian?.Game?.language;
                    const bodyLanguage = document.body?.getAttribute('data-language');
                    const htmlLanguage = document.documentElement?.getAttribute('lang');
                    const current = gameLanguage || bodyLanguage || htmlLanguage || '';
                    return String(current).trim().toLowerCase() === String(expected).toLowerCase();
                }
                """,
                TravianLanguageDetector.ExpectedLanguage,
                new PageWaitForFunctionOptions { Timeout = _config.TimeoutMs }).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            Notify($"[language] language save did not verify before timeout: {ex.Message}");
        }
    }

    internal static string? ExtractLanguageFromHtmlForTests(string? html)
        => TravianLanguageDetector.ExtractFromHtml(html);

    internal static bool IsExpectedLanguageForTests(string? language)
        => TravianLanguageDetector.IsExpected(language);
}
