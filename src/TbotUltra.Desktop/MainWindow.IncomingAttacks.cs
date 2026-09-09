using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<IncomingAttackRowItem> _incomingAttackRows = [];
    private readonly ObservableCollection<IncomingAttackMonitoringVillageItem> _incomingAttackMonitoringVillages = [];
    private readonly HashSet<string> _incomingAttackMonitoringDisabledKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IncomingAttack>> _incomingAttacksByVillage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IncomingAttackSignal> _incomingAttackPendingSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _incomingAttackLastReadUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _incomingAttackConfirmedMovementCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _incomingAttackReadsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _incomingAttackVisiblePlusMarkerKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _incomingAttackDorf1ClearVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool _incomingAttackSoundEnabled;
    private int _incomingAttackSoundCooldownMinutes = IncomingAttackMonitoringSettingsStore.DefaultSoundCooldownMinutes;
    private DateTimeOffset? _incomingAttackLastSoundUtc;
    private bool _incomingAttackSoundSettingsSync;

    private ICollectionView CreateIncomingAttackRowsView()
    {
        var view = CollectionViewSource.GetDefaultView(_incomingAttackRows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IncomingAttackRowItem.TargetVillageName)));
        view.SortDescriptions.Add(new SortDescription(nameof(IncomingAttackRowItem.TargetVillageName), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(IncomingAttackRowItem.ArrivalAtUtc), ListSortDirection.Ascending));
        return view;
    }

    private bool IsIncomingAttackMonitoringActive() =>
        _isLoggedIn && (IsContinuousLoopRunning() || _autoQueueRunning);

    private void ObserveIncomingAttackSignals(VillageStatus status)
    {
        if (status.IncomingAttackSignals is null || !IsIncomingAttackMonitoringActive())
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ObserveIncomingAttackSignals(status));
            return;
        }

        var resolvedSignals = new List<(string Key, IncomingAttackSignal Signal)>();
        foreach (var signal in status.IncomingAttackSignals)
        {
            var resolved = ResolveIncomingAttackSignal(signal, status);
            if (resolved is null)
            {
                AppendLog($"[incoming-attacks] skipped ambiguous signal for '{signal.VillageName}'.");
                continue;
            }

            resolvedSignals.Add(resolved.Value);
        }

        var newPlusMarkerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (status.IncomingAttackPlusOverviewWasRead)
        {
            var visiblePlusMarkerKeys = resolvedSignals
                .Where(resolved => resolved.Signal.Dorf1ArrivalTimesUtc is null
                                   && IsIncomingAttackMonitoringEnabled(resolved.Key))
                .Select(resolved => resolved.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            newPlusMarkerKeys.UnionWith(visiblePlusMarkerKeys.Except(_incomingAttackVisiblePlusMarkerKeys));
            _incomingAttackVisiblePlusMarkerKeys.IntersectWith(visiblePlusMarkerKeys);
            _incomingAttackVisiblePlusMarkerKeys.UnionWith(visiblePlusMarkerKeys);
        }

        var activeKey = VillageStatusCache.TryResolveCoordinateKey(status.ActiveVillage, status);
        if (status.IncomingAttackActiveVillageReadWasAuthoritative
            && activeKey is not null
            && IsIncomingAttackMonitoringEnabled(activeKey)
            && resolvedSignals.All(signal => !string.Equals(signal.Key, activeKey, StringComparison.OrdinalIgnoreCase)))
        {
            ClearIncomingAttacksAfterAuthoritativeDorf1Read(activeKey, status.ActiveVillage);
        }

        foreach (var (villageKey, normalizedSignal) in resolvedSignals)
        {
            if (!IsIncomingAttackMonitoringEnabled(villageKey))
            {
                continue;
            }
            var nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset? lastReadUtc = _incomingAttackLastReadUtc.TryGetValue(villageKey, out var lastRead)
                ? lastRead
                : null;
            int? confirmedMovementCount = _incomingAttackConfirmedMovementCounts.TryGetValue(villageKey, out var confirmedCount)
                ? confirmedCount
                : null;
            var shouldRead = IncomingAttackObservationPolicy.ShouldReadDetails(
                normalizedSignal,
                confirmedMovementCount,
                lastReadUtc,
                nowUtc,
                isNewPlusMarker: newPlusMarkerKeys.Contains(villageKey));
            _incomingAttackPendingSignals[villageKey] = normalizedSignal;
            if (shouldRead)
            {
                if (confirmedMovementCount.HasValue && normalizedSignal.Dorf1ArrivalTimesUtc is { } arrivals)
                {
                    AppendLog($"[incoming-attacks] red Dorf1 movement count increased for '{normalizedSignal.VillageName}': {confirmedMovementCount.Value} -> {arrivals.Count}.");
                }
                QueueIncomingAttackDetailsRead(villageKey, normalizedSignal);
            }
        }

        RefreshIncomingAttackUi();
        SaveIncomingAttackState();
    }

    private void ClearIncomingAttacksAfterAuthoritativeDorf1Read(string villageKey, string villageName)
    {
        CancelTroopEvasionForClearedVillage(villageKey);
        _incomingAttackDorf1ClearVersions[villageKey] =
            _incomingAttackDorf1ClearVersions.GetValueOrDefault(villageKey) + 1;
        var pendingRemoved = _incomingAttackPendingSignals.Remove(villageKey);
        var attacksRemoved = _incomingAttacksByVillage.Remove(villageKey);
        _incomingAttackLastReadUtc.Remove(villageKey);
        _incomingAttackConfirmedMovementCounts.Remove(villageKey);
        if (pendingRemoved || attacksRemoved)
        {
            AppendLog($"[incoming-attacks] clear Dorf1 read removed the warning for '{villageName}'.");
        }
    }

    private (string Key, IncomingAttackSignal Signal)? ResolveIncomingAttackSignal(
        IncomingAttackSignal signal,
        VillageStatus ownerStatus)
    {
        if (signal.CoordX.HasValue && signal.CoordY.HasValue)
        {
            var coordinateKey = $"xy:{signal.CoordX.Value}|{signal.CoordY.Value}";
            var known = ownerStatus.Villages.FirstOrDefault(village =>
                village.CoordX == signal.CoordX && village.CoordY == signal.CoordY);
            return (coordinateKey, signal with
            {
                VillageName = known?.Name ?? signal.VillageName,
                VillageUrl = known?.Url ?? signal.VillageUrl,
            });
        }

        var villages = ((DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
                        ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
                        ?? [])
            .Where(village => !string.IsNullOrWhiteSpace(village.Name) && village.Name != "-")
            .ToList();
        var byDid = signal.VillageId.HasValue
            ? villages.Where(village => village.Url.Contains($"newdid={signal.VillageId.Value}", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        var matches = byDid.Count == 1
            ? byDid
            : villages.Where(village => string.Equals(village.Name, signal.VillageName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
        {
            return null;
        }

        var match = matches[0];
        var key = match.CoordX.HasValue && match.CoordY.HasValue
            ? $"xy:{match.CoordX.Value}|{match.CoordY.Value}"
            : GetVillageKey(match);
        return (key, signal with
        {
            VillageName = match.Name,
            VillageUrl = match.Url,
            CoordX = match.CoordX,
            CoordY = match.CoordY,
        });
    }

    private void QueueIncomingAttackDetailsRead(string villageKey, IncomingAttackSignal signal)
    {
        if (!IsIncomingAttackMonitoringActive()
            || !IsIncomingAttackMonitoringEnabled(villageKey))
        {
            return;
        }
        if (!_incomingAttackReadsInFlight.Add(villageKey))
        {
            return;
        }

        _incomingAttackLastReadUtc[villageKey] = DateTimeOffset.UtcNow;
        var browserGeneration = _botService.BrowserGeneration;
        var dorf1ClearVersion = _incomingAttackDorf1ClearVersions.GetValueOrDefault(villageKey);
        _ = ReadIncomingAttackDetailsAsync(villageKey, signal, browserGeneration, dorf1ClearVersion);
    }

    private async Task ReadIncomingAttackDetailsAsync(
        string villageKey,
        IncomingAttackSignal signal,
        long browserGeneration,
        long dorf1ClearVersion)
    {
        try
        {
            var snapshot = await _botService.ReadIncomingAttacksAsync(
                AutomationExecutionOptions.WithoutImplicitVillageTarget(LoadBotOptions()),
                AppendLog,
                signal.VillageName,
                signal.VillageUrl,
                villageKey,
                _loopController.AcquireSessionScopeToken());
            await Dispatcher.InvokeAsync(() =>
            {
                if (browserGeneration != _botService.BrowserGeneration)
                {
                    AppendLog("[incoming-attacks] discarded result from an old browser generation.");
                    return;
                }
                if (_incomingAttackDorf1ClearVersions.GetValueOrDefault(villageKey) != dorf1ClearVersion)
                {
                    AppendLog($"[incoming-attacks] discarded stale Rally Point result for '{signal.VillageName}' after a clear Dorf1 read.");
                    return;
                }
                if (!IsIncomingAttackMonitoringEnabled(villageKey))
                {
                    AppendLog($"[incoming-attacks] discarded result for disabled village '{signal.VillageName}'.");
                    return;
                }

                var resolvedKey = snapshot.TargetVillageKey ?? villageKey;
                if (!snapshot.RallyPointReadSucceeded)
                {
                    _incomingAttackPendingSignals[resolvedKey] = signal with
                    {
                        Dorf1ArrivalTimesUtc = snapshot.Dorf1FallbackArrivalTimesUtc,
                        ObservedAtUtc = snapshot.ObservedAtUtc,
                    };
                    _incomingAttackLastReadUtc[resolvedKey] = DateTimeOffset.UtcNow;
                    RefreshIncomingAttackUi();
                    SaveIncomingAttackState();
                    return;
                }

                var activeAttacks = snapshot.Attacks
                    .Where(attack => attack.ArrivalAtUtc > DateTimeOffset.UtcNow)
                    .OrderBy(attack => attack.ArrivalAtUtc)
                    .ToList();
                var previousAttacks = _incomingAttacksByVillage.GetValueOrDefault(resolvedKey) ?? [];
                if (!string.Equals(resolvedKey, villageKey, StringComparison.OrdinalIgnoreCase)
                    && _incomingAttacksByVillage.TryGetValue(villageKey, out var unresolvedAttacks))
                {
                    previousAttacks = previousAttacks.Concat(unresolvedAttacks).ToList();
                }
                var newAttackCount = IncomingAttackObservationPolicy.CountNewConfirmedAttacks(previousAttacks, activeAttacks);
                _incomingAttacksByVillage[resolvedKey] = activeAttacks;
                var confirmedMovementCount = Math.Max(
                    _incomingAttackConfirmedMovementCounts.GetValueOrDefault(resolvedKey),
                    _incomingAttackConfirmedMovementCounts.GetValueOrDefault(villageKey));
                _incomingAttackConfirmedMovementCounts[resolvedKey] = Math.Max(
                    confirmedMovementCount,
                    snapshot.Attacks.Count);
                if (_incomingAttacksByVillage[resolvedKey].Count == 0)
                {
                    CancelTroopEvasionForClearedVillage(resolvedKey);
                }
                _incomingAttackPendingSignals.Remove(villageKey);
                if (!string.Equals(resolvedKey, villageKey, StringComparison.OrdinalIgnoreCase))
                {
                    _incomingAttackPendingSignals.Remove(resolvedKey);
                    _incomingAttacksByVillage.Remove(villageKey);
                    _incomingAttackConfirmedMovementCounts.Remove(villageKey);
                    _incomingAttackLastReadUtc.Remove(villageKey);
                }
                _incomingAttackLastReadUtc[resolvedKey] = DateTimeOffset.UtcNow;
                RefreshIncomingAttackUi();
                SaveIncomingAttackState();
                PlayIncomingAttackSoundIfDue(newAttackCount, snapshot.ObservedAtUtc);
            });
        }
        catch (OperationCanceledException)
        {
            // Pause/account switch leaves the pending warning visible for the next active run.
        }
        catch (Exception ex)
        {
            AppendLog($"[incoming-attacks] detail read for '{signal.VillageName}' failed: {ex.Message}");
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => _incomingAttackReadsInFlight.Remove(villageKey));
        }
    }

    private void TickIncomingAttacks(DateTimeOffset serverNow)
    {
        var nowUtc = serverNow.ToUniversalTime();
        var expiredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _incomingAttacksByVillage.ToList())
        {
            var active = pair.Value.Where(attack => attack.ArrivalAtUtc > nowUtc).ToList();
            if (active.Count != pair.Value.Count)
            {
                expiredKeys.Add(pair.Key);
                _incomingAttacksByVillage[pair.Key] = active;
            }
        }

        var expiredPendingKeys = _incomingAttackPendingSignals
            .Where(pair => !IncomingAttackObservationPolicy.ShouldKeepPendingSignal(
                pair.Value,
                _incomingAttacksByVillage.GetValueOrDefault(pair.Key)?.Count > 0,
                _incomingAttackConfirmedMovementCounts.ContainsKey(pair.Key),
                nowUtc))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in expiredPendingKeys)
        {
            _incomingAttackPendingSignals.Remove(key);
        }

        if (expiredKeys.Count > 0 || expiredPendingKeys.Count > 0)
        {
            if (expiredPendingKeys.Count > 0)
            {
                AppendLog($"[incoming-attacks] removed {expiredPendingKeys.Count} warning(s) after their arrival time passed.");
            }
            RefreshIncomingAttackUi();
            SaveIncomingAttackState();
        }

        if (IsIncomingAttackMonitoringActive())
        {
            foreach (var pending in _incomingAttackPendingSignals.ToList())
            {
                if (!IsIncomingAttackMonitoringEnabled(pending.Key))
                {
                    continue;
                }
                DateTimeOffset? lastReadUtc = _incomingAttackLastReadUtc.TryGetValue(pending.Key, out var lastRead)
                    ? lastRead
                    : null;
                int? confirmedMovementCount = _incomingAttackConfirmedMovementCounts.TryGetValue(pending.Key, out var confirmedCount)
                    ? confirmedCount
                    : null;
                if (IncomingAttackObservationPolicy.ShouldReadDetails(
                        pending.Value,
                        confirmedMovementCount,
                        lastReadUtc,
                        nowUtc))
                {
                    QueueIncomingAttackDetailsRead(pending.Key, pending.Value);
                }
            }
        }

        foreach (var row in _incomingAttackRows)
        {
            if (row.ArrivalAtUtc is not { } arrival)
            {
                row.CountdownText = "Reading…";
                continue;
            }

            row.CountdownText = FormatIncomingAttackCountdown(arrival, nowUtc);
        }
    }

    private void RefreshIncomingAttackUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshIncomingAttackUi);
            return;
        }

        var rows = _incomingAttacksByVillage
            .SelectMany(pair => pair.Value.Select(attack => CreateIncomingAttackRow(pair.Key, attack)))
            .ToList();
        foreach (var pending in _incomingAttackPendingSignals)
        {
            if (rows.Any(row => string.Equals(row.VillageKey, pending.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (_incomingAttackConfirmedMovementCounts.ContainsKey(pending.Key))
            {
                continue;
            }

            rows.Add(new IncomingAttackRowItem
            {
                Id = $"pending:{pending.Key}",
                VillageKey = pending.Key,
                TargetVillageName = pending.Value.VillageName,
                IsReading = true,
                CountdownText = "Reading…",
            });
        }

        rows = rows.OrderBy(row => row.ArrivalAtUtc ?? DateTimeOffset.MinValue).ToList();
        _incomingAttackRows.Clear();
        foreach (var row in rows)
        {
            _incomingAttackRows.Add(row);
        }

        RefreshIncomingAttackVillageIndicators();
        SyncTroopEvasionVillages();
    }

    private IncomingAttackRowItem CreateIncomingAttackRow(string villageKey, IncomingAttack attack)
    {
        var sourceCoordinates = attack.SourceCoordX.HasValue && attack.SourceCoordY.HasValue
            ? $"({attack.SourceCoordX.Value} | {attack.SourceCoordY.Value})"
            : string.Empty;
        return new IncomingAttackRowItem
        {
            Id = attack.Id,
            VillageKey = villageKey,
            TargetVillageName = attack.TargetVillageName,
            MovementType = attack.MovementType,
            SourcePlayerName = attack.SourcePlayerName ?? string.Empty,
            SourceVillageName = attack.SourceVillageName ?? string.Empty,
            SourceCoordinatesText = sourceCoordinates,
            ArrivalAtUtc = attack.ArrivalAtUtc,
            ArrivalText = FormatQueueServerTime(attack.ArrivalAtUtc),
            CountdownText = FormatIncomingAttackCountdown(attack.ArrivalAtUtc, GetServerNow().ToUniversalTime()),
        };
    }

    private static string FormatIncomingAttackCountdown(DateTimeOffset arrivalAtUtc, DateTimeOffset nowUtc)
    {
        var remaining = arrivalAtUtc - nowUtc;
        return remaining <= TimeSpan.Zero
            ? "Arrived"
            : $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void RefreshIncomingAttackVillageIndicators()
    {
        var activeKeys = _incomingAttacksByVillage
            .Where(pair => pair.Value.Count > 0)
            .Select(pair => pair.Key)
            .Concat(_incomingAttackPendingSignals.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var source = (DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>) ?? [];
        foreach (var village in source)
        {
            var key = GetVillageKey(village);
            village.HasIncomingAttack = activeKeys.Contains(key);
            if (!village.HasIncomingAttack)
            {
                village.IncomingAttackTooltip = "No incoming attacks";
                continue;
            }

            var count = _incomingAttacksByVillage.TryGetValue(key, out var attacks) ? attacks.Count : 0;
            village.IncomingAttackTooltip = count > 0
                ? $"{count} incoming attack(s) — click for details"
                : "Incoming attack detected — reading details";
        }
    }

    private void IncomingAttackButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not VillageSelectionItem village)
        {
            return;
        }

        MainTabControl.SelectedItem = TroopsTabItem;
        TroopsHubPanelControl.SelectIncomingAttacks();
        UpdateSidebarSelection(TroopsNavButton);
        var key = GetVillageKey(village);
        var row = _incomingAttackRows
            .Where(candidate => string.Equals(candidate.VillageKey, key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.ArrivalAtUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (row is null)
        {
            return;
        }

        TroopsHubPanelControl.IncomingAttacksGrid.SelectedItem = row;
        TroopsHubPanelControl.IncomingAttacksGrid.ScrollIntoView(row);
        TroopsHubPanelControl.IncomingAttacksGrid.Focus();
    }

    private void LoadIncomingAttacksForActiveAccount()
    {
        LoadIncomingAttackMonitoringSettings();
        var state = _incomingAttackStore.Load(
            _accountStore.ActiveAccountName(),
            LoadBotOptions().BaseUrl,
            DateTimeOffset.UtcNow);
        if (state.Attacks.Count > 0 || state.PendingSignals.Count > 0)
        {
            AppendLog($"[incoming-attacks] restored {state.Attacks.Count} active movement(s) and {state.PendingSignals.Count} pending signal(s) from snapshot.");
        }
        _incomingAttacksByVillage.Clear();
        _incomingAttackConfirmedMovementCounts.Clear();
        _incomingAttackVisiblePlusMarkerKeys.Clear();
        foreach (var pair in state.ConfirmedMovementCounts)
        {
            if (pair.Value >= 0)
            {
                _incomingAttackConfirmedMovementCounts[pair.Key] = pair.Value;
            }
        }
        foreach (var attack in state.Attacks)
        {
            var key = attack.TargetVillageKey
                      ?? (attack.TargetCoordX.HasValue && attack.TargetCoordY.HasValue
                          ? $"xy:{attack.TargetCoordX.Value}|{attack.TargetCoordY.Value}"
                          : attack.TargetVillageName);
            if (!_incomingAttacksByVillage.TryGetValue(key, out var attacks))
            {
                attacks = [];
                _incomingAttacksByVillage[key] = attacks;
            }
            attacks.Add(attack);
        }
        foreach (var pair in _incomingAttacksByVillage)
        {
            _incomingAttackConfirmedMovementCounts[pair.Key] = Math.Max(
                _incomingAttackConfirmedMovementCounts.GetValueOrDefault(pair.Key),
                pair.Value.Count);
        }

        _incomingAttackPendingSignals.Clear();
        foreach (var signal in state.PendingSignals)
        {
            var key = signal.CoordX.HasValue && signal.CoordY.HasValue
                ? $"xy:{signal.CoordX.Value}|{signal.CoordY.Value}"
                : signal.VillageName;
            if (!IsIncomingAttackMonitoringEnabled(key))
            {
                continue;
            }
            _incomingAttackPendingSignals[key] = signal;
        }
        SyncIncomingAttackMonitoringVillages();
        RefreshIncomingAttackUi();
        LoadTroopEvasionForActiveAccount();
    }

    private void SaveIncomingAttackState()
    {
        _incomingAttackStore.Save(
            _accountStore.ActiveAccountName(),
            LoadBotOptions().BaseUrl,
            _incomingAttacksByVillage.Values.SelectMany(attacks => attacks).ToList(),
            _incomingAttackPendingSignals.Values.ToList(),
            _incomingAttackConfirmedMovementCounts);
    }

    private void ClearIncomingAttackUiState()
    {
        _incomingAttacksByVillage.Clear();
        _incomingAttackPendingSignals.Clear();
        _incomingAttackLastReadUtc.Clear();
        _incomingAttackConfirmedMovementCounts.Clear();
        _incomingAttackReadsInFlight.Clear();
        _incomingAttackDorf1ClearVersions.Clear();
        _incomingAttackVisiblePlusMarkerKeys.Clear();
        _incomingAttackRows.Clear();
        _incomingAttackMonitoringVillages.Clear();
        _incomingAttackMonitoringDisabledKeys.Clear();
        _incomingAttackSoundEnabled = false;
        _incomingAttackSoundCooldownMinutes = IncomingAttackMonitoringSettingsStore.DefaultSoundCooldownMinutes;
        _incomingAttackLastSoundUtc = null;
        SyncIncomingAttackSoundSettings();
        ClearTroopEvasionUiState();
    }

    private bool IsIncomingAttackMonitoringEnabled(string villageKey) =>
        !_incomingAttackMonitoringDisabledKeys.Contains(villageKey);

    private void LoadIncomingAttackMonitoringSettings()
    {
        var settings = _incomingAttackMonitoringStore.Load(
            _accountStore.ActiveAccountName(), LoadBotOptions().BaseUrl);
        _incomingAttackMonitoringDisabledKeys.Clear();
        foreach (var key in settings.DisabledVillageKeys)
        {
            _incomingAttackMonitoringDisabledKeys.Add(key);
        }
        _incomingAttackSoundEnabled = settings.SoundEnabled;
        _incomingAttackSoundCooldownMinutes = settings.SoundCooldownMinutes;
        _incomingAttackLastSoundUtc = null;
        SyncIncomingAttackSoundSettings();
    }

    private void SyncIncomingAttackSoundSettings()
    {
        if (TroopsHubPanelControl?.IncomingAttackSoundAlert is null) return;
        _incomingAttackSoundSettingsSync = true;
        try
        {
            TroopsHubPanelControl.IncomingAttackSoundAlert.IsChecked = _incomingAttackSoundEnabled;
            var selectedItem = TroopsHubPanelControl.IncomingAttackSoundCooldown.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => int.TryParse(item.Tag?.ToString(), out var minutes)
                                        && minutes == _incomingAttackSoundCooldownMinutes);
            TroopsHubPanelControl.IncomingAttackSoundCooldown.SelectedItem = selectedItem
                ?? TroopsHubPanelControl.IncomingAttackSoundCooldown.Items[0];
        }
        finally
        {
            _incomingAttackSoundSettingsSync = false;
        }
    }

    private void SyncIncomingAttackMonitoringVillages()
    {
        if (TroopsHubPanelControl?.IncomingAttackMonitoringVillages is null) return;
        var villages = ((DashboardVillageList.ItemsSource as IEnumerable<VillageSelectionItem>)
                        ?? (VillageComboBox.ItemsSource as IEnumerable<VillageSelectionItem>)
                        ?? [])
            .Where(village => !string.IsNullOrWhiteSpace(village.Name) && village.Name != "-")
            .ToList();
        var existing = _incomingAttackMonitoringVillages.ToDictionary(item => item.VillageKey, StringComparer.OrdinalIgnoreCase);
        foreach (var village in villages)
        {
            var key = GetVillageKey(village);
            if (existing.TryGetValue(key, out var item))
            {
                item.VillageName = village.Name;
                item.Enabled = IsIncomingAttackMonitoringEnabled(key);
                continue;
            }

            _incomingAttackMonitoringVillages.Add(new IncomingAttackMonitoringVillageItem
            {
                VillageKey = key,
                VillageName = village.Name,
                Enabled = IsIncomingAttackMonitoringEnabled(key),
            });
        }

        foreach (var stale in _incomingAttackMonitoringVillages
                     .Where(item => villages.All(village => !string.Equals(GetVillageKey(village), item.VillageKey, StringComparison.OrdinalIgnoreCase)))
                     .ToList())
        {
            _incomingAttackMonitoringVillages.Remove(stale);
        }
    }

    internal void OnIncomingAttackMonitoringChanged(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not IncomingAttackMonitoringVillageItem village) return;
        ApplyIncomingAttackMonitoring(village.VillageKey, village.Enabled);
        PersistIncomingAttackMonitoringChanges();
    }

    internal void OnToggleAllIncomingAttackMonitoringClicked(object sender, RoutedEventArgs e)
    {
        var enableAll = _incomingAttackMonitoringVillages.Any(village => !village.Enabled);
        foreach (var village in _incomingAttackMonitoringVillages)
        {
            village.Enabled = enableAll;
            ApplyIncomingAttackMonitoring(village.VillageKey, enableAll);
        }

        PersistIncomingAttackMonitoringChanges();
    }

    internal void OnIncomingAttackSoundSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_incomingAttackSoundSettingsSync) return;
        _incomingAttackSoundEnabled = TroopsHubPanelControl.IncomingAttackSoundAlert.IsChecked == true;
        if (TroopsHubPanelControl.IncomingAttackSoundCooldown.SelectedItem is ComboBoxItem selected
            && int.TryParse(selected.Tag?.ToString(), out var minutes))
        {
            _incomingAttackSoundCooldownMinutes = minutes;
        }
        PersistIncomingAttackMonitoringChanges();
    }

    private void ApplyIncomingAttackMonitoring(string villageKey, bool enabled)
    {
        if (enabled)
        {
            _incomingAttackMonitoringDisabledKeys.Remove(villageKey);
            _incomingAttackLastReadUtc.Remove(villageKey);
        }
        else
        {
            _incomingAttackMonitoringDisabledKeys.Add(villageKey);
            CancelTroopEvasionForClearedVillage(villageKey);
            _incomingAttackPendingSignals.Remove(villageKey);
            _incomingAttackLastReadUtc.Remove(villageKey);
        }
    }

    private void PersistIncomingAttackMonitoringChanges()
    {
        SyncIncomingAttackMonitoringVillages();
        _incomingAttackMonitoringStore.Save(
            _accountStore.ActiveAccountName(),
            LoadBotOptions().BaseUrl,
            _incomingAttackMonitoringDisabledKeys,
            _incomingAttackSoundEnabled,
            _incomingAttackSoundCooldownMinutes);
        SaveIncomingAttackState();
        RefreshIncomingAttackUi();
        UpdateTroopEvasionRuntimeStatuses();
        SyncVillageProtectionSettingsRows();
    }

    private void PlayIncomingAttackSoundIfDue(int newAttackCount, DateTimeOffset observedAtUtc)
    {
        var nowUtc = observedAtUtc.ToUniversalTime();
        var cooldown = TimeSpan.FromMinutes(_incomingAttackSoundCooldownMinutes);
        if (!IncomingAttackObservationPolicy.ShouldPlaySound(
                _incomingAttackSoundEnabled,
                newAttackCount,
                _incomingAttackLastSoundUtc,
                cooldown,
                nowUtc))
        {
            if (_incomingAttackSoundEnabled && newAttackCount > 0)
            {
                AppendLog($"[incoming-attacks] sound suppressed by {_incomingAttackSoundCooldownMinutes}-minute cooldown for {newAttackCount} new movement(s).");
            }
            return;
        }

        try
        {
            SystemSounds.Exclamation.Play();
            _incomingAttackLastSoundUtc = nowUtc;
            AppendLog($"[incoming-attacks] played one bulk sound for {newAttackCount} new confirmed movement(s).");
        }
        catch (Exception ex)
        {
            AppendLog($"[incoming-attacks] sound could not be played: {ex.Message}");
        }
    }

    internal void OnClearIncomingAttackListClicked(object sender, RoutedEventArgs e)
    {
        var clearedCount = _incomingAttacksByVillage.Values.Sum(attacks => attacks.Count);
        foreach (var villageKey in _incomingAttacksByVillage.Keys.ToList())
        {
            CancelTroopEvasionForClearedVillage(villageKey);
        }

        _incomingAttacksByVillage.Clear();
        _incomingAttackPendingSignals.Clear();
        SaveIncomingAttackState();
        RefreshIncomingAttackUi();
        AppendLog($"[incoming-attacks] user cleared {clearedCount} movement(s) from the list.");
    }
}
