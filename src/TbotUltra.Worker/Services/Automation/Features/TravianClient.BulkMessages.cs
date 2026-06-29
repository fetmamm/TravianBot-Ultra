using Microsoft.Playwright;
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

    public async Task SendBulkMessageBatchAsync(
        IReadOnlyList<string> playerNames,
        string subject,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (playerNames.Count is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(playerNames), "A message batch must contain 1 to 25 players.");
        }

        Notify($"[bulk-messages] opening message writer for {playerNames.Count} recipient(s).");
        await EnsureLoggedInAsync();
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

        var recipientText = string.Join(';', playerNames.Select(name => name.Trim()).Where(name => name.Length > 0));
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

        await DelayBeforeClickAsync(cancellationToken, "bulk messages send");
        await sendButton.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs }).WaitAsync(cancellationToken);
        await WaitAfterBulkMessageSendAsync(cancellationToken);
        Notify($"[bulk-messages] sent batch to {playerNames.Count} recipient(s).");
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

    private async Task WaitAfterBulkMessageSendAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = Math.Min(_config.TimeoutMs, 8000),
            }).WaitAsync(cancellationToken);
        }
        catch
        {
            // Some React submissions do not navigate; validation below still catches visible errors.
        }

        await ActionPacer.FromOptions(_config, Notify).DelayAsync(
            _config.ActionPacingPageLoadMinSeconds,
            _config.ActionPacingPageLoadMaxSeconds,
            cancellationToken,
            "after message send");
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
