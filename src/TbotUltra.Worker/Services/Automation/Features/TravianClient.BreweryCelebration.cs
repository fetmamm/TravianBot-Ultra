using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int BreweryCelebrationRetrySeconds = 60;

    public async Task<BreweryCelebrationStatus> ReadBreweryCelebrationStatusAsync(
        IReadOnlyList<Building>? knownBuildings = null,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();

        var tribe = await ReadTribeAsync(cancellationToken);
        if (!string.Equals(tribe, "Teutons", StringComparison.OrdinalIgnoreCase))
        {
            return new BreweryCelebrationStatus(
                false,
                null,
                false,
                null,
                false,
                null,
                "N/A",
                "Teutons only.");
        }

        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var isCapital = TryGetCachedCapitalState(activeVillage);
        if (!isCapital.HasValue)
        {
            await RefreshCapitalStateForActiveVillageAsync(cancellationToken);
            isCapital = TryGetCachedCapitalState(activeVillage);
        }

        var buildings = knownBuildings ?? (await ReadBuildingsStatusAsync(cancellationToken)).Buildings;
        var brewery = ResolveBreweryBuilding(buildings);
        int? brewerySlotId = brewery?.SlotId;
        if (brewerySlotId is not > 0)
        {
            brewerySlotId = await TryProbeBrewerySlotOnDorf2Async(cancellationToken);
        }

        if (brewerySlotId is not > 0)
        {
            return new BreweryCelebrationStatus(
                true,
                isCapital,
                false,
                null,
                false,
                null,
                "N/A",
                "Brewery not found.");
        }

        if (isCapital == false)
        {
            return new BreweryCelebrationStatus(
                true,
                false,
                true,
                brewerySlotId,
                false,
                null,
                "N/A",
                "Capital village required.");
        }

        await GotoAsync(Paths.BuildBySlot(brewerySlotId.Value), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading brewery celebration status.", cancellationToken);
        await EnsureLoggedInAsync();

        var pageStatus = await ReadBreweryCelebrationStatusFromCurrentPageAsync(cancellationToken);
        var remainingSeconds = pageStatus.RemainingSeconds ?? TravianParsing.ParseDurationToSeconds(pageStatus.RemainingText);
        return new BreweryCelebrationStatus(
            true,
            isCapital,
            true,
            brewerySlotId,
            pageStatus.CelebrationRunning,
            remainingSeconds,
            remainingSeconds is > 0 ? TravianParsing.FormatDuration(remainingSeconds.Value) : (pageStatus.CanStart ? "Ready" : "N/A"),
            pageStatus.StatusText,
            remainingSeconds is > 0 ? TimerSnapshot.FromRemaining(remainingSeconds.Value) : null);
    }

    private async Task<int?> TryProbeBrewerySlotOnDorf2Async(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentUrlForPath(Paths.Buildings))
            {
                await GotoAsync(Paths.Buildings, cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while probing brewery slot on dorf2.", cancellationToken);
                await EnsureLoggedInAsync();
            }

            var payload = await _page.EvaluateAsync<JsonElement>(
                """
                () => {
                  const slotFromText = (text) => {
                    if (!text) return null;
                    const m1 = String(text).match(/[?&](?:id|a)=(\d{1,2})\b/i);
                    if (m1) return parseInt(m1[1], 10);
                    const m2 = String(text).match(/\baid(\d{1,2})\b/i);
                    if (m2) return parseInt(m2[1], 10);
                    const m3 = String(text).match(/\ba(\d{1,2})\b/i);
                    if (m3) return parseInt(m3[1], 10);
                    return null;
                  };

                  const collectFromElement = (el) => {
                    if (!el) return null;
                    const candidates = [
                      el.getAttribute && el.getAttribute('data-aid'),
                      el.getAttribute && el.getAttribute('data-slot'),
                      el.getAttribute && el.getAttribute('href'),
                      el.className || '',
                      el.outerHTML || ''
                    ];
                    for (const c of candidates) {
                      const slot = slotFromText(c);
                      if (slot && slot >= 19 && slot <= 40) return slot;
                    }
                    let parent = el.parentElement;
                    for (let i = 0; parent && i < 4; i++, parent = parent.parentElement) {
                      const slot = slotFromText((parent.className || '') + ' ' + (parent.getAttribute && parent.getAttribute('href') || ''));
                      if (slot && slot >= 19 && slot <= 40) return slot;
                    }
                    return null;
                  };

                  const selectors = [
                    'div.buildingSlot.g35',
                    'div.buildingSlot[class*=" g35"]',
                    'div.buildingSlot[class^="g35"]',
                    '[data-gid="35"]',
                    'area.g35',
                    'area[class*=" g35"]',
                    'area[class^="g35"]',
                    'a[href*="gid=35"]',
                    'img[alt="Brewery" i]',
                    '[title="Brewery" i]'
                  ];

                  for (const sel of selectors) {
                    const nodes = document.querySelectorAll(sel);
                    for (const node of nodes) {
                      const slot = collectFromElement(node);
                      if (slot) return { slotId: slot, source: sel };
                    }
                  }

                  const slots = Array.from(document.querySelectorAll('div.buildingSlot'));
                  for (const slot of slots) {
                    const img = slot.querySelector('img[alt], [title]');
                    const alt = img ? (img.getAttribute('alt') || img.getAttribute('title') || '') : '';
                    if (/brewery/i.test(alt)) {
                      const id = collectFromElement(slot);
                      if (id) return { slotId: id, source: 'alt-brewery' };
                    }
                  }

                  return { slotId: null, source: null };
                }
                """);

            if (payload.TryGetProperty("slotId", out var slotIdNode)
                && slotIdNode.ValueKind == JsonValueKind.Number
                && slotIdNode.TryGetInt32(out var slot)
                && slot is >= 19 and <= 40)
            {
                var source = payload.TryGetProperty("source", out var sourceNode) ? sourceNode.GetString() : null;
                Notify($"[brewery:verbose] fallback probe found slot {slot} via {source ?? "unknown"}");
                return slot;
            }
        }
        catch (Exception ex) when (!IsTransientExecutionContextException(ex))
        {
            Notify($"[brewery:verbose] fallback probe failed: {ex.Message}");
        }

        return null;
    }

    public async Task<string> RunBreweryCelebrationAsync(CancellationToken cancellationToken = default)
    {
        Notify("[brewery] celebration run starting");
        await EnsureLoggedInAsync();

        var status = await ReadBreweryCelebrationStatusAsync(cancellationToken: cancellationToken);
        if (!status.IsAvailableForTribe)
        {
            Notify("[brewery] skip — Teutons only");
            return "Brewery celebration: Teutons only. queue_wait_seconds=600";
        }

        if (status.IsCapital == false)
        {
            Notify("[brewery] skip — capital village required");
            return "Brewery celebration: capital village required. queue_wait_seconds=600";
        }

        if (!status.BreweryExists || status.BrewerySlotId is not > 0)
        {
            Notify("[brewery] skip — brewery not found in this village");
            return "Brewery celebration: brewery not found in this village. queue_wait_seconds=600";
        }

        if (status.CelebrationRunning && status.RemainingSeconds is > 0)
        {
            Notify($"[brewery] already running — {TravianParsing.FormatDuration(status.RemainingSeconds.Value)} remaining");
            return $"Brewery celebration running. queue_wait_seconds={Math.Max(1, status.RemainingSeconds.Value)}";
        }

        Notify($"[brewery] attempting to start celebration at slot {status.BrewerySlotId.Value}");
        var startAttempt = await TryStartBreweryCelebrationFromCurrentPageAsync(cancellationToken);
        if (!startAttempt.Started)
        {
            // No start button usually means the celebration's resources aren't covered. If the user enabled
            // hero resources for brewery, top up from the hero inventory once (Official-only, best-effort)
            // and retry the start on the reloaded page.
            if (await TryHeroResourceTransferForBreweryAsync(
                    $"Brewery celebration (slot {status.BrewerySlotId.Value})", cancellationToken))
            {
                Notify("[brewery] topped up from the hero inventory; retrying start.");
                startAttempt = await TryStartBreweryCelebrationFromCurrentPageAsync(cancellationToken);
            }
        }

        if (!startAttempt.Started)
        {
            Notify($"[brewery] start failed — {startAttempt.Message}");
            return $"{startAttempt.Message} queue_wait_seconds={BreweryCelebrationRetrySeconds}";
        }

        var startHref = ResolveUrl(startAttempt.Href);
        if (!string.IsNullOrWhiteSpace(startHref))
        {
            await GotoAsync(startHref, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared after starting brewery celebration.", cancellationToken);
            await EnsureLoggedInAsync();
        }

        await GotoAsync(Paths.BuildBySlot(status.BrewerySlotId.Value), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after navigating back to brewery.", cancellationToken);
        await EnsureLoggedInAsync();

        var startedStatus = await ReadBreweryCelebrationStatusFromCurrentPageAsync(cancellationToken);
        var remainingSeconds = startedStatus.RemainingSeconds
            ?? TravianParsing.ParseDurationToSeconds(startedStatus.RemainingText)
            ?? BreweryCelebrationRetrySeconds;

        if (!startedStatus.CelebrationRunning)
        {
            Notify("[brewery] start did not register — will retry");
            return $"Brewery celebration: start did not register, retrying. queue_wait_seconds={BreweryCelebrationRetrySeconds}";
        }

        Notify($"[brewery] celebration started — {TravianParsing.FormatDuration(Math.Max(1, remainingSeconds))} remaining");
        return $"Brewery celebration started. queue_wait_seconds={Math.Max(1, remainingSeconds)}";
    }

    private static Building? ResolveBreweryBuilding(IReadOnlyList<Building> buildings)
    {
        return buildings.FirstOrDefault(item =>
            item.Gid == 35
            || string.Equals(item.Name, "Brewery", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BreweryCelebrationPageStatus> ReadBreweryCelebrationStatusFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _page.EvaluateAsync<JsonElement>(
            """
            () => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const runningTimer =
                document.querySelector('.under_progress .timer, table.under_progress .timer, .under_progress span.timer');
              const runningText = normalize(runningTimer ? runningTimer.textContent : '');
              const runningValueRaw = runningTimer ? runningTimer.getAttribute('value') : null;
              const runningValue = runningValueRaw ? parseInt(runningValueRaw, 10) : null;
              const inProgressLabel = Array.from(document.querySelectorAll('.build_details .act .none, .under_progress, .build_details .act'))
                .map(node => normalize(node.textContent || ''))
                .find(text => /celebration is in progress/i.test(text) || /underway/i.test(text)) || '';
              const startLink = document.querySelector('.build_details td.act a.research, .build_details td.act a[href*="pivo"]');
              const startButton = document.querySelector('.build_details td.act button:not([disabled])')
                || Array.from(document.querySelectorAll('button')).find(node => {
                  if (!node || node.disabled) return false;
                  const click = (node.getAttribute('onclick') || '').toLowerCase();
                  const value = (node.getAttribute('value') || '').trim().toLowerCase();
                  const label = (node.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  return click.includes('action=celebration') || value === 'celebrate' || label === 'celebrate';
                });
              const canStart = (!!startLink) || (!!startButton && !startButton.disabled);
              const actText = normalize(document.querySelector('.build_details td.act')?.textContent || '');
              const celebrationRunning = !!runningTimer || /celebration is in progress/i.test(inProgressLabel);
              const statusText = celebrationRunning
                ? 'Celebration running.'
                : canStart
                  ? 'Ready.'
                  : (actText || 'Celebration unavailable.');

              return {
                celebrationRunning,
                remainingText: runningText,
                remainingSeconds: Number.isFinite(runningValue) && runningValue > 0 ? runningValue : null,
                canStart,
                statusText
              };
            }
            """);

        var celebrationRunning = payload.TryGetProperty("celebrationRunning", out var celebrationRunningNode)
            && celebrationRunningNode.ValueKind == JsonValueKind.True;
        var remainingText = payload.TryGetProperty("remainingText", out var remainingTextNode)
            ? remainingTextNode.GetString() ?? string.Empty
            : string.Empty;
        int? remainingSeconds = null;
        if (payload.TryGetProperty("remainingSeconds", out var remainingSecondsNode)
            && remainingSecondsNode.ValueKind == JsonValueKind.Number
            && remainingSecondsNode.TryGetInt32(out var parsedSeconds)
            && parsedSeconds > 0)
        {
            remainingSeconds = parsedSeconds;
        }
        var canStart = payload.TryGetProperty("canStart", out var canStartNode)
            && canStartNode.ValueKind == JsonValueKind.True;
        var statusText = payload.TryGetProperty("statusText", out var statusTextNode)
            ? statusTextNode.GetString() ?? string.Empty
            : string.Empty;

        return new BreweryCelebrationPageStatus(
            celebrationRunning,
            remainingText,
            remainingSeconds,
            canStart,
            string.IsNullOrWhiteSpace(statusText) ? "Celebration unavailable." : statusText);
    }

    private async Task<BreweryCelebrationStartAttempt> TryStartBreweryCelebrationFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        var payload = await _page.EvaluateAsync<JsonElement>(
            """
            () => {
              const link = document.querySelector('.build_details td.act a.research, .build_details td.act a[href*="pivo"]');
              if (link) {
                return { kind: 'link', href: link.getAttribute('href') || '' };
              }
              // The start control can also be a plain button whose onclick navigates to
              // build.php?...action=celebration (dynamic id, localized label), not just the legacy
              // td.act button. Match by that action, its value/text, or the legacy selector.
              const isCelebrate = (node) => {
                if (!node || node.disabled) return false;
                const onclick = (node.getAttribute('onclick') || '').toLowerCase();
                const value = (node.getAttribute('value') || '').trim().toLowerCase();
                const text = (node.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                return onclick.includes('action=celebration') || value === 'celebrate' || text === 'celebrate';
              };
              const button = document.querySelector('.build_details td.act button:not([disabled])')
                || Array.from(document.querySelectorAll('button')).find(isCelebrate);
              if (button && !button.disabled) {
                // Prefer the celebration URL the button encodes — navigating to it is more reliable than
                // hoping a synthetic click fires the inline onclick's location change.
                const onclick = button.getAttribute('onclick') || '';
                const match = onclick.match(/['"]([^'"]*action=celebration[^'"]*)['"]/i);
                if (match) {
                  return { kind: 'link', href: match[1] };
                }
                button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                return { kind: 'button', href: '' };
              }
              return { kind: 'none', href: '' };
            }
            """);

        var kind = payload.TryGetProperty("kind", out var kindNode) ? kindNode.GetString() ?? "none" : "none";
        var href = payload.TryGetProperty("href", out var hrefNode) ? hrefNode.GetString() ?? string.Empty : string.Empty;

        return kind switch
        {
            "link" => new BreweryCelebrationStartAttempt(true, "Brewery celebration started.", href),
            "button" => new BreweryCelebrationStartAttempt(true, "Brewery celebration started.", string.Empty),
            _ => new BreweryCelebrationStartAttempt(false, "Brewery celebration: no start button available.", string.Empty),
        };
    }

    private sealed record BreweryCelebrationPageStatus(
        bool CelebrationRunning,
        string RemainingText,
        int? RemainingSeconds,
        bool CanStart,
        string StatusText);

    private sealed record BreweryCelebrationStartAttempt(
        bool Started,
        string Message,
        string Href);
}
