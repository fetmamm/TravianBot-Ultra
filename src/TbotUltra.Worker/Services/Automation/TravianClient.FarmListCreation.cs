using Microsoft.Playwright;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int OfficialFarmListCountLimit = 100;

    public async Task<FarmListCreateBatchResult> CreateFarmListsAsync(
        FarmListCreateRequest request,
        IProgress<FarmListCreateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        if (_config.IsPrivateServer)
        {
            throw new InvalidOperationException("Create Farmlists is currently available on official servers only.");
        }

        var names = request.Names
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException("At least one farm list name is required.");
        }

        if (names.Any(name => name.Length > 30))
        {
            throw new InvalidOperationException("Farm list names can contain at most 30 characters.");
        }

        var duplicateRequestName = names
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateRequestName is not null)
        {
            throw new InvalidOperationException($"Farm list name '{duplicateRequestName}' is entered more than once.");
        }

        var troopIndex = TroopCatalog.ResolveTroopIndex(request.TroopType);
        if (troopIndex is null || request.TroopCount <= 0)
        {
            throw new InvalidOperationException("Select one troop type and enter a troop count greater than 0.");
        }

        progress?.Report(new FarmListCreateProgress("Analyzing farmlists", 0, names.Count));
        await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        var existingNames = await ReadOfficialFarmListNamesAsync(cancellationToken);
        var existingListCount = await _page.EvaluateAsync<int>(
            "() => document.querySelectorAll('#rallyPointFarmList .farmListWrapper').length");
        if (existingListCount + names.Count > OfficialFarmListCountLimit)
        {
            throw new InvalidOperationException(
                $"Cannot create {names.Count} farm lists. The account has {existingListCount}/100 lists.");
        }

        var existingSet = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingRequestedName = names.FirstOrDefault(existingSet.Contains);
        if (existingRequestedName is not null)
        {
            throw new InvalidOperationException($"Farm list '{existingRequestedName}' already exists.");
        }

        Notify(
            $"[farm-list-create] starting: requested={names.Count}, existing={existingListCount}, " +
            $"village='{request.VillageName}', troop={request.TroopCount} {request.TroopType}.");
        var created = new List<string>();
        for (var index = 0; index < names.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = names[index];
            progress?.Report(new FarmListCreateProgress("Creating farmlists", index, names.Count, name));
            Notify($"[farm-list-create] [{index + 1}/{names.Count}] creating '{name}'.");

            var verified = false;
            for (var attempt = 1; attempt <= 2 && !verified; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await OpenOfficialCreateFarmListDialogAsync(cancellationToken);
                await FillAndSubmitOfficialCreateFarmListAsync(
                    name,
                    request.VillageName,
                    request.VillageId,
                    troopIndex.Value,
                    request.TroopCount,
                    cancellationToken);

                verified = await WaitForOfficialFarmListNameAsync(name, cancellationToken);
                if (!verified)
                {
                    Notify(
                        $"[farm-list-create] '{name}' was not visible after Save; refreshing " +
                        $"({attempt}/2).");
                    await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                    await WaitForPageReadyAsync(cancellationToken);
                    await WaitForOfficialFarmListRenderAsync(cancellationToken);
                    verified = await HasOfficialFarmListNameAsync(name, cancellationToken);
                }
            }

            if (!verified)
            {
                throw new InvalidOperationException($"Farm list '{name}' was not visible after creation and refresh.");
            }

            created.Add(name);
            existingSet.Add(name);
            progress?.Report(new FarmListCreateProgress("Creating farmlists", index + 1, names.Count, name));
            Notify($"[farm-list-create] [{index + 1}/{names.Count}] verified '{name}'.");
        }

        return new FarmListCreateBatchResult(names.Count, created.Count, created);
    }

    private async Task<IReadOnlyList<string>> ReadOfficialFarmListNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<string[]>(
            """
            () => Array.from(document.querySelectorAll('#rallyPointFarmList .farmListWrapper .farmListName .name'))
              .map(node => (node.textContent || '').replace(/\s+/g, ' ').trim())
              .filter(Boolean)
            """);
    }

    private async Task OpenOfficialCreateFarmListDialogAsync(CancellationToken cancellationToken)
    {
        if (!await IsFarmListPageAsync(cancellationToken))
        {
            await EnsureRallyPointAndOpenFarmListPageAsync(cancellationToken);
        }

        var clicked = await _page.EvaluateAsync<bool>(
            """
            () => {
              const button = document.querySelector(
                '#stickyPin button.createFarmList, #rallyPointFarmList button.createFarmList, button.createFarmList');
              if (!button) return false;
              button.click();
              return true;
            }
            """);
        if (!clicked)
        {
            throw new InvalidOperationException("Could not find the Official Create farm list button.");
        }

        try
        {
            await _page.WaitForFunctionAsync(
                "() => !!document.querySelector('#createFarmListForm')",
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Create farm list dialog did not render.");
        }
    }

    private async Task FillAndSubmitOfficialCreateFarmListAsync(
        string name,
        string villageName,
        string? villageId,
        int troopIndex,
        int troopCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filled = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const form = document.querySelector('#createFarmListForm');
              if (!form) return false;
              const setValue = (input, value) => {
                if (!input) return false;
                input.focus();
                input.value = String(value);
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              };

              const nameInput = form.querySelector('input[name="listName"]');
              const villageSelect = form.querySelector('select[name="villageId"]');
              if (!setValue(nameInput, args.name) || !villageSelect) return false;

              const normalize = value => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const villageOption = Array.from(villageSelect.options || []).find(option =>
                (args.villageId && option.value === String(args.villageId))
                || normalize(option.textContent) === normalize(args.villageName));
              if (!villageOption) return false;
              villageSelect.value = villageOption.value;
              villageSelect.dispatchEvent(new Event('input', { bubbles: true }));
              villageSelect.dispatchEvent(new Event('change', { bubbles: true }));

              for (const input of form.querySelectorAll('input.unitAmount[name^="t"]')) {
                setValue(input, input.name === `t${args.troopIndex}` ? args.troopCount : 0);
              }
              return true;
            }
            """,
            new { name, villageName, villageId, troopIndex, troopCount });
        if (!filled)
        {
            throw new InvalidOperationException(
                $"Could not fill Create farm list for village '{villageName}'.");
        }

        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const form = document.querySelector('#createFarmListForm');
                  const save = form?.querySelector('button.save, button[type="submit"]');
                  return !!save && !save.disabled;
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Create button did not become ready for farm list '{name}'.");
        }

        await _page.EvaluateAsync(
            """
            () => document.querySelector('#createFarmListForm button.save, #createFarmListForm button[type="submit"]')?.click()
            """);
        try
        {
            await _page.WaitForFunctionAsync(
                "() => !document.querySelector('#createFarmListForm')",
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            Notify($"[farm-list-create] dialog remained visible after saving '{name}'.");
        }
    }

    private async Task<bool> WaitForOfficialFarmListNameAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _page.WaitForFunctionAsync(
                """
                (target) => Array.from(document.querySelectorAll(
                  '#rallyPointFarmList .farmListWrapper .farmListName .name'))
                  .some(node => (node.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase()
                    === String(target).trim().toLowerCase())
                """,
                name,
                new PageWaitForFunctionOptions { Timeout = 5000 });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> HasOfficialFarmListNameAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<bool>(
            """
            (target) => Array.from(document.querySelectorAll(
              '#rallyPointFarmList .farmListWrapper .farmListName .name'))
              .some(node => (node.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase()
                === String(target).trim().toLowerCase())
            """,
            name);
    }
}
