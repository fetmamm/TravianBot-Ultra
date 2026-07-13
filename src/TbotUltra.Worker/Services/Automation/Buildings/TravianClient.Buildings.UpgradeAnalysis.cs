using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Upgrade button analysis and resource/actionability decisions.
public sealed partial class TravianClient
{
    private async Task<UpgradeAttemptResult> AnalyzeUpgradeActionabilityAsync(
        int slotId,
        CancellationToken cancellationToken,
        bool performClick,
        bool skipNavigationIfOnExpectedSlot = false)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!skipNavigationIfOnExpectedSlot || !TravianUrls.IsBuildPageForSlot(_page.Url, slotId))
                {
                    await GotoAsync(Paths.BuildBySlot(slotId), cancellationToken);
                }
                await PauseForManualStepIfVisibleAsync("Manual verification appeared while opening the upgrade page.", cancellationToken);
                await EnsureLoggedInAsync();
                await EnsureExpectedBuildSlotPageAsync(slotId, "analyze upgrade", cancellationToken);
                await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay

                var rawJson = await _page.EvaluateAsync<string>(
                    """
                    ({ profile }) => {
                      const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                      const textOf = (element) => clean(`${element.textContent || ''} ${element.getAttribute('value') || ''} ${element.getAttribute('title') || ''} ${element.getAttribute('aria-label') || ''}`);
                      const pageText = clean(document.body ? document.body.innerText : '').toLowerCase();
                      const normalizedProfile = clean(profile || '').toLowerCase();

                      const detectMaxLevel = () => {
                        const maxMatch = pageText.match(/max(?:imum)?[^0-9]{0,12}level[^0-9]{0,8}(\d{1,3})/i)
                          || pageText.match(/level[^0-9]{0,8}(\d{1,3})[^0-9]{0,8}max/i)
                          || pageText.match(/(?:level|lvl)[^0-9]{0,6}\d{1,3}\s*\/\s*(\d{1,3})/i);
                        return maxMatch ? Number(maxMatch[1]) : null;
                      };

                      const noneHints = Array.from(document.querySelectorAll('span.none, div.none, .none'))
                        .map((node) => clean(node.textContent || '').toLowerCase())
                        .filter((text) => text.length > 0);
                      const workersBusyHint = noneHints.find((text) => /all\s*workers\s*are\s*busy/.test(text)) || null;
                      const resourcesAvailableHint = noneHints.find((text) => /resources\s*will\s*be\s*available/.test(text)) || null;

                      const blockedByMax = /max(?:imum)?\s*level|max\s*reached|maxlevel|already\s*max/i.test(pageText);
                      const blockedByQueue = !!workersBusyHint
                        || /building\s*queue|construction\s*queue|under\s*construction|queue\s*full|busy|occupied|cannot\s*start/i.test(pageText);
                      const blockedByResources = !!resourcesAvailableHint
                        || /not\s*enough|insufficient|resources|lumber|clay|iron|crop|wood|missing\s*resources|requires\s*more/i.test(pageText);
                      const parseDurationSeconds = (raw) => {
                        const text = clean(raw || '');
                        if (!text) {
                          return null;
                        }

                        const full = text.match(/(\d{1,3})\s*:\s*(\d{1,2})\s*:\s*(\d{1,2})/);
                        if (full) {
                          return Number(full[1]) * 3600 + Number(full[2]) * 60 + Number(full[3]);
                        }

                        const short = text.match(/(^|[^\d])(\d{1,3})\s*:\s*(\d{1,2})([^\d]|$)/);
                        if (short) {
                          return Number(short[2]) * 60 + Number(short[3]);
                        }

                        const sec = text.match(/(\d{1,6})\s*s(?:ec|econd)?s?\b/i);
                        if (sec) {
                          return Number(sec[1]);
                        }

                        const min = text.match(/(\d{1,4})\s*m(?:in|inute)?s?\b/i);
                        if (min) {
                          return Number(min[1]) * 60;
                        }

                        return null;
                      };

                      const parseTargetLevel = (raw) => {
                        const match = clean(raw || '').match(/upgrade\s+to\s+level\s+(\d{1,3})/i);
                        return match ? Number(match[1]) : null;
                      };

                      const detectQueueWaitSeconds = () => {
                        const timerSelectors = [
                          '.buildingList .timer',
                          '.buildingList .countdown',
                          '.buildingList .value',
                          '#building_contract .timer',
                          '#building_contract .countdown',
                          '#building_contract .value',
                          '.underConstruction .timer',
                          '.underConstruction .countdown',
                          '.underConstruction .value',
                          '[id^="timer"]',
                          '[counting="down"]',
                          '.timer',
                          '.countdown',
                          '.value'
                        ];

                        for (const selector of timerSelectors) {
                          const nodes = document.querySelectorAll(selector);
                          for (const node of nodes) {
                            const seconds = parseDurationSeconds(node.textContent || '');
                            if (seconds && seconds > 0) {
                              return seconds;
                            }
                          }
                        }
                        return null;
                      };

                      const readServerNow = () => {
                        const candidates = ['#servertime .timeStandard', '#servertime', '.serverTime'];
                        for (const sel of candidates) {
                          const el = document.querySelector(sel);
                          const t = clean(el?.textContent || '');
                          const m = t.match(/(\d{1,2}):(\d{2}):(\d{2})/);
                          if (m) {
                            const now = new Date();
                            now.setHours(Number(m[1]), Number(m[2]), Number(m[3]), 0);
                            return now;
                          }
                        }
                        return new Date();
                      };

                      const parseClockTimeToSeconds = (raw) => {
                        const text = clean(raw || '');
                        if (!text) return null;
                        const tomorrow = /(tomorrow|morgen|imorgon|i\s*morgon|demain|domani|ma[ñn]ana|jutro)/i.test(text);
                        const today = /(today|heute|idag|i\s*dag|aujourd|oggi|hoy|dzisiaj)/i.test(text);
                        if (!today && !tomorrow) return null;
                        const m = text.match(/(\d{1,2}):(\d{2}):(\d{2})/);
                        if (!m) return null;
                        const target = readServerNow();
                        target.setHours(Number(m[1]), Number(m[2]), Number(m[3]), 0);
                        if (tomorrow) target.setDate(target.getDate() + 1);
                        let diff = Math.round((target.getTime() - readServerNow().getTime()) / 1000);
                        if (today && diff < 0) diff += 86400;
                        return diff > 0 ? diff : null;
                      };

                      const detectResourceWaitSeconds = () => {
                        const sources = [];
                        if (resourcesAvailableHint) {
                          sources.push(resourcesAvailableHint);
                        }
                        for (const node of document.querySelectorAll('span.none, div.none, .none, .contract, .errorMessage, .error')) {
                          const text = clean(node.textContent || '');
                          if (!text) {
                            continue;
                          }

                          if (/resources\s*will\s*be\s*available/i.test(text) || /not\s*enough|insufficient|missing\s*resources/i.test(text)) {
                            sources.push(text);
                          }
                        }

                        for (const source of sources) {
                          const clockSeconds = parseClockTimeToSeconds(source);
                          if (clockSeconds && clockSeconds > 0) {
                            return clockSeconds;
                          }
                          const seconds = parseDurationSeconds(source);
                          if (seconds && seconds > 0) {
                            return seconds;
                          }
                        }

                        return null;
                      };

                      const score = (candidate) => {
                        const green = candidate.classes.includes('green');
                        const upgradeText = candidate.text.includes('upgrade') || candidate.text.includes('build');
                        const signalClass = candidate.classes.includes('upgrade') || candidate.classes.includes('build') || candidate.classes.includes('contract');
                        const container = candidate.inUpgradeContainer;
                        const officialPrimary = candidate.inOfficialPrimarySection;
                        if (normalizedProfile === 'strict_green') {
                          return (officialPrimary ? 8 : 0) + (green ? 6 : 0) + (container ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        if (normalizedProfile === 'container_first') {
                          return (officialPrimary ? 8 : 0) + (container ? 6 : 0) + (green ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        if (normalizedProfile === 'aggressive') {
                          return (officialPrimary ? 8 : 0) + (signalClass ? 4 : 0) + (container ? 3 : 0) + (green ? 2 : 0) + (upgradeText ? 1 : 0);
                        }
                        return (officialPrimary ? 8 : 0) + (green ? 3 : 0) + (container ? 2 : 0) + (upgradeText ? 1 : 0);
                      };

                      const candidates = Array.from(document.querySelectorAll('button, input[type="submit"], input[type="button"], a, div.addHoverClick, div.button-container'));
                      const picked = [];
                      const clickOrder = [];
                      let hasMasterBuilderOnlyControl = false;

                      for (let candidateIndex = 0; candidateIndex < candidates.length; candidateIndex += 1) {
                        const element = candidates[candidateIndex];
                        const text = textOf(element).toLowerCase();
                        const classes = clean(element.className || '').toLowerCase();
                        const href = (element.getAttribute('href') || '').toLowerCase();
                        const form = element.closest('form');
                        const formAction = (form ? form.getAttribute('action') : '') || '';
                        const control = element.closest('button, input[type="submit"], input[type="button"], a, div.addHoverClick');
                        const controlText = control ? textOf(control).toLowerCase() : '';
                        const controlClasses = control ? clean(control.className || '').toLowerCase() : '';
                        const onclick = `${(element.getAttribute('onclick') || '')} ${(control ? control.getAttribute('onclick') || '' : '')}`.toLowerCase();
                        const combined = `${text} ${classes} ${controlText} ${controlClasses} ${href} ${formAction} ${onclick}`;
                        const displayText = text || controlText;
                        const disabled = !!(
                          element.disabled
                          || classes.includes('disabled')
                          || element.getAttribute('aria-disabled') === 'true'
                          || (control && control.disabled)
                          || controlClasses.includes('disabled')
                          || (control && control.getAttribute('aria-disabled') === 'true')
                        );
                        const inOfficialPrimarySection = !!element.closest('.upgradeButtonsContainer .section1');
                        const inOfficialSpeedupSection = !!element.closest('.upgradeButtonsContainer .section2');
                        const isMasterBuilder = combined.includes('master builder') || combined.includes('buildmaster');
                        const isGold = isMasterBuilder || combined.includes('gold') || combined.includes('npc') || combined.includes('instant') || combined.includes('exchange') || combined.includes('sharing resources') || combined.includes('share resources');
                        // Official payment-wizard decoy: the green "Open shop" button (onclick=openPaymentWizard)
                        // is not a build control but matches the bare 'green' signal below. Clicking it opens a
                        // modal whose #dialogOverlay then intercepts all later clicks → upgrade-click timeout loop.
                        // Match the locale-independent onclick first, then the English text/value as a fallback.
                        const isPaymentShop = combined.includes('openpaymentwizard') || combined.includes('paymentwizard') || combined.includes('open shop');
                        // This scanner runs only for upgrades. A "Construct building" control means the slot is
                        // empty / shows the construction-choice page — never a valid upgrade candidate. Its text
                        // contains "build", so without this guard it leaks through as a false CanUpgrade. Match on
                        // the button TEXT only: both construct and upgrade buttons share onclick `action=build`,
                        // so the action keyword cannot distinguish them — the text ("Construct building" vs
                        // "Upgrade to level N") is the reliable signal.
                        const isConstruct = /construct\s+building/i.test(displayText);
                        // Village-map level badges (e.g. dorf2 building-slot overlays
                        // `<a class="level colorlayer good aidNN <tribe>" href="build.php?id=N">`) link to
                        // build.php but only carry the slot's current level number — they are NOT upgrade
                        // controls. They leak in via the bare `href build.php` signal below and, being the
                        // only "candidate" found, mask the real blocked state into a false CanUpgrade →
                        // misleading "could not find Upgrade to level N" alarm. The `colorlayer` overlay class
                        // never appears on a real upgrade button, so exclude it.
                        const isLevelBadge = classes.includes('colorlayer') || controlClasses.includes('colorlayer');
                        // The hero adventure button is green (`... adventure green attention`) but unrelated to
                        // building upgrades; it leaks in via the bare `green` signal. The `adventure` class
                        // never appears on a real upgrade button, so exclude it.
                        const isAdventure = classes.includes('adventure') || controlClasses.includes('adventure');
                        const isSpeedup = inOfficialSpeedupSection || classes.includes('purple') || controlClasses.includes('purple') || classes.includes('videofeaturebutton') || controlClasses.includes('videofeaturebutton') || combined.includes('videoFeature') || combined.includes('videofeature') || combined.includes('faster');
                        const inUpgradeContainer = !!element.closest('.upgradeBuilding, .contract, .contractWrapper, .build_details, .buildingWrapper, #contract, form[action*="build.php"]');
                        const hasUpgradeSignals =
                          inOfficialPrimarySection
                          || classes.includes('green')
                          || controlClasses.includes('green')
                          || classes.includes('upgrade')
                          || controlClasses.includes('upgrade')
                          || classes.includes('build')
                          || controlClasses.includes('build')
                          || classes.includes('contract')
                          || controlClasses.includes('contract')
                          || classes.includes('addhoverclick')
                          || controlClasses.includes('addhoverclick')
                          || href.includes('build.php')
                          || formAction.includes('build.php')
                          || (inUpgradeContainer && /upgrade\s+to\s+level|upgrade|build/i.test(displayText))
                          || /upgrade\s+to\s+level/i.test(displayText);

                        if (isMasterBuilder) {
                          hasMasterBuilderOnlyControl = true;
                        }

                        const looksLikePrimaryNoise = inOfficialPrimarySection
                          && !/upgrade|construct|build/i.test(displayText)
                          && !href.includes('action=build')
                          && !formAction.includes('build.php');

                        if (!hasUpgradeSignals || isGold || isPaymentShop || isConstruct || isLevelBadge || isAdventure || isSpeedup || looksLikePrimaryNoise || displayText.length === 0) {
                          continue;
                        }

                        picked.push({
                          text: displayText.slice(0, 120),
                          classes: classes.slice(0, 120),
                          disabled,
                          inUpgradeContainer,
                          inOfficialPrimarySection
                        });

                        if (!disabled) {
                          clickOrder.push({ candidateIndex, text: displayText, targetLevel: parseTargetLevel(displayText), classes: `${classes} ${controlClasses}`, inUpgradeContainer, inOfficialPrimarySection });
                        }
                      }

                      clickOrder.sort((a, b) => score(b) - score(a));

                      // Official "not enough resources" hard block: the .upgradeBlocked panel
                      // replaces the green upgrade button with an errorMessage ("Enough resources
                      // on DD.MM. at HH:MM") plus only master-builder/exchange (gold) controls.
                      // When this panel is present we must NOT click any leftover candidate — that
                      // produced an endless click/navigate spam loop. Take the precise wait from the
                      // panel's embedded countdown timer (value=<seconds>) so the task defers cleanly.
                      const upgradeBlockedEl = document.querySelector('.upgradeBlocked');
                      if (upgradeBlockedEl) {
                        const blockText = clean(upgradeBlockedEl.textContent || '').toLowerCase();
                        const isResourceBlock = /enough\s*resources\s*on|not\s*enough|insufficient|missing\s*resources/i.test(blockText);
                        const isStorageCapacityBlock =
                          /extend\s+(?:the\s+)?(?:warehouse|granary|silo)/i.test(blockText)
                          || /(?:warehouse|granary|silo)(?:\s+and\s+(?:warehouse|granary|silo))?\s+first/i.test(blockText);
                        if (isResourceBlock || isStorageCapacityBlock) {
                          let blockedSeconds = null;
                          const timerEl = upgradeBlockedEl.querySelector('.timer[value], .timer[data-value]');
                          if (timerEl) {
                            const v = Number(timerEl.getAttribute('value') || timerEl.getAttribute('data-value'));
                            if (Number.isFinite(v) && v > 0) {
                              blockedSeconds = v;
                            }
                          }
                          if (blockedSeconds === null) {
                            blockedSeconds = detectResourceWaitSeconds();
                          }
                          return JSON.stringify({
                            outcome: 'BlockedByResources',
                            reason: isStorageCapacityBlock
                              ? 'Upgrade blocked: storage capacity is too low (upgradeBlocked panel).'
                              : 'Upgrade blocked: not enough resources yet (upgradeBlocked panel).',
                            detectedMaxLevel: detectMaxLevel(),
                            queueWaitSeconds: blockedSeconds,
                            summary: picked.slice(0, 8)
                          });
                        }
                      }

                      if (clickOrder.length > 0) {
                        return JSON.stringify({
                          outcome: 'CanUpgrade',
                          reason: `Detected candidate '${clickOrder[0].text.slice(0, 80)}'`,
                          detectedMaxLevel: detectMaxLevel(),
                          queueWaitSeconds: detectQueueWaitSeconds(),
                          detectedTargetLevel: clickOrder[0].targetLevel,
                          candidateIndex: clickOrder[0].candidateIndex,
                          summary: picked.slice(0, 8)
                        });
                      }

                      if (blockedByMax) {
                        return JSON.stringify({
                          outcome: 'BlockedByMaxLevel',
                          reason: 'Page indicates max level reached.',
                          detectedMaxLevel: detectMaxLevel(),
                          summary: picked.slice(0, 8)
                        });
                      }

                      if (blockedByQueue) {
                        const queueWaitSeconds = detectQueueWaitSeconds();
                        return JSON.stringify({
                          outcome: 'BlockedByQueue',
                          reason: workersBusyHint
                            ? `Page indicates workers are busy: '${workersBusyHint.slice(0, 120)}'.`
                            : 'Page indicates building queue/slot is busy.',
                          detectedMaxLevel: detectMaxLevel(),
                          queueWaitSeconds,
                          summary: picked.slice(0, 8)
                        });
                      }

                      if (hasMasterBuilderOnlyControl) {
                        const queueWaitSeconds = detectQueueWaitSeconds();
                        return JSON.stringify({
                          outcome: 'BlockedByQueue',
                          reason: 'Only master builder construction is available; normal build queue is full.',
                          detectedMaxLevel: detectMaxLevel(),
                          queueWaitSeconds,
                          summary: picked.slice(0, 8)
                        });
                      }

                      if (blockedByResources) {
                        const resourceWaitSeconds = detectResourceWaitSeconds();
                        return JSON.stringify({
                          outcome: 'BlockedByResources',
                          reason: resourcesAvailableHint
                            ? `Page indicates resources are not ready yet: '${resourcesAvailableHint.slice(0, 120)}'.`
                            : 'Page indicates not enough resources.',
                          detectedMaxLevel: detectMaxLevel(),
                          queueWaitSeconds: resourceWaitSeconds,
                          summary: picked.slice(0, 8)
                        });
                      }

                      return JSON.stringify({
                        outcome: 'BlockedUnknown',
                        reason: 'No actionable upgrade control found.',
                        detectedMaxLevel: detectMaxLevel(),
                        summary: picked.slice(0, 8)
                      });
                    }
                    """,
                    new
                    {
                        profile = string.IsNullOrWhiteSpace(_config.UpgradeSelectorProfile) ? "auto" : _config.UpgradeSelectorProfile
                    });

                var parsed = string.IsNullOrWhiteSpace(rawJson)
                    ? null
                    : JsonSerializer.Deserialize<UpgradeActionabilityJs>(rawJson);

                var outcome = TravianParsing.ParseUpgradeOutcome(parsed?.Outcome);
                var reason = string.IsNullOrWhiteSpace(parsed?.Reason)
                    ? "Unknown actionability result."
                    : parsed!.Reason!;
                if ((outcome == UpgradeAttemptOutcome.BlockedByQueue || outcome == UpgradeAttemptOutcome.BlockedByResources)
                    && parsed?.QueueWaitSeconds is int waitSeconds
                    && waitSeconds > 0)
                {
                    reason = $"{reason} queue_wait_seconds={waitSeconds}";
                }
                var summary = parsed?.Summary is { Count: > 0 }
                    ? string.Join(" | ", parsed.Summary.Take(3).Select(item => $"{item.Text} [{item.Classes}] disabled={item.Disabled}"))
                    : string.Empty;

                if (performClick && outcome == UpgradeAttemptOutcome.CanUpgrade)
                {
                    await ClickDetectedUpgradeCandidateAsync(slotId, parsed?.CandidateIndex, cancellationToken);
                    reason = $"Clicked detected upgrade candidate for slot {slotId} (index {parsed?.CandidateIndex?.ToString() ?? "?"}).";
                }

                if (outcome == UpgradeAttemptOutcome.BlockedUnknown)
                {
                    if (summary.Length > 0)
                    {
                        Notify($"Upgrade actionability debug for slot {slotId}: {summary}");
                    }

                    await CaptureFailureArtifactsAsync($"upgrade-slot-{slotId}-blocked-unknown", cancellationToken);
                }

                await RetryAsync("wait for page load", async () =>
                {
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded)
                        .WaitAsync(cancellationToken);
                }, cancellationToken: cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after upgrade actionability analysis.", cancellationToken);

                return new UpgradeAttemptResult(
                    Outcome: outcome,
                    Reason: reason,
                    DetectedMaxLevel: parsed?.DetectedMaxLevel,
                    QueueWaitSeconds: parsed?.QueueWaitSeconds,
                    DetectedTargetLevel: parsed?.DetectedTargetLevel,
                    CandidateIndex: parsed?.CandidateIndex,
                    DebugSummary: summary);
            }
            catch (ManualVerificationRequiredException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3 && IsTransientExecutionContextException(ex))
            {
                Notify($"Upgrade analysis for slot {slotId} hit transient execution-context error on attempt {attempt}/3. Retrying...");
                await Task.Delay(250 * attempt, cancellationToken);
                continue;
            }
            catch (Exception ex)
            {
                await CaptureFailureArtifactsAsync($"upgrade-slot-{slotId}-exception", cancellationToken);
                throw new InvalidOperationException($"Upgrade analysis failed for slot {slotId}: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException($"Upgrade analysis failed for slot {slotId}: exhausted retries.");
    }

}
