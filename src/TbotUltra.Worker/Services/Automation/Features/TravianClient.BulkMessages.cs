using Microsoft.Playwright;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private static readonly string[] BulkMessageRecipientSelectors =
    [
        "#messageForm input#receiver",
        "#messageForm input[name='an']",
        "input[name='recipients']",
        "textarea[name='recipients']",
        "input[name='recipient']",
        "textarea[name='recipient']",
        "input[name='to']",
        "textarea[name='to']",
        "input[name*='recipient' i]",
        "textarea[name*='recipient' i]",
        "input[name*='receiver' i]",
        "textarea[name*='receiver' i]",
        "input[id*='recipient' i]",
        "textarea[id*='recipient' i]",
        "input[id*='receiver' i]",
        "textarea[id*='receiver' i]",
        "input[placeholder*='Recipient' i]",
        "textarea[placeholder*='Recipient' i]",
        "input[placeholder*='Receiver' i]",
        "textarea[placeholder*='Receiver' i]",
        "input[placeholder*='Player' i]",
        "textarea[placeholder*='Player' i]",
        "input[aria-label*='Recipient' i]",
        "textarea[aria-label*='Recipient' i]",
    ];

    private static readonly string[] BulkMessageSubjectSelectors =
    [
        "#messageForm #subject input[name='be']",
        "#messageForm input[name='be']",
        "input[name='subject']",
        "input[name*='subject' i]",
        "input[id*='subject' i]",
        "input[placeholder*='Subject' i]",
        "input[aria-label*='Subject' i]",
        "input[name*='betreff' i]",
        "input[id*='betreff' i]",
        "input[placeholder*='Betreff' i]",
    ];

    private static readonly string[] BulkMessageBodySelectors =
    [
        "#messageForm textarea#message",
        "#messageForm textarea[name='message']",
        "textarea[name='message']",
        "textarea[name*='message' i]",
        "textarea[name*='body' i]",
        "textarea[name*='text' i]",
        "textarea[id*='message' i]",
        "textarea[id*='body' i]",
        "textarea[placeholder*='Message' i]",
        "textarea[aria-label*='Message' i]",
        "[contenteditable='true'][role='textbox']",
        "[contenteditable='true']",
        "textarea",
    ];

    private static readonly string[] BulkMessageSendButtonSelectors =
    [
        "#messageForm #send button[type='submit']",
        "#messageForm button[value='Send']",
        "form button[type='submit']",
        "form input[type='submit']",
        "button[type='submit']",
        "input[type='submit']",
        "button:has-text('Send')",
        "button:has-text('Send message')",
        "input[value*='Send' i]",
        ".button-content:has(.text:text-is('Send'))",
        ".button-container:has(.text:text-is('Send'))",
    ];

    private static readonly Regex BulkMessageMissingPlayerRegex =
        new(@"^\s*The\s+name\s+(.+?)\s+does\s+not\s+exist\.?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<IReadOnlyList<string>> SendBulkMessageBatchAsync(
        IReadOnlyList<string> playerNames,
        string subject,
        string message,
        CancellationToken cancellationToken = default)
    {
        var safePlayerNames = playerNames
            .Select(name => (name ?? string.Empty).Trim())
            .Where(name => name.Length > 0)
            .Where(name => !MapSqlPlayerParser.IsProtectedPlayerName(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var skippedProtected = playerNames.Count - safePlayerNames.Count;
        if (skippedProtected > 0)
        {
            Notify($"[bulk-messages] skipped {skippedProtected} protected/invalid recipient(s) before writing message.");
        }

        if (safePlayerNames.Count is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(playerNames), "A message batch must contain 1 to 25 players.");
        }

        Notify($"[bulk-messages] opening message writer for {safePlayerNames.Count} recipient(s).");
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await GotoAsync(MessagesWritePath, cancellationToken);
        await WaitForBulkMessageWriteFormAsync(cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening message writer.", cancellationToken);

        var recipients = await FindVisibleBulkMessageFieldAsync(BulkMessageRecipientSelectors, "recipients");
        var subjectInput = await FindVisibleBulkMessageFieldAsync(BulkMessageSubjectSelectors, "subject");
        var body = await FindVisibleBulkMessageFieldAsync(BulkMessageBodySelectors, "message body");

        if (recipients is null || subjectInput is null || body is null)
        {
            var missing = string.Join(", ", new[]
            {
                recipients is null ? "recipients" : null,
                subjectInput is null ? "subject" : null,
                body is null ? "message body" : null,
            }.Where(value => value is not null));
            throw new InvalidOperationException($"Could not find message write field(s): {missing}. Save the /messages/write DOM and add selectors.");
        }

        var currentPlayerNames = safePlayerNames.ToList();
        var recipientText = string.Join(';', currentPlayerNames);
        await DelayBeforeClickAsync(cancellationToken, "bulk messages focus recipients");
        await FillBulkMessageFieldAsync(recipients, recipientText, "recipients", cancellationToken);
        await DelayBeforeClickAsync(cancellationToken, "bulk messages focus subject");
        await FillBulkMessageFieldAsync(subjectInput, subject, "subject", cancellationToken);
        await DelayBeforeClickAsync(cancellationToken, "bulk messages focus message");
        await FillBulkMessageFieldAsync(body, message, "message body", cancellationToken);

        var sendButton = await FindVisibleBulkMessageFieldAsync(BulkMessageSendButtonSelectors, "send button");
        if (sendButton is null)
        {
            throw new InvalidOperationException("Could not find the message Send button. Save the /messages/write DOM and add selectors.");
        }

        var retryGuard = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (currentPlayerNames.Count == 0)
            {
                throw new InvalidOperationException("Bulk message batch has no valid recipients left after removing missing players.");
            }

            await DelayBeforeClickAsync(cancellationToken, "bulk messages send");
            await sendButton.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs }).WaitAsync(cancellationToken);
            var missingPlayerName = await TryHandleBulkMessageMissingPlayerDialogAsync(currentPlayerNames, cancellationToken);
            if (missingPlayerName is null)
            {
                await WaitAfterBulkMessageSendAsync(cancellationToken);
                Notify($"[bulk-messages] sent batch to {currentPlayerNames.Count} recipient(s).");
                return currentPlayerNames;
            }

            var removed = RemoveBulkMessageRecipient(currentPlayerNames, missingPlayerName);
            if (!removed)
            {
                throw new InvalidOperationException($"Bulk message recipient '{missingPlayerName}' does not exist, but it could not be matched to the current batch.");
            }

            retryGuard++;
            if (retryGuard > safePlayerNames.Count)
            {
                throw new InvalidOperationException("Bulk message missing-player retry guard reached.");
            }

            Notify($"[bulk-messages] removed missing recipient '{missingPlayerName}' and retrying batch with {currentPlayerNames.Count} recipient(s).");
            await FillBulkMessageFieldAsync(recipients, string.Empty, "recipients", cancellationToken);
            await FillBulkMessageFieldAsync(recipients, string.Join(';', currentPlayerNames), "recipients", cancellationToken);
        }
    }

    private async Task WaitForBulkMessageWriteFormAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.Locator("#messageForm input#receiver, #messageForm input[name='an']")
                .First
                .WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = Math.Min(_config.TimeoutMs, 10000),
                })
                .WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Message write form did not load: recipient field was not visible.");
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException($"Message write form did not load: {ex.Message}", ex);
        }
    }

    private async Task<ILocator?> FindVisibleBulkMessageFieldAsync(IReadOnlyList<string> selectors, string label)
    {
        foreach (var selector in selectors)
        {
            ILocator locator;
            try
            {
                locator = _page.Locator(selector);
            }
            catch
            {
                continue;
            }

            int count;
            try
            {
                count = Math.Min(await locator.CountAsync(), 10);
            }
            catch
            {
                continue;
            }

            for (var index = 0; index < count; index++)
            {
                var candidate = locator.Nth(index);
                try
                {
                    if (await candidate.IsVisibleAsync()
                        && await candidate.IsEnabledAsync(new LocatorIsEnabledOptions { Timeout = 1000 }))
                    {
                        Notify($"[bulk-messages:verbose] matched {label} selector '{selector}' index={index}.");
                        return candidate;
                    }
                }
                catch
                {
                    // Try the next candidate.
                }
            }
        }

        return null;
    }

    private async Task FillBulkMessageFieldAsync(ILocator field, string value, string label, CancellationToken cancellationToken)
    {
        await field.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs }).WaitAsync(cancellationToken);
        await field.FillAsync(value, new LocatorFillOptions { Timeout = _config.TimeoutMs }).WaitAsync(cancellationToken);
        await field.EvaluateAsync(
            """
            element => {
              element.dispatchEvent(new Event('input', { bubbles: true }));
              element.dispatchEvent(new Event('change', { bubbles: true }));
            }
            """).WaitAsync(cancellationToken);

        var actual = await field.EvaluateAsync<string>(
            """
            element => {
              if ('value' in element) return element.value || '';
              return element.textContent || '';
            }
            """).WaitAsync(cancellationToken);
        if (!string.Equals(
            NormalizeBulkMessageFieldValue(actual),
            NormalizeBulkMessageFieldValue(value),
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Could not fill bulk message {label}: field value was not accepted.");
        }
    }

    private static string NormalizeBulkMessageFieldValue(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    internal static string? TryExtractBulkMessageMissingPlayerName(string? dialogText)
    {
        if (string.IsNullOrWhiteSpace(dialogText))
        {
            return null;
        }

        var match = BulkMessageMissingPlayerRegex.Match(dialogText.Trim());
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private async Task<string?> TryHandleBulkMessageMissingPlayerDialogAsync(
        IReadOnlyCollection<string> attemptedPlayerNames,
        CancellationToken cancellationToken)
    {
        var content = _page.Locator(".dialogOverlay.dialogVisible #dialogContent").First;
        try
        {
            await content.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 1200,
            }).WaitAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (PlaywrightException)
        {
            return null;
        }

        var dialogText = await content.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 }).WaitAsync(cancellationToken);
        var missingPlayerName = TryExtractBulkMessageMissingPlayerName(dialogText);
        if (missingPlayerName is null)
        {
            return null;
        }

        var okButton = _page.Locator(".dialogOverlay.dialogVisible button.dialogButtonOk, .dialogOverlay.dialogVisible button.ok").First;
        await DelayBeforeClickAsync(cancellationToken, "bulk messages missing player dialog ok");
        await okButton.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs }).WaitAsync(cancellationToken);
        try
        {
            await _page.Locator(".dialogOverlay.dialogVisible")
                .First
                .WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Hidden,
                    Timeout = Math.Min(_config.TimeoutMs, 5000),
                })
                .WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The next field fill will fail clearly if the dialog still blocks the page.
        }

        var matchedName = attemptedPlayerNames.FirstOrDefault(name =>
            string.Equals(
                MapSqlPlayerParser.NormalizeNameKey(name),
                MapSqlPlayerParser.NormalizeNameKey(missingPlayerName),
                StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(matchedName) ? missingPlayerName : matchedName;
    }

    private static bool RemoveBulkMessageRecipient(List<string> playerNames, string missingPlayerName)
    {
        var missingKey = MapSqlPlayerParser.NormalizeNameKey(missingPlayerName);
        var removed = playerNames.RemoveAll(name =>
            string.Equals(MapSqlPlayerParser.NormalizeNameKey(name), missingKey, StringComparison.Ordinal));
        return removed > 0;
    }

    private async Task WaitAfterBulkMessageSendAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WaitForPageReadyAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Notify($"[bulk-messages:verbose] message send page-ready wait failed or no navigation occurred: {ex.Message}");
        }

        await PauseForManualStepIfVisibleAsync("Manual verification appeared after sending a message.", cancellationToken);

        var hasVisibleError = await _page.EvaluateAsync<bool>(
            """
            () => {
              const nodes = Array.from(document.querySelectorAll('.error, .errors, .alert, .warning, [class*="error" i], [class*="warning" i]'));
              return nodes.some(node => {
                const style = window.getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden') return false;
                const text = (node.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                return text.includes('recipient') || text.includes('receiver') || text.includes('subject') || text.includes('message');
              });
            }
            """).WaitAsync(cancellationToken);
        if (hasVisibleError)
        {
            throw new InvalidOperationException("Message send appears to have failed: the page shows a visible validation error.");
        }
    }
}
