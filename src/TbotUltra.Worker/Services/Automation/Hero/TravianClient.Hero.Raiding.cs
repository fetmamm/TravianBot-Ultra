using System.Text.Json;
using System.Globalization;
using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

internal sealed record HeroRaidingReadiness(
    int VillageX,
    int VillageY,
    int AttackStrength,
    int HpPercent,
    bool Mounted,
    bool CagesEquipped,
    int EquippedCages);

internal sealed record HeroRaidingLiveOutcome(bool StateKnown, bool Home, bool Dead, int? HpPercent);
internal enum HeroRaidDispatchState { Unknown, Home, Running, Dead }

public sealed partial class TravianClient
{
    /// <summary>Reads the global hero widget and top-bar HP without navigating away from the current page.</summary>
    internal async Task<HeroRaidingLiveOutcome> ReadHeroRaidingLiveOutcomeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await _page.EvaluateAsync<string?>(
            """
            () => {
              const root = document.querySelector('.heroStatus');
              if (!root || document.querySelectorAll('.heroStatus').length !== 1) return null;
              const home = !!root.querySelector('i.heroHome');
              const running = !!root.querySelector('i.heroRunning');
              const dead = !!root.querySelector('i.heroDead, i.heroReviving');
              return JSON.stringify({ home, running, dead });
            }
            """);
        if (string.IsNullOrWhiteSpace(state))
        {
            Notify("[hero-raiding] global hero status widget was unavailable during return verification.");
            return new HeroRaidingLiveOutcome(false, false, false, null);
        }

        using var document = JsonDocument.Parse(state);
        var root = document.RootElement;
        var home = root.GetProperty("home").GetBoolean();
        var running = root.GetProperty("running").GetBoolean();
        var dead = root.GetProperty("dead").GetBoolean();
        var known = (home ? 1 : 0) + (running ? 1 : 0) + (dead ? 1 : 0) == 1;
        var hp = home && known ? await ReadHeroHpFromCurrentPageAsync(cancellationToken) : null;
        Notify($"[hero-raiding] live return state: known={known}, home={home}, running={running}, dead={dead}, HP={(hp?.ToString() ?? "unknown")}%. ");
        return new HeroRaidingLiveOutcome(known, home && known, dead && known, hp);
    }

    internal async Task<int> ReadHeroRaidingAvailableWaitSecondsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await ReadHeroStatusAsync(cancellationToken);
        var remaining = status.SecondsUntilReturn ?? status.ReviveRemainingSeconds;
        var wait = remaining is > 0 ? Math.Clamp(remaining.Value + 15, 30, 86400) : 300;
        Notify($"[hero-raiding] hero unavailable; next readiness check in {wait}s (live return/revive timer={(remaining?.ToString() ?? "unknown")}).");
        return wait;
    }

    /// <summary>Reads live Official attributes and equipment; never relies on cached point allocation.</summary>
    internal async Task<HeroRaidingReadiness?> ReadHeroRaidingReadinessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        await EnsurePageForReadAsync(Paths.HeroAttributes, "hero raiding readiness", cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        if (!await IsHeroHomeOnCurrentPageAsync(cancellationToken))
        {
            Notify("[hero-raiding] hero is not positively confirmed home; discovery deferred.");
            return null;
        }

        var home = await ReadHeroHomeVillageInfoAsync(cancellationToken);
        var villageCoordinates = (home.X, home.Y);
        if (!VillageIdentityReconciler.HasCoordinates(villageCoordinates) && !string.IsNullOrWhiteSpace(home.Name))
        {
            // Official hero-state links can contain only a map tile id (?d=), not x/y.
            // A unique live sidebar name is sufficient; duplicate names require the
            // hero-state tile link and its own map details instead of a name guess.
            var sidebarVillages = await ReadVillagesFromCurrentPageAsync(cancellationToken);
            villageCoordinates = ResolveHeroRaidingVillage(home.Name, sidebarVillages);
            if (VillageIdentityReconciler.HasCoordinates(villageCoordinates))
                Notify($"[hero-raiding] hero village '{home.Name}' coordinates verified from live sidebar: ({villageCoordinates.X}|{villageCoordinates.Y}).");
            else
                Notify($"[hero-raiding] hero village '{home.Name}' could not be uniquely resolved from live sidebar; checking its exact map link.");
        }
        var hp = await ReadHeroHpFromCurrentPageAsync(cancellationToken);
        var strengthText = await _page.EvaluateAsync<string?>(
            """
            () => {
              const input = document.querySelector('.heroAttributes input[name="power"]');
              const total = input?.closest('.inputRatio')?.querySelector('.denominator .value');
              return total?.textContent?.trim() || null;
            }
            """);
        var strengthDigits = new string((strengthText ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!VillageIdentityReconciler.HasCoordinates(villageCoordinates) && !home.Away && !string.IsNullOrWhiteSpace(home.Name))
            villageCoordinates = await ReadHeroRaidingHomeCoordinatesFromMapAsync(home.Name, cancellationToken);
        if (home.Away || !VillageIdentityReconciler.HasCoordinates(villageCoordinates) || hp is not > 0
            || !int.TryParse(strengthDigits, out var strength) || strength <= 0)
            throw new InvalidOperationException("Hero raiding could not verify the hero's live home village, HP, or total fighting strength.");

        await EnsurePageForReadAsync(Paths.HeroInventory, "hero raiding equipment", cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        var equipmentJson = await _page.EvaluateAsync<string?>(
            """
            () => {
              const root = document.querySelector('.currentHeroEquipment .equipmentSlots');
              if (!root) return null;
              const bag = root.querySelector('.heroItem[data-slot="bag"]');
              const horse = root.querySelector('.heroItem[data-slot="horse"]');
              if (!bag || !horse) return null;
              const cages = !!bag.querySelector('.item.item114');
              const countText = bag.querySelector('.count')?.textContent || '';
              const count = Number(countText.replace(/[^0-9]/g, '')) || 0;
              return JSON.stringify({ mounted: !horse.classList.contains('empty'), cages, count: cages ? Math.max(1, count) : 0 });
            }
            """);
        if (string.IsNullOrWhiteSpace(equipmentJson))
            throw new InvalidOperationException("Hero raiding could not verify the equipped horse and bag slots.");
        var equipment = JsonSerializer.Deserialize<HeroRaidingEquipmentRead>(equipmentJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (equipment is null || !await IsHeroHomeOnCurrentPageAsync(cancellationToken))
            throw new InvalidOperationException("Hero raiding equipment or home status changed while reading readiness.");

        Notify($"[hero-raiding] hero readiness: village=({villageCoordinates.X}|{villageCoordinates.Y}), strength={strength}, HP={hp}%, mounted={equipment.Mounted}, cages={equipment.Count}.");
        return new HeroRaidingReadiness(villageCoordinates.X!.Value, villageCoordinates.Y!.Value, strength, hp.Value,
            equipment.Mounted, equipment.Cages, equipment.Count);
    }

    internal static (int? X, int? Y) ResolveHeroRaidingVillage(
        string heroVillageName,
        IReadOnlyList<Village> sidebarVillages)
    {
        var matches = sidebarVillages
            .Where(village => VillageIdentityReconciler.IsSameName(village.Name, heroVillageName))
            .Take(2)
            .ToList();
        return matches.Count == 1 && matches[0].CoordX.HasValue && matches[0].CoordY.HasValue
            ? (matches[0].CoordX, matches[0].CoordY)
            : (null, null);
    }

    private async Task<(int? X, int? Y)> ReadHeroRaidingHomeCoordinatesFromMapAsync(
        string heroVillageName, CancellationToken cancellationToken)
    {
        var link = _page.Locator(".attributeBox .heroState a[href*='karte.php?d=']");
        if (await link.CountAsync() != 1)
            throw new InvalidOperationException("Hero raiding could not identify one exact hero-home map link in Attributes.");

        var href = await link.GetAttributeAsync("href");
        if (!Uri.TryCreate(new Uri(_page.Url), href, out var target)
            || !string.Equals(target.Host, new Uri(_page.Url).Host, StringComparison.OrdinalIgnoreCase)
            || !target.AbsolutePath.EndsWith("/karte.php", StringComparison.OrdinalIgnoreCase)
            || !ParseQueryParameters(target.Query).TryGetValue("d", out var tileId)
            || !int.TryParse(tileId, out var parsedTileId) || parsedTileId <= 0)
            throw new InvalidOperationException("Hero raiding hero-home map link has no valid tile identity.");

        await ClickLocatorAsync(link, "hero-raiding-open-home-map", cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        var current = new Uri(_page.Url);
        if (!current.AbsolutePath.EndsWith("/karte.php", StringComparison.OrdinalIgnoreCase)
            || !ParseQueryParameters(current.Query).TryGetValue("d", out var currentTileId)
            || currentTileId != tileId)
            throw new InvalidOperationException($"Hero raiding map did not open the expected home tile d={tileId}; current page: '{_page.Url}'.");

        await _page.Locator("#tileDetails.village .titleInHeader").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = _config.TimeoutMs })
            .WaitAsync(cancellationToken);
        var raw = await _page.EvaluateAsync<string?>(
            """
            () => {
              const details = document.querySelector('#tileDetails.village');
              const title = details?.querySelector('.titleInHeader');
              const center = details?.querySelector('a[data-centermap="true"]');
              if (!title || !center) return null;
              const name = Array.from(title.childNodes).filter(n => n.nodeType === Node.TEXT_NODE)
                .map(n => n.textContent || '').join(' ').replace(/\s+/g, ' ').trim();
              const url = new URL(center.getAttribute('href') || '', location.href);
              if (!url.searchParams.has('x') || !url.searchParams.has('y')) return null;
              const x = Number(url.searchParams.get('x'));
              const y = Number(url.searchParams.get('y'));
              if (!Number.isInteger(x) || !Number.isInteger(y)) return null;
              const displayed = (value) => (value || '').replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069\ufeff]/g, '')
                .replace(/\u2212/g, '-').replace(/[^\d-]/g, '');
              const displayX = title.querySelector('.coordinateX');
              const displayY = title.querySelector('.coordinateY');
              if (!displayX || !displayY) return null;
              const textX = displayed(displayX.textContent);
              const textY = displayed(displayY.textContent);
              if (!/^-?\d+$/.test(textX) || !/^-?\d+$/.test(textY)) return null;
              const shownX = Number(textX);
              const shownY = Number(textY);
              if (x !== shownX || y !== shownY) return null;
              return JSON.stringify({ name, x, y });
            }
            """);
        var coordinates = ParseHeroRaidingMapVillage(raw, heroVillageName);
        if (!VillageIdentityReconciler.HasCoordinates(coordinates))
            throw new InvalidOperationException($"Hero raiding could not verify the name and coordinates of home tile d={tileId}.");
        Notify($"[hero-raiding] hero village '{heroVillageName}' coordinates verified from map tile d={tileId}: ({coordinates.X}|{coordinates.Y}).");
        return coordinates;
    }

    internal static (int? X, int? Y) ParseHeroRaidingMapVillage(string? raw, string expectedName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || !VillageIdentityReconciler.IsSameName(name.GetString(), expectedName)
            || !root.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var parsedX)
            || !root.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out var parsedY))
            return (null, null);
        return (parsedX, parsedY);
    }

    /// <summary>Uses Official's Cage popup to move all equipped cages, then verifies the equipped bag is empty.</summary>
    internal async Task<bool> TryUnequipHeroRaidingCagesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsurePageForReadAsync(Paths.HeroInventory, "hero raiding cage removal", cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        var equipment = _page.Locator(".currentHeroEquipment .heroItem[data-slot='bag']");
        if (await equipment.CountAsync() != 1)
        {
            Notify("[hero-raiding] cage removal stopped: equipped bag slot could not be uniquely verified.");
            return false;
        }

        var cages = equipment.Locator(".item.item114");
        if (await cages.CountAsync() == 0)
        {
            Notify("[hero-raiding] cages are already unequipped.");
            return true;
        }

        var source = _page.Locator(".currentHeroEquipment .heroItem[data-slot='bag']:has(.item.item114)");
        if (await source.CountAsync() != 1)
        {
            Notify("[hero-raiding] cage removal stopped: equipped cages could not be uniquely verified.");
            return false;
        }
        var countText = await source.Locator(".count").TextContentAsync();
        var countDigits = new string((countText ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!int.TryParse(countDigits, out var count) || count <= 0)
        {
            Notify("[hero-raiding] cage removal stopped: equipped cage count was unavailable.");
            return false;
        }

        Notify($"[hero-raiding] opening equipped Cage popup to move {count} cage(s) to inventory.");
        await DelayBeforeClickAsync(cancellationToken, "hero raiding: open equipped cages");
        await ClickLocatorAsync(source, "hero-raiding-open-cages", cancellationToken);
        var popup = _page.Locator(".heroConsumablesPopup");
        try
        {
            await popup.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000,
            });
        }
        catch (PlaywrightException ex)
        {
            Notify($"[hero-raiding] Cage popup did not open: {ex.Message}");
            return false;
        }
        var popupTitle = await popup.Locator("#dialogContent h3").TextContentAsync();
        var amount = popup.Locator("#consumableHeroItem input[inputmode='numeric']");
        var move = popup.Locator(".buttonsWrapper button:has-text('Move')");
        if (!string.Equals(popupTitle?.Trim(), "Cage", StringComparison.OrdinalIgnoreCase)
            || await amount.CountAsync() != 1 || await move.CountAsync() != 1)
        {
            Notify("[hero-raiding] Cage popup controls did not match the verified Official form.");
            return false;
        }
        if (!int.TryParse(await amount.InputValueAsync(), out var popupCount) || popupCount != count)
            await TypeHumanlyAsync(amount, count.ToString(CultureInfo.InvariantCulture), cancellationToken);
        if (!int.TryParse(await amount.InputValueAsync(), out popupCount) || popupCount != count)
        {
            Notify("[hero-raiding] Cage popup could not be set to move all equipped cages.");
            return false;
        }

        await DelayBeforeClickAsync(cancellationToken, "hero raiding: move cages");
        try
        {
            // One state-changing click only. A timeout can occur after the server accepted it;
            // the observed inventory state below decides success without blindly retrying.
            await ClickLocatorAsync(move, "hero-raiding-move-cages", cancellationToken);
        }
        catch (PlaywrightException ex)
        {
            Notify($"[hero-raiding] Cage Move did not complete cleanly; checking live equipment: {ex.Message}");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verified = await _page.EvaluateAsync<bool>(
                """
                () => {
                  const bag = document.querySelector('.currentHeroEquipment .heroItem[data-slot="bag"]');
                  return !!bag && bag.classList.contains('empty')
                    && !bag.querySelector('.item.item114');
                }
                """);
            if (verified)
            {
                Notify("[hero-raiding] equipped bag is empty after Cage Move; cages will remain off.");
                return true;
            }

            if (attempt < 4) await Task.Delay(400, cancellationToken);
        }

        Notify("[hero-raiding] cage removal could not be verified; raid dispatch is blocked.");
        return false;
    }

    /// <summary>
    /// Prepares, but does not submit, an exact hero-only Raid form from the hero's verified village.
    /// The first and final Send actions remain separate one-shot operations.
    /// </summary>
    internal async Task PrepareHeroRaidingSendFormAsync(
        HeroRaidingReadiness readiness,
        int targetX,
        int targetY,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetX is < -200 or > 200 || targetY is < -200 or > 200
            || targetX == readiness.VillageX && targetY == readiness.VillageY)
            throw new ArgumentOutOfRangeException(nameof(targetX), "Hero raid target coordinates are invalid.");

        if (!await TryUnequipHeroRaidingCagesAsync(cancellationToken))
            throw new InvalidOperationException("Hero raid stopped because cage removal could not be verified.");
        var currentHp = await ReadHeroHpFromCurrentPageAsync(cancellationToken);
        if (currentHp is null || currentHp < readiness.HpPercent
            || !await IsHeroHomeOnCurrentPageAsync(cancellationToken))
            throw new InvalidOperationException("Hero raid stopped because live HP or home status changed after target selection.");

        var villages = await ReadVillagesFromCurrentPageAsync(cancellationToken);
        var source = villages.SingleOrDefault(village =>
            village.CoordX == readiness.VillageX && village.CoordY == readiness.VillageY);
        if (source is null || string.IsNullOrWhiteSpace(source.Url))
            throw new InvalidOperationException("Hero raid source village could not be uniquely verified in the live sidebar.");

        await SwitchToVillageByIdentityAsync(source.Name, source.Url,
            $"xy:{readiness.VillageX}|{readiness.VillageY}", cancellationToken);
        var activeCoordinates = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        if (!VillageIdentityReconciler.SameCoordinates(activeCoordinates,
            (readiness.VillageX, readiness.VillageY)))
            throw new InvalidOperationException("Hero raid source village changed before the Rally Point form opened.");

        await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: false);
        activeCoordinates = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        if (!VillageIdentityReconciler.SameCoordinates(activeCoordinates,
            (readiness.VillageX, readiness.VillageY))
            || !await IsHeroHomeOnCurrentPageAsync(cancellationToken))
            throw new InvalidOperationException("Hero raid stopped: the source village or hero-home state changed at Rally Point.");

        var heroInput = _page.Locator("#troops input[name='troop[t11]']");
        if (await heroInput.CountAsync() != 1 || await heroInput.IsDisabledAsync()
            || await ReadAvailableTroopCountAsync("t11", cancellationToken) != 1)
            throw new InvalidOperationException("Hero raid stopped: exactly one available hero was not verified in the troop form.");

        var xInput = _page.Locator("#xCoordInput");
        var yInput = _page.Locator("#yCoordInput");
        var raidRadio = _page.Locator("input[type='radio'][name='eventType'][value='4']");
        if (await xInput.CountAsync() != 1 || await yInput.CountAsync() != 1
            || await raidRadio.CountAsync() != 1 || await raidRadio.IsDisabledAsync())
            throw new InvalidOperationException("Hero raid stopped: exact coordinate or Raid controls were not available.");

        await TypeHumanlyAsync(xInput, targetX.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await TypeHumanlyAsync(yInput, targetY.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await TypeHumanlyAsync(heroInput, "1", cancellationToken);
        await DelayBeforeClickAsync(cancellationToken, "hero raiding: select Raid");
        await ClickLocatorAsync(raidRadio, "hero-raiding-select-raid", cancellationToken);

        var verified = await _page.EvaluateAsync<bool>(
            """
            ({x, y}) => {
              const troopInputs = Array.from(document.querySelectorAll('#troops input[name^="troop["]'));
              const hero = troopInputs.filter(input => input.name === 'troop[t11]');
              const otherTroops = troopInputs.filter(input => input.name !== 'troop[t11]');
              return troopInputs.length === 11
                && hero.length === 1 && !hero[0].disabled && hero[0].value.trim() === '1'
                && otherTroops.every(input => !input.value.trim() || Number(input.value) === 0)
                && document.querySelector('#xCoordInput')?.value.trim() === String(x)
                && document.querySelector('#yCoordInput')?.value.trim() === String(y)
                && document.querySelector('input[name="eventType"][value="4"]')?.checked === true;
            }
            """,
            new { x = targetX, y = targetY });
        if (!verified)
            throw new InvalidOperationException("Hero raid stopped: the live form did not verify as hero-only Raid to the selected oasis.");

        Notify($"[hero-raiding] hero-only Raid form verified from ({readiness.VillageX}|{readiness.VillageY}) to ({targetX}|{targetY}); no Send action clicked.");
    }

    /// <summary>
    /// Submits the prepared form once, verifies the separate confirmation page, and confirms once.
    /// The caller must persist an unverified outcome before the final click: a lost response or
    /// process exit must never cause a second raid to be sent blindly.
    /// </summary>
    internal async Task<bool> SendHeroRaidAsync(
        HeroRaidingReadiness readiness,
        int targetX,
        int targetY,
        Func<DateTimeOffset, Task> persistPendingOutcome,
        CancellationToken cancellationToken)
    {
        var sourceVillages = await ReadVillagesFromCurrentPageAsync(cancellationToken);
        var source = sourceVillages.SingleOrDefault(village =>
            village.CoordX == readiness.VillageX && village.CoordY == readiness.VillageY);
        var sourceVillageId = TravianUrls.TryParseNewdid(source?.Url);
        if (sourceVillageId is null)
            throw new InvalidOperationException("Hero raid source village ID could not be verified before Send.");

        var firstSend = _page.Locator("button#ok[type='submit'][name='ok']");
        if (await firstSend.CountAsync() != 1)
            throw new InvalidOperationException("Hero raid first Send button was not uniquely available.");

        Notify($"[hero-raiding] submitting first Send for hero-only Raid to ({targetX}|{targetY}).");
        await DelayBeforeClickAsync(cancellationToken, "hero raiding: first Send");
        await ClickLocatorAsync(firstSend, "hero-raiding-first-send", cancellationToken);
        if (!await WaitForSendTroopsConfirmationPageAsync(cancellationToken))
            throw new InvalidOperationException("Hero raid confirmation page did not load after first Send; final Confirm was not clicked.");

        var confirmationVerified = await _page.EvaluateAsync<bool>(
            """
            ({sourceVillageId, x, y}) => {
              const form = document.querySelector('form#troopSendForm');
              if (!form || document.querySelectorAll('form#troopSendForm').length !== 1) return false;
              const field = name => form.querySelector(`input[name="${name}"]`)?.value?.trim();
              const troopFields = Array.from(form.querySelectorAll('input[name^="troops[0][t"]'));
              const button = form.querySelectorAll('button#confirmSendTroops.rallyPointConfirm[name="confirmSendTroops"]');
              const player = form.querySelector('#short_info tr:nth-child(2) td strong')?.textContent?.trim();
              const headline = form.querySelector('.troop_details .troopHeadline')?.textContent?.trim();
              return field('eventType') === '4'
                && field('x') === String(x) && field('y') === String(y)
                && field('troops[0][villageId]') === String(sourceVillageId)
                && troopFields.length === 11
                && troopFields.every(input => input.name === 'troops[0][t11]'
                    ? input.value.trim() === '1' : input.value.trim() === '0')
                && player === 'Nature'
                && headline === 'Raid unoccupied oasis'
                && button.length === 1 && !button[0].disabled;
            }
            """,
            new { sourceVillageId, x = targetX, y = targetY });
        if (!confirmationVerified)
            throw new InvalidOperationException("Hero raid confirmation did not match the exact hero, source, target, Nature player and Raid mode; final Confirm was not clicked.");

        var oneWay = await ReadTroopEvasionTravelTimeAsync(cancellationToken);
        if (oneWay is null || oneWay <= TimeSpan.Zero || oneWay > TimeSpan.FromDays(3))
            throw new InvalidOperationException("Hero raid travel duration could not be verified on the confirmation page; final Confirm was not clicked.");
        var finalHp = await ReadHeroHpFromCurrentPageAsync(cancellationToken);
        if (finalHp is null || finalHp < readiness.HpPercent
            || !await IsHeroHomeOnCurrentPageAsync(cancellationToken))
            throw new InvalidOperationException("Hero raid HP or home status changed on the confirmation page; final Confirm was not clicked.");
        var expectedHomeUtc = CurrentTravianServerTimeUtc().Add(oneWay.Value + oneWay.Value);

        // Persist before the irreversible action. If clicking becomes ambiguous, the next run
        // must investigate the pending outcome instead of dispatching another raid.
        await persistPendingOutcome(expectedHomeUtc);
        var finalConfirm = _page.Locator("button#confirmSendTroops.rallyPointConfirm[name='confirmSendTroops']");
        Notify($"[hero-raiding] confirmation verified; clicking final Confirm once for ({targetX}|{targetY}).");
        await DelayBeforeClickAsync(cancellationToken, "hero raiding: final Confirm");
        try
        {
            await ClickLocatorAsync(finalConfirm, "hero-raiding-final-confirm", cancellationToken);
        }
        catch (PlaywrightException ex)
        {
            // The click can have reached Travian before Playwright loses the navigating page.
            // Never click Confirm again; observe the global hero widget instead.
            Notify($"[hero-raiding] final Confirm response was interrupted; checking hero status without resending: {ex.Message}");
        }

        var verified = await VerifyHeroRaidDispatchAsync(
            ReadHeroRaidDispatchStateFromWidgetAsync, Task.Delay, Notify, cancellationToken);
        Notify(verified
            ? $"[hero-raiding] hero status changed from verified home to running after final Confirm for ({targetX}|{targetY}); combat outcome remains pending."
            : $"[hero-raiding] hero did not become running after final Confirm for ({targetX}|{targetY}); send outcome is unknown and remains pending.");
        return verified;
    }

    private async Task<HeroRaidDispatchState> ReadHeroRaidDispatchStateFromWidgetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await _page.EvaluateAsync<string?>(
            """
            () => {
              const widgets = document.querySelectorAll('.heroStatus');
              if (widgets.length !== 1) return null;
              const widget = widgets[0];
              const running = !!widget.querySelector('i.heroRunning');
              const home = !!widget.querySelector('i.heroHome');
              const dead = !!widget.querySelector('i.heroDead, i.heroReviving');
              if (Number(running) + Number(home) + Number(dead) !== 1) return null;
              return running ? 'Running' : home ? 'Home' : 'Dead';
            }
            """);
        return Enum.TryParse<HeroRaidDispatchState>(state, out var parsed) ? parsed : HeroRaidDispatchState.Unknown;
    }

    internal static async Task<bool> VerifyHeroRaidDispatchAsync(
        Func<CancellationToken, Task<HeroRaidDispatchState>> readState,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        // The exact hero-only Raid was checked on the confirmation page, and the hero was
        // positively home immediately before the one-shot Confirm. A Running transition
        // is sufficient dispatch evidence without scanning thousands of Rally Point rows.
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var state = await readState(cancellationToken);
                if (state == HeroRaidDispatchState.Running) return true;
                if (state == HeroRaidDispatchState.Dead)
                {
                    log("[hero-raiding] hero status became dead during send verification.");
                    return false;
                }
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                log($"[hero-raiding] hero widget read crossed a navigation ({attempt}/12); retrying without another Confirm click.");
            }
            if (attempt < 12) await delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return false;
    }

    private sealed record HeroRaidingEquipmentRead(bool Mounted, bool Cages, int Count);
}
