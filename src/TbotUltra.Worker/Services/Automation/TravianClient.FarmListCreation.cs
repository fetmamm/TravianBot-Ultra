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
            if (index < names.Count - 1)
            {
                await DelayFarmListStepAsync(cancellationToken);
            }
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
              const buttons = Array.from(document.querySelectorAll(
                '#stickyPin button.createFarmList, #rallyPointFarmList button.createFarmList, button.createFarmList'));
              const button = buttons.find(candidate =>
                !candidate.disabled
                && candidate.getAttribute('aria-disabled') !== 'true'
                && !candidate.classList.contains('disabled'))
                || buttons[0];
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
                """
                () => {
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                  const hasNameInput = root => !!root.querySelector(
                    'input[name="listName"], input[name="name"], input[name*="list" i], input[placeholder*="name" i], input[type="text"]');
                  const roots = Array.from(document.querySelectorAll(
                    '#createFarmListForm, #dialogContent, .dialogWrapper, .dialogContainer, [role="dialog"]'));
                  return roots.some(root => {
                    const text = normalize(root.textContent).toLowerCase();
                    return hasNameInput(root)
                      && (root.id === 'createFarmListForm' || text.includes('farm list') || text.includes('farmlist'));
                  });
                }
                """,
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

        var form = _page.Locator("#createFarmListForm").First;
        try
        {
            await form.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5000,
            }).WaitAsync(cancellationToken);

            await DelayFarmListStepAsync(cancellationToken);
            await form.Locator("input[name='listName']").First.FillAsync(name).WaitAsync(cancellationToken);

            var villageValue = await ResolveOfficialCreateFarmListVillageValueAsync(
                villageName,
                villageId,
                cancellationToken);
            await DelayFarmListStepAsync(cancellationToken);
            await form.Locator("select[name='villageId']").First
                .SelectOptionAsync(villageValue)
                .WaitAsync(cancellationToken);
            await _page.WaitForFunctionAsync(
                "value => document.querySelector('#createFarmListForm select[name=\"villageId\"]')?.value === value",
                villageValue,
                new PageWaitForFunctionOptions { Timeout = 5000 }).WaitAsync(cancellationToken);

            var troopInputs = await form.Locator("input.unitAmount[name^='t']").CountAsync();
            if (troopInputs == 0)
            {
                throw new InvalidOperationException("No default troop inputs were found.");
            }

            for (var unit = 1; unit <= troopInputs; unit++)
            {
                var value = unit == troopIndex
                    ? troopCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "0";
                await form.Locator($"input.unitAmount[name='t{unit}']").First
                    .FillAsync(value)
                    .WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not fill Create farm list for village '{villageName}': {ex.Message}", ex);
        }

        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const findDialog = () => {
                    const roots = Array.from(document.querySelectorAll(
                      '#createFarmListForm, #dialogContent, .dialogWrapper, .dialogContainer, [role="dialog"]'));
                    return roots.find(root => {
                      const text = normalize(root.textContent);
                      const hasNameInput = !!root.querySelector(
                        'input[name="listName"], input[name="name"], input[name*="list" i], input[placeholder*="name" i], input[type="text"]');
                      return hasNameInput
                        && (root.id === 'createFarmListForm' || text.includes('farm list') || text.includes('farmlist'));
                    }) || null;
                  };
                  const dialog = findDialog();
                  if (!dialog) return false;
                  const buttons = Array.from(dialog.querySelectorAll('button, input[type="submit"], a.button, .textButtonV2'));
                  const save = buttons.find(button => {
                    const text = normalize(button.textContent || button.value || button.getAttribute('aria-label') || button.getAttribute('title'));
                    return !text.includes('cancel')
                      && !text.includes('delete')
                      && (text.includes('create') || text.includes('save') || text.includes('confirm') || button.classList.contains('save'));
                  }) || dialog.querySelector('button.save, button[type="submit"]');
                  return !!save
                    && !save.disabled
                    && save.getAttribute('aria-disabled') !== 'true'
                    && !save.classList.contains('disabled');
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Create button did not become ready for farm list '{name}'.");
        }

        await DelayBeforeClickAsync(cancellationToken, "create farm list");
        await form.Locator("button.save[type='submit'], button.save, button[type='submit']").First
            .ClickAsync()
            .WaitAsync(cancellationToken);
        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
                  const roots = Array.from(document.querySelectorAll(
                    '#createFarmListForm, #dialogContent, .dialogWrapper, .dialogContainer, [role="dialog"]'));
                  return !roots.some(root => {
                    const text = normalize(root.textContent);
                    const hasNameInput = !!root.querySelector(
                      'input[name="listName"], input[name="name"], input[name*="list" i], input[placeholder*="name" i], input[type="text"]');
                    return hasNameInput
                      && (root.id === 'createFarmListForm' || text.includes('farm list') || text.includes('farmlist'));
                  });
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            Notify($"[farm-list-create] dialog remained visible after saving '{name}'.");
        }
    }

    private async Task<string> ResolveOfficialCreateFarmListVillageValueAsync(
        string villageName,
        string? villageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _page.EvaluateAsync<string?>(
            """
            (args) => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const select = document.querySelector('#createFarmListForm select[name="villageId"]');
              if (!select) return null;
              const options = Array.from(select.options || []);
              const option = options.find(item => args.villageId && item.value === String(args.villageId))
                || options.find(item => normalize(item.textContent) === normalize(args.villageName));
              return option?.value || null;
            }
            """,
            new { villageName, villageId }).WaitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Could not find village '{villageName}' in the Create farm list dropdown.");
        }

        return value;
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
