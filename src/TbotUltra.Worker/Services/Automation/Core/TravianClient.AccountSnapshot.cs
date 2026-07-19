using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Configuration;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task RefreshAccountFeatureSignalsAsync(CancellationToken cancellationToken = default)
    {
        using var trace = _browserTrace.BeginOperation("REFRESH", "account-feature-signals", "reason=preflight source=live-or-cache");
        // Plus can change during a round. The account tribe cannot, and Gold Club only matters as a latched true.
        if (_cachedTravianPlusActive.HasValue
            && (_cachedGoldClubEnabled == true)
            && DateTimeOffset.UtcNow - _cachedTribePlusAt < TimeSpan.FromSeconds(60))
        {
            _browserTrace.Event("CACHE", "account-feature-signals-hit", "hit", "ageSeconds<60 plus=true goldClub=true");
            trace.Complete("success", "source=cache changed=false");
            return;
        }

        _browserTrace.Event("CACHE", "account-feature-signals-miss", "miss", "reason=missing-stale-or-unlatched");

        try
        {
            var plus = await IsTravianPlusActiveAsync(cancellationToken);
            if (!_cachedTravianPlusActive.HasValue || _cachedTravianPlusActive != plus)
            {
                _cachedTravianPlusActive = plus;
                Notify($"[plus] active={plus}");
            }
            _cachedTribePlusAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Notify($"Plus status check failed: {ex.Message}");
        }

        // Gold Club is monotonic — once true within a session it cannot revert. Skip re-checks once latched.
        if (_cachedGoldClubEnabled != true)
        {
            try
            {
                var gold = await ReadGoldClubEnabledAsync(cancellationToken);
                if (!_cachedGoldClubEnabled.HasValue || _cachedGoldClubEnabled != gold)
                {
                    Notify($"[goldclub] active={gold}");
                }
                _cachedGoldClubEnabled = gold;
            }
            catch (Exception ex)
            {
                Notify($"Gold Club status check failed: {ex.Message}");
            }
        }

        trace.Complete(
            "success",
            $"source=live plus={_cachedTravianPlusActive?.ToString() ?? "unknown"} goldClub={_cachedGoldClubEnabled?.ToString() ?? "unknown"} accountTribe={_accountTribe ?? "unknown"}");
    }

    public async Task<VillageStatus> ReadVillageStatusAsync(CancellationToken cancellationToken = default)
    {
        using var trace = _browserTrace.BeginOperation("REFRESH", "village-status", "reason=manual-or-background scope=active-village");
        Notify("ReadVillageStatusAsync started");
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        var result = await ReadCurrentVillageStatusAsync(cancellationToken);
        trace.Complete(
            "success",
            $"village={result.ActiveVillage} resources={result.Resources.Count} buildings={result.Buildings.Count} queue={result.BuildQueue.Count}");
        return result;
    }

    public async Task<VillageStatus> ReadVillageStatusAsync(
        IReadOnlyList<Village> knownVillages,
        IReadOnlyList<Building> knownBuildings,
        CancellationToken cancellationToken = default)
    {
        using var trace = _browserTrace.BeginOperation("REFRESH", "village-status", "reason=manual-or-background scope=active-village knownState=true");
        Notify("ReadVillageStatusAsync started with known villages/buildings");
        if (!IsCurrentUrlForPath(Paths.Resources))
        {
            await GotoAsync(Paths.Resources, cancellationToken);
        }

        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        var result = await ReadCurrentVillageStatusAsync(cancellationToken, knownVillages, knownBuildings);
        trace.Complete(
            "success",
            $"village={result.ActiveVillage} resources={result.Resources.Count} buildings={result.Buildings.Count} queue={result.BuildQueue.Count}");
        return result;
    }

    public async Task<AccountSnapshot> ReadAccountSnapshotAsync(
        bool forceRefreshVillages = false,
        bool preferCurrentPageVillages = false,
        bool restorePageAfterProfile = true,
        bool suppressEnsureUiSync = false,
        bool skipOverviewNavigation = false,
        CancellationToken cancellationToken = default)
    {
        using var trace = _browserTrace.BeginOperation(
            "REFRESH",
            "account-snapshot",
            $"forceVillages={forceRefreshVillages} preferCurrentPage={preferCurrentPageVillages} skipOverview={skipOverviewNavigation}");
        Notify("ReadAccountSnapshotAsync started");
        if (suppressEnsureUiSync)
        {
            _suppressEnsureUiSyncDepth++;
        }

        try
        {
            // Normally we land on the village overview first for a stable page state. The post-login
            // flow passes skipOverviewNavigation=true when it just read the hero inventory and is
            // about to refresh villages from the profile anyway — that saves one dorf1 hop, since
            // ReadVillagesFromServerAsync navigates straight to the profile regardless.
            if (!skipOverviewNavigation && !IsCurrentUrlForPath(Paths.Resources))
            {
                await GotoAsync(Paths.Resources, cancellationToken);
            }

            await EnsureLoggedInAsync(cancellationToken: cancellationToken);

            IReadOnlyList<Village> villages;
            if (forceRefreshVillages)
            {
                villages = await ReadVillagesFromServerAsync(cancellationToken, restorePreviousUrl: restorePageAfterProfile);
                if (villages.Count > 0)
                {
                    _cachedVillages = villages.ToList();
                    _cachedVillagesAt = DateTimeOffset.UtcNow;
                    if (villages.Any(v => v.Population.HasValue))
                    {
                        _cachedVillagesPopulationAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            else
            {
                villages = preferCurrentPageVillages
                    ? await ReadVillagesPreferCacheAsync(cancellationToken)
                    : await ReadVillagesAsync(cancellationToken);
            }

            var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
            var activeCoordinates = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            var result = new AccountSnapshot(
                Tribe: await ReadAccountTribeAsync(cancellationToken),
                ActiveVillage: activeVillage,
                VillageCount: villages.Count,
                Villages: villages,
                ServerTimeUtc: _serverTimeUtc,
                ActiveVillageCoordX: activeCoordinates.X,
                ActiveVillageCoordY: activeCoordinates.Y);
            trace.Complete("success", $"villageCount={result.VillageCount} activeVillage={result.ActiveVillage}");
            return result;
        }
        finally
        {
            if (suppressEnsureUiSync)
            {
                _suppressEnsureUiSyncDepth--;
            }
        }
    }

public async Task<AccountAnalysisSnapshot> ReadAccountAnalysisSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Notify("ReadAccountAnalysisSnapshotAsync started");
        await GotoAsync(Paths.Resources, cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await RefreshCapitalStateForActiveVillageAsync(cancellationToken);

        var tribe = await ReadAccountTribeAsync(cancellationToken);
        var goldClubEnabled = await ReadGoldClubEnabledAsync(cancellationToken);
        var catalog = BuildingCatalogService.GetCatalogForTribe(tribe);

        return new AccountAnalysisSnapshot(
            SchemaVersion: 1,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            AccountName: _account.Name,
            ServerUrl: _config.BaseUrl.TrimEnd('/'),
            Tribe: tribe,
            GoldClubEnabled: goldClubEnabled,
            BuildingCatalog: catalog);
    }

    public async Task<bool> ReadGoldClubStatusAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        return await ReadGoldClubEnabledAsync(cancellationToken);
    }

    private async Task<(int? Gold, int? Silver)> ReadCurrencyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const hasGold = !!document.querySelector('#ajaxReplaceableGoldAmount_2, [id^="ajaxReplaceableGoldAmount_"], .ajaxReplaceableGoldAmount, .value.ajaxReplaceableGoldAmount, #gold');
                  const hasSilver = !!document.querySelector('#silver, #silverValue, [id^="ajaxReplaceableSilverAmount_"], .ajaxReplaceableSilverAmount, .value.ajaxReplaceableSilverAmount, [id*="silver" i], [class*="silver" i], font[color="#B3B3B3"], font[color="#b3b3b3"]');
                  return hasGold || hasSilver;
                }
                """,
                new PageWaitForFunctionOptions { Timeout = 2500 });
        }
        catch
        {
            // Continue with fallback polling.
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await _page.EvaluateAsync<Dictionary<string, long?>>(
                """
                () => {
                  const parseNumber = (value) => {
                    const text = (value || '')
                      .replace(/[\u202A-\u202E\u2066-\u2069]/g, '')
                      .replace(/\s+/g, ' ')
                      .trim();
                    if (!text) return null;
                    const digits = text.replace(/[^\d]/g, '');
                    if (!digits) return null;
                    const parsed = Number(digits);
                    return Number.isFinite(parsed) ? parsed : null;
                  };

                  const readFirstNumber = (selectors) => {
                    for (const selector of selectors) {
                      for (const node of document.querySelectorAll(selector)) {
                        const value =
                          parseNumber(node.getAttribute('data-value'))
                          ?? parseNumber(node.getAttribute('data-amount'))
                          ?? parseNumber(node.textContent || '')
                          ?? parseNumber(node.getAttribute('title') || '')
                          ?? parseNumber(node.getAttribute('aria-label') || '');
                        if (value !== null) return value;
                      }
                    }

                    return null;
                  };

                  const readFromHtmlPattern = (regex) => {
                    const html = document.documentElement?.innerHTML || '';
                    const match = html.match(regex);
                    if (!match || match.length < 2) return null;
                    return parseNumber(match[1] || '');
                  };

                  const readFromLabel = (labels) => {
                    const lines = (document.body?.innerText || '').split(/\n+/).map(line => line.trim()).filter(Boolean);
                    for (const line of lines) {
                      for (const label of labels) {
                        if (!new RegExp(`\\b${label}\\b`, 'i').test(line)) continue;
                        const value = parseNumber(line);
                        if (value !== null) return value;
                      }
                    }
                    return null;
                  };

                  const gold =
                    readFirstNumber([
                      '#ajaxReplaceableGoldAmount_2',
                      '[id^="ajaxReplaceableGoldAmount_"]',
                      '.ajaxReplaceableGoldAmount',
                      '.value.ajaxReplaceableGoldAmount',
                      '#gold',
                      '#gold .value',
                      '[id*="gold" i]',
                      '[class*="gold" i]'
                    ])
                    ?? readFromHtmlPattern(/id=["']ajaxReplaceableGoldAmount_[^"']*["'][^>]*>([^<]+)/i)
                    ?? readFromHtmlPattern(/class=["'][^"']*\bajaxReplaceableGoldAmount\b[^"']*["'][^>]*>([^<]+)/i)
                    ?? readFromLabel(['gold', 'guld', 'premium']);

                  const silver =
                    readFirstNumber([
                      '#silver',
                      '#silverValue',
                      '[id^="ajaxReplaceableSilverAmount_"]',
                      '.ajaxReplaceableSilverAmount',
                      '.value.ajaxReplaceableSilverAmount',
                      '#sidebarBoxActiveVillage #silver',
                      '#sidebarBoxActiveVillage .silver',
                      "font[color='#B3B3B3']",
                      "font[color='#b3b3b3']"
                    ])
                    ?? readFromHtmlPattern(/id=["']ajaxReplaceableSilverAmount_[^"']*["'][^>]*>([^<]+)/i)
                    ?? readFromHtmlPattern(/class=["'][^"']*\bajaxReplaceableSilverAmount\b[^"']*["'][^>]*>([^<]+)/i)
                    ?? readFromHtmlPattern(/<font[^>]*color=["']#b3b3b3["'][^>]*>([^<]+)/i)
                    ?? readFromLabel(['silver', 'silber']);

                  return { gold, silver };
                }
                """);

            if (raw is not null)
            {
                raw.TryGetValue("gold", out var gold);
                raw.TryGetValue("silver", out var silver);
                if (gold is not null || silver is not null)
                {
                    return MergeCurrencyWithCache(ClampLongToInt32(gold), ClampLongToInt32(silver));
                }
            }

            if (attempt == 0)
            {
                var earlyLocator = await ReadCurrencyFromLocatorsAsync(cancellationToken);
                if (earlyLocator.Gold is not null || earlyLocator.Silver is not null)
                {
                    return MergeCurrencyWithCache(earlyLocator.Gold, earlyLocator.Silver);
                }
            }

            if (attempt < 4)
            {
                await Task.Delay(Random.Shared.Next(150, 350), cancellationToken); // Random wait
            }
        }

        var locatorCurrency = await ReadCurrencyFromLocatorsAsync(cancellationToken);
        var locatorGold = locatorCurrency.Gold;
        var locatorSilver = locatorCurrency.Silver;
        if (locatorGold is not null || locatorSilver is not null)
        {
            return MergeCurrencyWithCache(locatorGold, locatorSilver);
        }

        await LogCurrencyReadFailureDiagnosticsAsync(cancellationToken);
        if (TryGetCachedCurrency(out var cached))
        {
            var ageSeconds = _cachedCurrencyAt == DateTimeOffset.MinValue
                ? "unknown"
                : Math.Max(0, (int)(DateTimeOffset.UtcNow - _cachedCurrencyAt).TotalSeconds).ToString(CultureInfo.InvariantCulture);
            Notify($"[resources:verbose] Could not detect live gold/silver values on this page. Using cached values from {ageSeconds}s ago: gold={cached.Gold?.ToString() ?? "-"} silver={cached.Silver?.ToString() ?? "-"}.");
            return cached;
        }

        Notify("Could not detect gold/silver values on this page and no cached values are available. Returning '-'.");
        return (null, null);
    }

    private async Task<(int? Gold, int? Silver)> ReadCurrencyFromLocatorsAsync(CancellationToken cancellationToken)
    {
        var gold = await ReadNumberFromSelectorsAsync(
            [
                "#ajaxReplaceableGoldAmount_2",
                "[id^='ajaxReplaceableGoldAmount_']",
                ".ajaxReplaceableGoldAmount",
                ".value.ajaxReplaceableGoldAmount",
                "#gold",
                "[id*='gold' i]"
            ],
            cancellationToken);
        var silver = await ReadNumberFromSelectorsAsync(
            [
                "#silver",
                "#silverValue",
                "[id^='ajaxReplaceableSilverAmount_']",
                ".ajaxReplaceableSilverAmount",
                ".value.ajaxReplaceableSilverAmount",
                "#sidebarBoxActiveVillage #silver",
                "#sidebarBoxActiveVillage .silver",
                "font[color='#B3B3B3']",
                "font[color='#b3b3b3']"
            ],
            cancellationToken);
        return (gold, silver);
    }

    private (int? Gold, int? Silver) MergeCurrencyWithCache(int? liveGold, int? liveSilver)
    {
        var gold = liveGold ?? _cachedGold;
        var silver = liveSilver ?? _cachedSilver;
        _cachedGold = gold;
        _cachedSilver = silver;
        _cachedCurrencyAt = DateTimeOffset.UtcNow;
        return (gold, silver);
    }

    private bool TryGetCachedCurrency(out (int? Gold, int? Silver) currency)
    {
        currency = (_cachedGold, _cachedSilver);
        return _cachedGold is not null || _cachedSilver is not null;
    }

    private async Task LogCurrencyReadFailureDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var diagnostics = await _page.EvaluateAsync<string>(
                """
                () => JSON.stringify({
                  url: window.location.href,
                  title: document.title || '',
                  bodyClass: document.body?.className || '',
                  goldCount: document.querySelectorAll('#ajaxReplaceableGoldAmount_2, [id^="ajaxReplaceableGoldAmount_"], .ajaxReplaceableGoldAmount, .value.ajaxReplaceableGoldAmount, #gold').length,
                  silverCount: document.querySelectorAll('#silver, #silverValue, [id^="ajaxReplaceableSilverAmount_"], .ajaxReplaceableSilverAmount, .value.ajaxReplaceableSilverAmount').length,
                  currencyLikeCount: document.querySelectorAll('[class*="currency" i], [id*="currency" i], [class*="gold" i], [id*="gold" i], [class*="silver" i], [id*="silver" i]').length,
                  dialogCount: document.querySelectorAll('dialog, [role="dialog"], .dialog, #dialogContent, .popup').length
                })
                """);
            Notify($"Currency read diagnostics: {diagnostics}");
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            Notify($"Currency read diagnostics skipped: {ex.Message}");
        }
    }

    private async Task<int?> ReadNumberFromSelectorsAsync(IEnumerable<string> selectors, CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var locator = _page.Locator(selector).First;
                if (await locator.CountAsync() == 0)
                {
                    continue;
                }

                var text = await locator.InnerTextAsync();
                var parsed = TravianParsing.ParseNumericTextToInt(text);
                if (parsed is not null)
                {
                    return parsed;
                }

                var title = await locator.GetAttributeAsync("title");
                parsed = TravianParsing.ParseNumericTextToInt(title);
                if (parsed is not null)
                {
                    return parsed;
                }

                var aria = await locator.GetAttributeAsync("aria-label");
                parsed = TravianParsing.ParseNumericTextToInt(aria);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch
            {
                // Try next selector.
            }
        }

        return null;
    }

    private async Task<string> ReadAccountTribeAsync(CancellationToken cancellationToken)
    {
        var cached = KnownAccountTribe;
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var previousUrl = _page.Url;
        try
        {
            if (!IsCurrentUrlForPath(Paths.PlayerProfile))
            {
                await GotoAsync(Paths.PlayerProfile, cancellationToken);
                await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            }

            var value = await DetectTribeFromCurrentPageAsync(includeVillageBuildings: false);
            if (!IsKnownTribe(value))
            {
                return "Unknown";
            }

            _accountTribe = value;
            _cachedAccountTribe = value;
            _cachedTribePlusAt = DateTimeOffset.UtcNow;
            Notify($"[tribe] account={value} source=profile");
            return value;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(previousUrl)
                && !string.Equals(_page.Url, previousUrl, StringComparison.OrdinalIgnoreCase))
            {
                await GotoAsync(previousUrl, cancellationToken);
            }
        }
    }

    private async Task CaptureAccountTribeFromCurrentProfileAsync()
    {
        var value = await DetectTribeFromCurrentPageAsync(includeVillageBuildings: false);
        if (!IsKnownTribe(value))
        {
            return;
        }

        _accountTribe = value;
        _cachedAccountTribe = value;
        _cachedTribePlusAt = DateTimeOffset.UtcNow;
        Notify($"[tribe] account={value} source=profile");
    }

    private async Task<string> ReadActiveVillageTribeAsync(CancellationToken cancellationToken)
    {
        var (cacheKey, activeVillage) = await ResolveActiveVillageTribeCacheKeyAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(cacheKey)
            && _session.VillageTribes.TryGetValue(cacheKey, out var cached)
            && IsKnownTribe(cached))
        {
            return cached;
        }

        var value = await DetectTribeFromCurrentPageAsync(includeVillageBuildings: true);
        if (!IsKnownTribe(value))
        {
            Notify($"[tribe] village='{activeVillage ?? "unknown"}' key='{cacheKey ?? "unknown"}' tribe=Unknown source=live");
            return "Unknown";
        }

        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            _session.VillageTribes[cacheKey] = value;
        }

        if (_cachedVillages is { Count: > 0 })
        {
            _cachedVillages = _cachedVillages
                .Select(village => IsSameVillageIdentity(village, cacheKey, activeVillage)
                    ? village with { Tribe = value }
                    : village)
                .ToList();
        }

        Notify($"[tribe] village='{activeVillage ?? "unknown"}' key='{cacheKey ?? "unknown"}' tribe={value} source=live");
        return value;
    }

    private async Task<(string? CacheKey, string? ActiveVillage)> ResolveActiveVillageTribeCacheKeyAsync(CancellationToken cancellationToken)
    {
        var activeVillage = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        var activeDid = await _page.EvaluateAsync<string?>(
            """
            () => document.querySelector(
              '#sidebarBoxVillageList .listEntry.village.active[data-did], ' +
              '#sidebarBoxVillagelist .listEntry.village.active[data-did], ' +
              '.listEntry.village.active[data-did]')?.getAttribute('data-did') || null
            """);
        if (int.TryParse(activeDid, out var did))
        {
            return ($"did:{did}", activeVillage);
        }

        var coords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        if (coords.Item1.HasValue && coords.Item2.HasValue)
        {
            return ($"xy:{coords.Item1.Value}|{coords.Item2.Value}", activeVillage);
        }

        var matchingVillages = _cachedVillages?
            .Where(village => VillageIdentityReconciler.IsSameName(village.Name, activeVillage))
            .ToList();
        if (matchingVillages is { Count: 1 })
        {
            var cachedDid = TravianUrls.TryParseNewdid(matchingVillages[0].Url);
            if (cachedDid.HasValue)
            {
                return ($"did:{cachedDid.Value}", activeVillage);
            }

            if (matchingVillages[0].CoordX is int x && matchingVillages[0].CoordY is int y)
            {
                return ($"xy:{x}|{y}", activeVillage);
            }
        }

        return string.IsNullOrWhiteSpace(activeVillage)
            ? (null, activeVillage)
            : ($"name:{activeVillage.Trim().ToLowerInvariant()}", activeVillage);
    }

    private static bool IsSameVillageIdentity(Village village, string? cacheKey, string? activeVillage)
    {
        if (cacheKey?.StartsWith("did:", StringComparison.OrdinalIgnoreCase) == true
            && int.TryParse(cacheKey.AsSpan(4), out var did))
        {
            return TravianUrls.TryParseNewdid(village.Url) == did;
        }

        if (cacheKey?.StartsWith("xy:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return string.Equals(cacheKey, $"xy:{village.CoordX}|{village.CoordY}", StringComparison.OrdinalIgnoreCase);
        }

        return VillageIdentityReconciler.IsSameName(village.Name, activeVillage);
    }

    internal static IReadOnlyList<Village> EnrichActiveVillageTribe(
        IReadOnlyList<Village> villages,
        string activeVillage,
        string tribe)
    {
        if (!IsKnownTribe(tribe))
        {
            return villages;
        }

        return villages
            .Select(village => VillageIdentityReconciler.IsSameName(village.Name, activeVillage)
                ? village with { Tribe = tribe }
                : village)
            .ToList();
    }

    // This parser intentionally has two modes: profile reads resolve the permanent avatar tribe,
    // while village reads prefer building-slot markers that belong to the active village.
    private async Task<string> DetectTribeFromCurrentPageAsync(bool includeVillageBuildings)
    {
        string value;
        try
        {
            value = await _page.EvaluateAsync<string>(
                """
                (includeVillageBuildings) => {
              const tribeNames = {
                1: 'Romans',
                2: 'Teutons',
                3: 'Gauls',
                4: 'Nature',
                5: 'Natars',
                6: 'Egyptians',
                7: 'Huns',
                8: 'Spartans'
              };
              const altNorm = (raw) => {
                const t = (raw || '').toLowerCase();
                if (t.startsWith('roman')) return 'Romans';
                if (t.startsWith('teuton')) return 'Teutons';
                if (t.startsWith('gaul')) return 'Gauls';
                if (t.startsWith('egypt')) return 'Egyptians';
                if (t.startsWith('hun')) return 'Huns';
                if (t.startsWith('spartan')) return 'Spartans';
                return null;
              };
              const srcNorm = (raw) => {
                const m = (raw || '').match(/(roman|teuton|gaul|egypt|hun|spartan)/i);
                return m ? altNorm(m[1]) : null;
              };

              // Most reliable on official Travian (T4.6): building slots and their images carry
              // the village tribe as a CSS class token, e.g. "buildingSlot a19 g0 aid19 gaul"
              // and "building g23 gaul". Use getAttribute so SVG className objects are handled.
              const classNorm = (raw) => {
                const c = (raw || '').toLowerCase();
                if (/\bgaul\b/.test(c)) return 'Gauls';
                if (/\bteuton\b/.test(c)) return 'Teutons';
                if (/\broman\b/.test(c)) return 'Romans';
                if (/\begyptian\b/.test(c)) return 'Egyptians';
                if (/\bhun\b/.test(c)) return 'Huns';
                if (/\bspartan\b/.test(c)) return 'Spartans';
                return null;
              };
              if (includeVillageBuildings) {
                for (const node of document.querySelectorAll('div.buildingSlot, img.building')) {
                  const fromClass = classNorm(node.getAttribute('class'));
                  if (fromClass) return fromClass;
                }
              }

              // Primary: tribe icon img (works directly from dorf1/dorf2).
                for (const img of document.querySelectorAll('img.nationBig, img[src*="/tribes/"], img[src*="nation"], img[alt]')) {
                  const fromAlt = altNorm(img.getAttribute('alt'));
                  if (fromAlt) return fromAlt;
                  const fromSrc = srcNorm(img.getAttribute('src'));
                  if (fromSrc) return fromSrc;
                }

              // Note: a bare 'body' catch-all was removed — scanning the whole page text for a
              // tribe word false-matched a stray "roman" on official Travian and got cached.
              // Returning 'Unknown' (not cached, retried on dorf2) is safer than a wrong tribe.
              if (!includeVillageBuildings) {
                for (const row of document.querySelectorAll('tr')) {
                  const label = (row.querySelector('th, dt, .label')?.textContent || '').trim();
                  if (!/^(tribe|nation)$/i.test(label)) continue;
                  const valueNode = row.querySelector('td, dd, .value');
                  const text = `${valueNode?.getAttribute('class') || ''} ${valueNode?.textContent || ''}`;
                  const fromClass = classNorm(text);
                  if (fromClass) return fromClass;
                  const tribeMatch = text.match(/tribe[^0-9]*(\d+)/i) || text.match(/tribe(\d+)/i);
                  if (tribeMatch && tribeNames[Number(tribeMatch[1])]) return tribeNames[Number(tribeMatch[1])];
                }
              }

              const selectors = includeVillageBuildings
                ? [
                    '[class*="tribe" i]',
                    '[id*="tribe" i]',
                    '#sidebarBoxActiveVillage'
                  ]
                : [
                    '#content [class*="tribe" i]',
                    '#content [id*="tribe" i]',
                    'main [class*="tribe" i]',
                    '.playerProfile [class*="tribe" i]',
                    '.playerInfo'
                  ];

              for (const selector of selectors) {
                const element = document.querySelector(selector);
                if (!element) continue;
                const text = `${element.className || ''} ${element.getAttribute('title') || ''} ${element.textContent || ''}`.toLowerCase();
                if (text.includes('roman')) return 'Romans';
                if (text.includes('teuton')) return 'Teutons';
                if (text.includes('gaul')) return 'Gauls';
                if (text.includes('egypt')) return 'Egyptians';
                if (text.includes('hun')) return 'Huns';
                if (text.includes('spartan')) return 'Spartans';

                const tribeMatch = text.match(/tribe[^0-9]*(\d+)/i) || text.match(/tribe(\d+)/i);
                if (tribeMatch && tribeNames[Number(tribeMatch[1])]) return tribeNames[Number(tribeMatch[1])];
              }

              return 'Unknown';
            }
            """,
            includeVillageBuildings);
        }
        catch (PlaywrightException ex) when (!BrowserFailureClassifier.IsTargetCrash(ex))
        {
            Notify($"[tribe] live detection failed; continuing with Unknown: {ex.Message}");
            return "Unknown";
        }

        if (!IsKnownTribe(value))
        {
            return "Unknown";
        }

        return value;
    }

    private async Task<bool> ReadGoldClubEnabledAsync(CancellationToken cancellationToken)
    {
        if (_cachedGoldClubEnabled == true)
        {
            return true;
        }

        var enabled = await _page.EvaluateAsync<bool>(
            """
            () => {
              // Official Travian (T4.6) embeds a GraphQL bootstrap state blob in the page that carries
              // the account's gold features, e.g. "goldFeatures":{"travianPlus":{"isActive":true},"goldClub":true}.
              // This is the reliable signal: read the boolean directly (the unquoted "goldClub" in the
              // query definition has no colon+boolean, so it never matches).
              const html = document.documentElement ? document.documentElement.innerHTML : '';
              const match = html.match(/"goldClub"\s*:\s*(true|false)/i);
              if (match) return match[1].toLowerCase() === 'true';

              // Fallback for variants without the GraphQL blob: the Gold Club master-build
              // button, or a builder marker in the village-list sidebar.
              if (document.querySelector('#buttonBuild')) return true;
              const sidebar = document.querySelector('#sidebarBoxVillagelist');
              return /buildOff|buildOn|builder=On/.test(sidebar?.innerHTML || '');
            }
            """);

        if (enabled)
        {
            _cachedGoldClubEnabled = true;
        }

        return enabled;
    }

    private static bool IsKnownTribe(string? tribe)
        => !string.IsNullOrWhiteSpace(tribe)
           && !string.Equals(tribe, "Unknown", StringComparison.OrdinalIgnoreCase);

}
