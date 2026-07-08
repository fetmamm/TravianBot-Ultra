using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<string?> ReadCurrentLanguageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var language = await _page.EvaluateAsync<string?>(
                """
                () => {
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
                Notify($"[language] Travian language confirmed: {TravianLanguageDetector.ExpectedLanguage}.");
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

        var display = string.IsNullOrWhiteSpace(language) ? "unknown" : language;
        Notify($"[language] ALARM: Travian language is '{display}', expected '{TravianLanguageDetector.ExpectedLanguage}'.");
        throw new UnexpectedTravianLanguageException(language);
    }

    public async Task<string?> SetLanguageToEnglishAsync(CancellationToken cancellationToken = default)
    {
        Notify("[language] setting Travian language to English.");
        await GotoAsync(Paths.Options, cancellationToken);

        var languageSelect = _page.Locator("form#settings select[name='language']").First;
        if (await languageSelect.CountAsync() == 0)
        {
            throw new InvalidOperationException("Could not find Travian language dropdown on the options page.");
        }

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

        await WaitForPageReadyAsync(cancellationToken);
        await EnsureExpectedLanguageAsync(cancellationToken);
        Notify("[language] Travian language changed to English.");
        return await ReadCurrentLanguageAsync(cancellationToken);
    }

    internal static string? ExtractLanguageFromHtmlForTests(string? html)
        => TravianLanguageDetector.ExtractFromHtml(html);

    internal static bool IsExpectedLanguageForTests(string? language)
        => TravianLanguageDetector.IsExpected(language);
}
