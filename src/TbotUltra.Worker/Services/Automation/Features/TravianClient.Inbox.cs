using Microsoft.Playwright;
using System.Text.Json.Serialization;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<InboxStatus> ReadInboxStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("[inbox:verbose] reading unread counts");
        await EnsureLoggedInAsync();
        var unreadInbox = await ReadUnreadInboxCountsAsync(cancellationToken);
        Notify($"[inbox] unread — messages={unreadInbox.UnreadMessages}, reports={unreadInbox.UnreadReports}");
        return new InboxStatus(
            UnreadMessages: unreadInbox.UnreadMessages,
            UnreadReports: unreadInbox.UnreadReports);
    }

    public async Task<bool> MarkMessagesAsReadAsync(CancellationToken cancellationToken = default)
    {
        Notify("[inbox] marking messages as read");
        await EnsureLoggedInAsync();
        await GotoAsync(MessagesPath, cancellationToken);
        return await TryMarkMessagesAsReadAcrossPagesAsync(
            markSelectors:
            [
                "button",
                "a",
                ".markAllRead",
                "button[title*='read' i]",
                "a[title*='read' i]",
            ],
            unreadSelectorHints:
            [
                "a[href*='nachrichten' i]",
                "a[href*='message' i]",
                "#n6",
            ],
            cancellationToken: cancellationToken);
    }

    public async Task<bool> MarkReportsAsReadAsync(CancellationToken cancellationToken = default)
    {
        Notify("[inbox] marking reports as read");
        await EnsureLoggedInAsync();
        await GotoAsync(ReportsPath, cancellationToken);
        return await TryMarkInboxItemsAsReadAsync(
            markSelectors:
            [
                "button",
                "a",
                "button[title*='read' i]",
                "a[title*='read' i]",
                ".markAllRead",
            ],
            unreadSelectorHints:
            [
                "a[href*='berichte' i]",
                "a[href*='report' i]",
                "#n5",
            ],
            label: "reports",
            cancellationToken: cancellationToken);
    }

    private async Task<InboxUnreadCountsJs> ReadUnreadInboxCountsAsync(CancellationToken cancellationToken)
    {

        var result = await _page.EvaluateAsync<InboxUnreadCountsJs>(
            """
            () => {
              const numberFromBadge = (root) => {
                if (!root) return 0;
                const badges = root.querySelectorAll(
                  '.indicator, .speechBubbleContent, .notification, [class*="indicator"], [class*="badge"], [class*="counter"]'
                );
                for (const badge of badges) {
                  const text = (badge.textContent || '').replace(/\s+/g, ' ').trim();
                  // Accept plain counts ("1") and capped counts ("100+").
                  const match = text.match(/^(\d{1,4})\s*\+?$/);
                  if (match) return Number(match[1]);
                }
                // Fallback: an explicit data-count attribute.
                const dataCount = root.getAttribute && root.getAttribute('data-count');
                if (dataCount) {
                  const n = parseInt(dataCount, 10);
                  if (Number.isFinite(n)) return n;
                }
                return 0;
              };

              const readForId = (id) => {
                const el = document.getElementById(id);
                if (!el) return 0;
                return numberFromBadge(el);
              };

              // Official Travian (T4.6) nav has no #n5/#n6; it uses anchors
              // a.messages / a.reports with the unread count in a .indicator child.
              const readForSelector = (selector) => numberFromBadge(document.querySelector(selector));

              return {
                unreadMessages: readForId('n6') || readForSelector('a.messages, a[href="/messages"], a[href*="nachrichten"]'),
                unreadReports: readForId('n5') || readForSelector('a.reports, a[href="/report"], a[href*="berichte"]')
              };
            }
            """);

        return result ?? new InboxUnreadCountsJs();
    }

    private async Task<bool> TryMarkMessagesAsReadAcrossPagesAsync(
        IReadOnlyList<string> markSelectors,
        IReadOnlyList<string> unreadSelectorHints,
        CancellationToken cancellationToken)
    {
        const int maxPages = 50;

        var changed = false;
        for (var pageIndex = 0; pageIndex < maxPages; pageIndex++)
        {
            var pageChanged = await TryMarkInboxItemsAsReadAsync(
                markSelectors,
                unreadSelectorHints,
                label: "messages",
                cancellationToken: cancellationToken);
            changed |= pageChanged;

            var remaining = await ReadUnreadInboxCountsAsync(cancellationToken);
            if (remaining.UnreadMessages <= 0)
            {
                return changed;
            }

            if (!await TryNavigateToForwardInboxPageAsync(cancellationToken))
            {
                return changed;
            }

        }

        Notify("[inbox] stopped marking as read — page limit reached");
        return changed;
    }

    private async Task<bool> TryMarkInboxItemsAsReadAsync(
        IReadOnlyList<string> markSelectors,
        IReadOnlyList<string> unreadSelectorHints,
        string label,
        CancellationToken cancellationToken)
    {
        var before = await ReadUnreadInboxCountsAsync(cancellationToken);
        var beforeCount = label.Equals("reports", StringComparison.OrdinalIgnoreCase)
            ? before.UnreadReports
            : before.UnreadMessages;

        var clicked = false;
        if (label.Equals("messages", StringComparison.OrdinalIgnoreCase))
        {
            clicked = await TryMarkCurrentInboxPageAsReadViaCheckAllAsync();
            if (!clicked)
            {
                clicked = await TryMarkUnreadMessagesViaCheckboxesAsync();
            }
        }
        else if (label.Equals("reports", StringComparison.OrdinalIgnoreCase))
        {
            clicked = await TryMarkCurrentInboxPageAsReadViaCheckAllAsync();
            if (!clicked)
            {
                clicked = await TryMarkAllReportsAsReadAsync();
            }
        }

        foreach (var selector in markSelectors)
        {
            if (clicked)
            {
                break;
            }

            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                clicked = true;
                break;
            }
            catch
            {
                // Try next selector.
            }
        }

        if (!clicked)
        {
            clicked = await _page.EvaluateAsync<bool>(
                """
                (selectors) => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  for (const selector of selectors) {
                    for (const node of document.querySelectorAll(selector)) {
                      const text = clean(`${node.textContent || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`);
                      if (text.includes('mark as read') || text.includes('mark all') || text.includes('als gelesen') || text.includes('read')) {
                        node.click();
                        return true;
                      }
                    }
                  }
                  return false;
                }
                """,
                markSelectors);
        }

        if (clicked)
        {
            await Task.Delay(300, cancellationToken);
        }

        // Reload current inbox page to ensure counters refresh.
        await GotoAsync(_page.Url, cancellationToken);

        var after = await ReadUnreadInboxCountsAsync(cancellationToken);
        var afterCount = label.Equals("reports", StringComparison.OrdinalIgnoreCase)
            ? after.UnreadReports
            : after.UnreadMessages;

        if (afterCount < beforeCount)
        {
            return true;
        }

        if (beforeCount == 0)
        {
            return false;
        }

        // Fallback: inspect tab hints for unread classes/counters after click.
        var hintUnreads = await _page.EvaluateAsync<int>(
            """
            (selectors) => {
              const countFrom = (node) => {
                if (!node) return 0;
                const blob = `${node.className || ''} ${node.id || ''} ${node.textContent || ''} ${node.getAttribute('title') || ''}`.toLowerCase();
                const match = blob.match(/\b(\d{1,3})\b/);
                if (match) return Number(match[1]);
                return /unread|new|alert|active/.test(blob) ? 1 : 0;
              };

              let best = 0;
              for (const selector of selectors) {
                for (const node of document.querySelectorAll(selector)) {
                  best = Math.max(best, countFrom(node));
                  for (const child of node.querySelectorAll('*')) {
                    best = Math.max(best, countFrom(child));
                  }
                }
              }

              return best;
            }
            """,
            unreadSelectorHints);

        return hintUnreads == 0;
    }

    private async Task<bool> TryNavigateToForwardInboxPageAsync(CancellationToken cancellationToken)
    {
        var nextUrl = await _page.EvaluateAsync<string?>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const link = Array.from(document.querySelectorAll('.paginator a, a'))
                .find((node) => clean(node.textContent).includes('forward'));
              if (!link) return null;
              const href = link.getAttribute('href');
              if (!href) return null;
              return new URL(href, window.location.href).toString();
            }
            """);

        if (string.IsNullOrWhiteSpace(nextUrl)
            || string.Equals(nextUrl, _page.Url, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await GotoAsync(nextUrl, cancellationToken);
        return true;
    }

    private async Task<bool> TryMarkCurrentInboxPageAsReadViaCheckAllAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  // Official Travian (T4.6) uses id="sAll1"/"sAll2" for select-all (not "sAll").
                  const checkAll = document.querySelector("input#sAll[type='checkbox'], input[id^='sAll'][type='checkbox'], .footer .markAll input.check[type='checkbox'], .markAll input[type='checkbox']");
                  if (checkAll && !checkAll.checked) {
                    checkAll.click();
                  }

                  const checkboxes = Array.from(document.querySelectorAll("input[type='checkbox']"))
                    .filter((box) => !box.disabled && box !== checkAll);
                  for (const checkbox of checkboxes) {
                    if (!checkbox.checked) {
                      checkbox.checked = true;
                      checkbox.dispatchEvent(new Event('input', { bubbles: true }));
                      checkbox.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                  }

                  if (!checkAll && checkboxes.length === 0) {
                    return false;
                  }

                  // Official Travian (T4.6) messages submit button: <button name="mark" value="read" id="mark">.
                  const officialMark = document.querySelector("button#mark[name='mark'], button[name='mark'][value='read' i]");
                  if (officialMark && !officialMark.disabled) {
                    officialMark.click();
                    return true;
                  }

                  const contentNode = Array.from(document.querySelectorAll('div.button-content'))
                    .find((node) => clean(node.textContent).includes('mark as read') || clean(node.textContent).includes('als gelesen'));
                  if (contentNode) {
                    const clickable = contentNode.closest('.button-container, button, a, div.button, li, span');
                    (clickable || contentNode).click();
                    return true;
                  }

                  const fallbackNode = Array.from(document.querySelectorAll("button, a, input[type='submit'], div.button, span"))
                    .find((node) => {
                      const text = clean(`${node.textContent || ''} ${node.getAttribute('value') || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`);
                      return text.includes('mark as read') || text.includes('als gelesen');
                    });
                  if (!fallbackNode) {
                    return false;
                  }

                  fallbackNode.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsExecutionContextDestroyed(ex))
        {
            return true;
        }
    }

    private async Task<bool> TryMarkUnreadMessagesViaCheckboxesAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const unreadMarkers = Array.from(document.querySelectorAll("img.messageStatusUnread, img[alt*='unread' i], img[src*='x.gif']"));
                  const selected = new Set();

                  for (const marker of unreadMarkers) {
                    const row = marker.closest('tr, li, .message, .inboxRow, .row, .listEntry, .entry');
                    const scope = row || marker.parentElement || document;
                    const checkbox = scope.querySelector("input.check[type='checkbox'], input[type='checkbox'][name^='n'], input[type='checkbox']");
                    if (!checkbox || checkbox.disabled || selected.has(checkbox)) {
                      continue;
                    }

                    checkbox.checked = true;
                    checkbox.dispatchEvent(new Event('input', { bubbles: true }));
                    checkbox.dispatchEvent(new Event('change', { bubbles: true }));
                    selected.add(checkbox);
                  }

                  if (selected.size === 0) {
                    return false;
                  }

                  const isMarkAsRead = (value) => {
                    const text = clean(value);
                    return text.includes('mark as read') || text.includes('als gelesen');
                  };

                  const contentNode = Array.from(document.querySelectorAll('div.button-content'))
                    .find((node) => isMarkAsRead(node.textContent || ''));
                  if (contentNode) {
                    const clickable = contentNode.closest('button, a, div.button, li, span');
                    (clickable || contentNode).click();
                    return true;
                  }

                  const fallbackNode = Array.from(document.querySelectorAll("button, a, input[type='submit'], div.button, span"))
                    .find((node) => isMarkAsRead(`${node.textContent || ''} ${node.getAttribute('value') || ''} ${node.getAttribute('title') || ''} ${node.getAttribute('aria-label') || ''}`));
                  if (!fallbackNode) {
                    return false;
                  }

                  fallbackNode.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsExecutionContextDestroyed(ex))
        {
            return true;
        }
    }

    private async Task<bool> TryMarkAllReportsAsReadAsync()
    {
        try
        {
            // Step 1: trigger "Mark all as read". Official Travian (T4.6) uses a button with class
            // "markAsRead" (onclick handleMarkAsRead).
            var triggered = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const official = document.querySelector("button.markAsRead, button[onclick*='handleMarkAsRead']");
                  if (official && !official.disabled) { official.click(); return true; }
                  return false;
                }
                """);

            if (!triggered)
            {
                return false;
            }

            // Step 2: official Travian shows a confirmation dialog with a confirm button
            // (onclick processMarkAsRead). Click it if it appears (best-effort, fire-and-forget).
            await Task.Delay(400);
            await _page.EvaluateAsync(
                """
                () => {
                  const confirm = document.querySelector("button[onclick*='processMarkAsRead'], .dialog button.confirm, button.confirm.negativeAction");
                  if (confirm && !confirm.disabled) confirm.click();
                }
                """);
            return true;
        }
        catch (PlaywrightException ex) when (IsExecutionContextDestroyed(ex))
        {
            return true;
        }
    }

    private static bool IsExecutionContextDestroyed(PlaywrightException ex)
        => ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase);

    private sealed class InboxUnreadCountsJs
    {
        [JsonPropertyName("unreadMessages")]
        public int UnreadMessages { get; init; }

        [JsonPropertyName("unreadReports")]
        public int UnreadReports { get; init; }
    }
}
