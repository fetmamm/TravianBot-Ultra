using Microsoft.Playwright;

namespace TbotUltra.Worker.Services.Automation.Combat;

internal interface IRallyPointNavigator
{
    Task OpenSendTroopsAsync(bool allowReuseCurrentPage, CancellationToken cancellationToken);
    Task<bool> IsRallyPointLevelZeroAsync(CancellationToken cancellationToken);
}

internal sealed class SendTroopsNavigator(
    IPage page,
    Func<string, CancellationToken, Task> gotoAsync,
    Func<CancellationToken, Task> ensureLoggedInAsync,
    Action<string> notify,
    string sendTroopsPath,
    string farmListFallbackPath) : IRallyPointNavigator
{
    public async Task OpenSendTroopsAsync(bool allowReuseCurrentPage, CancellationToken cancellationToken)
    {
        if (allowReuseCurrentPage && await IsSendTroopsPageAsync()) return;
        await gotoAsync(sendTroopsPath, cancellationToken);
        await ensureLoggedInAsync(cancellationToken);
        if (await IsSendTroopsPageAsync()) return;
        await gotoAsync(farmListFallbackPath, cancellationToken);
        await ensureLoggedInAsync(cancellationToken);
        await gotoAsync(sendTroopsPath, cancellationToken);
        await ensureLoggedInAsync(cancellationToken);
        if (await IsSendTroopsPageAsync()) return;
        if (await TryOpenSendTroopsTabAsync())
        {
            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
            await ensureLoggedInAsync(cancellationToken);
        }
        if (!await IsSendTroopsPageAsync())
            throw new InvalidOperationException("Rally Point does not appear to be constructed yet. Build Rally Point before sending troops.");
    }

    public async Task<bool> IsRallyPointLevelZeroAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await page.EvaluateAsync<bool>("""() => { const content = document.querySelector('#content.buildRallyPoint'); if (!content) return false; const match = (content.querySelector('.titleInHeader .level')?.textContent || '').match(/(\d+)/); return match ? parseInt(match[1], 10) === 0 : false; }""");
        }
        catch (PlaywrightException ex)
        {
            notify($"[farm-list] could not read Rally Point level: {ex.Message}");
            return false;
        }
    }

    private Task<bool> IsSendTroopsPageAsync() => page.EvaluateAsync<bool>("""() => { const hasCoords = !!document.querySelector('input[name="x"], input[name="y"], input[name*="xCoord" i], input[name*="yCoord" i], input[id*="xCoord" i], input[id*="yCoord" i]'); const hasAttackMode = !!document.querySelector('input[type="radio"][name="eventType"]'); return hasCoords && hasAttackMode && (document.body?.innerText || '').toLowerCase().includes('send troops'); }""");

    private Task<bool> TryOpenSendTroopsTabAsync() => page.EvaluateAsync<bool>("""() => { const target = Array.from(document.querySelectorAll('a.tabItem, .tabItem, a[href*="build.php?t=2"], a[href*="t=2"]')).find(node => { const text = (node.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase(); const href = (node.getAttribute('href') || '').toLowerCase(); return text.includes('send troops') || href.includes('build.php?t=2') || href.includes('t=2'); }); if (!target) return false; target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true })); return true; }""");
}
