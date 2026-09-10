using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public sealed record BuildingTemplateVillageTarget(
    VillageSelectionItem Village,
    VillageStatus? SourceStatus,
    VillageStatus? PlanningStatus,
    IReadOnlyList<QueueItem> ExistingQueueItems);

public sealed record BuildingTemplateQueueSelection(
    VillageSelectionItem Village,
    VillageStatus Status,
    IReadOnlyList<QueueItem> ExistingQueueItems,
    BuildingTemplatePlanResult Plan);

public partial class BuildingTemplateVillageQueueWindow : Window
{
    public ObservableCollection<BuildingTemplateVillageQueueRow> Villages { get; }
    public IReadOnlyList<BuildingTemplateQueueSelection> Selections { get; private set; } = [];

    public BuildingTemplateVillageQueueWindow(
        IReadOnlyList<BuildingTemplateVillageTarget> targets,
        IReadOnlyList<BuildingTemplateRow> templateRows,
        double serverSpeed)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        var planner = new BuildingTemplatePlanner();
        Villages = new ObservableCollection<BuildingTemplateVillageQueueRow>(
            targets.Select(target => BuildingTemplateVillageQueueRow.Create(target, templateRows, planner, serverSpeed)));
        DataContext = this;
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        Selections = Villages
            .Where(row => row.IsSelected && row.CanSelect && row.Status is not null && row.Plan is not null)
            .Select(row => new BuildingTemplateQueueSelection(row.Village, row.Status!, row.ExistingQueueItems, row.Plan!))
            .ToList();
        if (Selections.Count == 0)
        {
            AppDialog.Show(this, "Select at least one available village.", "Queue template", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}

public sealed class BuildingTemplateVillageQueueRow : INotifyPropertyChanged
{
    private bool _isSelected;

    private BuildingTemplateVillageQueueRow(
        VillageSelectionItem village,
        VillageStatus? status,
        IReadOnlyList<QueueItem> existingQueueItems,
        BuildingTemplatePlanResult? plan,
        bool canSelect,
        string statusText)
    {
        Village = village;
        Status = status;
        ExistingQueueItems = existingQueueItems;
        Plan = plan;
        CanSelect = canSelect;
        _isSelected = canSelect;
        StatusText = statusText;
    }

    public VillageSelectionItem Village { get; }
    public VillageStatus? Status { get; }
    public IReadOnlyList<QueueItem> ExistingQueueItems { get; }
    public BuildingTemplatePlanResult? Plan { get; }
    public bool CanSelect { get; }
    public string DisplayName => Village.NameWithCoords;
    public string StatusText { get; }
    public string ActionCountText => Plan is { Actions.Count: > 0 } ? $"{Plan.Actions.Count} item(s)" : string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect || _isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public static BuildingTemplateVillageQueueRow Create(
        BuildingTemplateVillageTarget target,
        IReadOnlyList<BuildingTemplateRow> rows,
        BuildingTemplatePlanner planner,
        double serverSpeed)
    {
        var status = target.SourceStatus;
        var planningStatus = target.PlanningStatus;
        if (status is null || planningStatus is null || status.Buildings.Count == 0 || status.ResourceFields.Count == 0)
        {
            return new(target.Village, status, target.ExistingQueueItems, null, false, "Load buildings first.");
        }

        if (status.WarehouseCapacity is not > 0 || status.GranaryCapacity is not > 0)
        {
            return new(target.Village, status, target.ExistingQueueItems, null, false, "Refresh buildings and storage capacity first.");
        }

        var mainBuildingLevel = planningStatus.Buildings
            .Where(building => building.Gid == 15 || string.Equals(building.Name, "Main Building", StringComparison.OrdinalIgnoreCase))
            .Select(building => building.Level ?? 0)
            .DefaultIfEmpty(1)
            .Max();
        var plan = planner.Plan(rows, planningStatus, serverSpeed, Math.Max(1, mainBuildingLevel));
        if (plan.Errors.Count > 0)
        {
            return new(target.Village, status, target.ExistingQueueItems, plan, false, string.Join(" ", plan.Errors.Take(2)));
        }

        var blockingWarnings = plan.Warnings
            .Where(warning => warning.Contains("no valid free building slot", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (blockingWarnings.Count > 0)
        {
            return new(target.Village, status, target.ExistingQueueItems, plan, false, string.Join(" ", blockingWarnings.Take(2)));
        }

        if (plan.Actions.Count == 0)
        {
            return new(target.Village, status, target.ExistingQueueItems, plan, false, "Already complete or already queued.");
        }

        var statusText = target.Village.IsEnabledForAutomation
            ? "Ready to queue."
            : "Auto off - queued tasks will wait.";
        return new(target.Village, status, target.ExistingQueueItems, plan, true, statusText);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
