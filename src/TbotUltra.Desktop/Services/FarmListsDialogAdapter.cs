using System.Windows;
using TbotUltra.Core.Farming;
using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

internal sealed class FarmListsDialogAdapter(Window owner)
{
    public OfficialAddFarmsDialogResult ShowAddFarms(OfficialAddFarmsDialogRequest request)
    {
        var dialog = new OfficialAddFarmsWindow(
            request.Tribe,
            request.DefaultTroopCount,
            request.Loader,
            request.Runner,
            request.PlanBuilder,
            request.CancellationToken,
            request.ProtectionPreferences,
            request.PrepareTargetProtection,
            request.Villages,
            request.SelectedVillageName)
        {
            Owner = owner,
        };
        var accepted = dialog.ShowDialog() == true && dialog.RunResult is not null;
        return new OfficialAddFarmsDialogResult(
            accepted,
            dialog.RunResult,
            dialog.RunDuration,
            dialog.LoadFailureMessage);
    }

    public FarmListCreateBatchResult? ShowCreateFarmLists(CreateFarmListsDialogRequest request)
    {
        var dialog = new CreateFarmListsWindow(
            request.Tribe,
            request.Villages,
            request.OnlyCreateReportsWithLosses,
            request.OnlyCreateReportsWithLossesChanged,
            request.Runner,
            request.CancellationToken)
        {
            Owner = owner,
        };
        return dialog.ShowDialog() == true ? dialog.RunResult : null;
    }
}

internal sealed record OfficialAddFarmsDialogRequest(
    string Tribe,
    int DefaultTroopCount,
    Func<CancellationToken, Task<OfficialAddFarmsLoadResult>> Loader,
    Func<IReadOnlyList<OfficialFarmAddPlan>, bool, string, int, FarmTargetProtectionContext,
        IProgress<FarmAddProgress>, CancellationToken, Task<OfficialFarmAddRunResult>> Runner,
    Func<OfficialFarmAddPlanRequest, IReadOnlyList<OfficialFarmAddPlan>> PlanBuilder,
    CancellationToken CancellationToken,
    AddFarmsProtectionPreferences ProtectionPreferences,
    Func<FarmTargetIdentity, AddFarmsProtectionPreferences, FarmTargetProtectionPreparation> PrepareTargetProtection,
    IReadOnlyList<OfficialAddFarmsWindow.AddFarmsVillageOption> Villages,
    string? SelectedVillageName);

internal sealed record OfficialAddFarmsDialogResult(
    bool Accepted,
    OfficialFarmAddRunResult? RunResult,
    TimeSpan RunDuration,
    string? LoadFailureMessage);

internal sealed record CreateFarmListsDialogRequest(
    string Tribe,
    IReadOnlyList<VillageSelectionItem> Villages,
    bool OnlyCreateReportsWithLosses,
    Action<bool> OnlyCreateReportsWithLossesChanged,
    Func<FarmListCreateRequest, IProgress<FarmListCreateProgress>, CancellationToken,
        Task<FarmListCreateBatchResult>> Runner,
    CancellationToken CancellationToken);
