using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Building surface of the TravianClient facade. The interface list is declared
// on this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : IBuildingClient
{

    public Task<string> ReadTribeOnlyAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        return ReadTribeAsync(cancellationToken);
    }

    public async Task<bool> IsTravianPlusActiveAsync(CancellationToken cancellationToken = default)
    {
        var state = await EvaluatePlusStateOnCurrentPageAsync(cancellationToken);
        Notify($"[plus:verbose] state='{state}' url='{_page.Url}'");

        // Source of truth is dorf1/dorf2: the village quick-links bar and the link-list edit button
        // both reflect Plus there (green=on, gold=off). Other pages (e.g. build.php with Plus active)
        // can be inconclusive, so re-read on dorf2 before falling back.
        if (state == PlusState.Unknown
            && !IsCurrentUrlForPath(Paths.Resources)
            && !IsCurrentUrlForPath(Paths.Buildings))
        {
            await GotoAsync(Paths.Buildings, cancellationToken);
            state = await EvaluatePlusStateOnCurrentPageAsync(cancellationToken);
            Notify($"[plus:verbose] dorf2 re-read state='{state}' url='{_page.Url}'");
        }

        // Conservative fallback: only a positive "on" signal counts as Plus active. Anything
        // unknown is treated as inactive so we never over-fill the build queue (1 slot, not 2).
        return state == PlusState.On;
    }

    private static class PlusState
    {
        public const string On = "on";
        public const string Off = "off";
        public const string Unknown = "unknown";
    }

    // Reads a tri-state Plus signal ("on"/"off"/"unknown") from the current page using verified,
    // language-independent markup. Never defaults to "on".
    private async Task<string> EvaluatePlusStateOnCurrentPageAsync(CancellationToken cancellationToken)
    {

        try
        {
            return await _page.EvaluateAsync<string>(
                """
                () => {
                  const hasClass = (el, c) => (el.className || '').toString().split(/\s+/).includes(c);

                  // 1) Village quick-links bar (dorf1/dorf2). Present in BOTH states; only the color
                  //    differs: green = Plus active, gold = Plus inactive. (Verified against live DOM.)
                  const quickLinks = Array.from(document.querySelectorAll('a[data-dragid^="villageQuickLinks"]'));
                  if (quickLinks.length > 0) {
                    if (quickLinks.some(node => hasClass(node, 'green'))) return 'on';
                    if (quickLinks.some(node => hasClass(node, 'gold'))) return 'off';
                  }

                  // 2) Link-list edit button in the sidebar (village pages). green + linklist.php = Plus
                  //    active; gold + a PlusDialog upsell onclick = Plus inactive. (Verified.)
                  const edit = document.querySelector('#sidebarBoxLinklist a.edit, #sidebarBoxLinklist a.layoutButton.edit');
                  if (edit) {
                    const onclick = edit.getAttribute('onclick') || '';
                    if (hasClass(edit, 'gold') || /PlusDialog/.test(onclick)) return 'off';
                    if (hasClass(edit, 'green')) return 'on';
                  }

                  // 3) Build page (build.php): the 2nd queue slot is advertised as a locked Plus feature
                  //    only when Plus is inactive. (Verified: `.plusAdvertising` with featureKey 'buildingQueue'.)
                  if (document.querySelector('.plusAdvertising')) return 'off';

                  return 'unknown';
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return PlusState.Unknown;
        }
    }



}

