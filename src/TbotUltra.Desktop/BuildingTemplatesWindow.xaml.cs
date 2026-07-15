using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class BuildingTemplatesWindow : Window, INotifyPropertyChanged
{
    private readonly BuildingTemplateStore _store;
    private readonly BuildingTemplatePlanner _planner = new();
    private readonly VillageStatus _status;
    private readonly double _serverSpeed;
    private readonly int _mainBuildingLevel;
    private BuildingTemplate? _selectedTemplate;
    private BuildingTemplateRowView? _selectedRow;
    private string _statusText = string.Empty;
    private string _totalWoodText = "-";
    private string _totalClayText = "-";
    private string _totalIronText = "-";
    private string _totalCropText = "-";
    private string _totalTimeText = "Time -";
    private string _totalConstructFasterTimeText = "Time (25%) -";
    private string _validationSummaryText = string.Empty;
    private bool _isRefreshingPlanPreview;
    private string? _templateLoadWarning;

    public ObservableCollection<BuildingTemplate> Templates { get; } = [];
    public ObservableCollection<BuildingTemplateRowView> Rows { get; } = [];
    public ObservableCollection<BuildingTemplateTargetOption> BuildingOptions { get; } = [];
    public ObservableCollection<BuildingTemplateTargetOption> ResourceOptions { get; } = [];
    public IReadOnlyList<string> RowKinds { get; } = ["Building", "Add resources"];
    public IReadOnlyList<string> LevelOptions { get; } =
        Enumerable.Range(1, 20).Select(item => item.ToString()).ToList();

    public BuildingTemplatePlanResult? QueuePlan { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BuildingTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (ReferenceEquals(_selectedTemplate, value))
            {
                return;
            }

            if (_selectedTemplate is not null)
            {
                _selectedTemplate.Rows = BuildTemplateRowsFromUi().ToList();
                _selectedTemplate.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            _selectedTemplate = value;
            OnPropertyChanged();
            LoadRowsFromSelectedTemplate();
        }
    }

    public BuildingTemplateRowView? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string TotalWoodText
    {
        get => _totalWoodText;
        private set => SetProperty(ref _totalWoodText, value);
    }

    public string TotalClayText
    {
        get => _totalClayText;
        private set => SetProperty(ref _totalClayText, value);
    }

    public string TotalIronText
    {
        get => _totalIronText;
        private set => SetProperty(ref _totalIronText, value);
    }

    public string TotalCropText
    {
        get => _totalCropText;
        private set => SetProperty(ref _totalCropText, value);
    }

    public string TotalTimeText
    {
        get => _totalTimeText;
        private set => SetProperty(ref _totalTimeText, value);
    }

    public string TotalConstructFasterTimeText
    {
        get => _totalConstructFasterTimeText;
        private set => SetProperty(ref _totalConstructFasterTimeText, value);
    }

    public string ValidationSummaryText
    {
        get => _validationSummaryText;
        private set => SetProperty(ref _validationSummaryText, value);
    }

    public BuildingTemplatesWindow(
        string projectRoot,
        VillageStatus status,
        double serverSpeed,
        int mainBuildingLevel)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        DataContext = this;

        _store = new BuildingTemplateStore(projectRoot);
        _status = status;
        _serverSpeed = serverSpeed;
        _mainBuildingLevel = mainBuildingLevel;

        Rows.CollectionChanged += Rows_CollectionChanged;
        LoadBuildingOptions(status.Tribe);
        LoadTemplates();
        RefreshPlanPreview();
    }

    private void LoadBuildingOptions(string tribe)
    {
        BuildingOptions.Clear();
        foreach (var item in BuildingCatalogService.GetFullCatalog(tribe)
                     .Where(item => item.Gid is not 38 and not 39 and not 40)
                     .OrderBy(item => CategorySortOrder(CategoryDisplayName(item.Gid, item.Category, item.IsSpecial)))
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            BuildingOptions.Add(new BuildingTemplateTargetOption(
                item.Gid,
                item.Name,
                CategoryDisplayName(item.Gid, item.Category, item.IsSpecial),
                ResourceScope: null,
                FixedSlotId: FixedSlotFor(item.Gid)));
        }

        ResourceOptions.Clear();
        ResourceOptions.Add(new BuildingTemplateTargetOption(null, "All resources", "Resources", "all", null));
        ResourceOptions.Add(new BuildingTemplateTargetOption(null, "All Woodcutters", "Resources", "wood", null));
        ResourceOptions.Add(new BuildingTemplateTargetOption(null, "All Clay Pits", "Resources", "clay", null));
        ResourceOptions.Add(new BuildingTemplateTargetOption(null, "All Iron Mines", "Resources", "iron", null));
        ResourceOptions.Add(new BuildingTemplateTargetOption(null, "All Croplands", "Resources", "crop", null));
    }

    private void LoadTemplates()
    {
        Templates.Clear();
        var loadedTemplates = _store.Load();
        _templateLoadWarning = _store.LastLoadWarning;
        foreach (var template in loadedTemplates)
        {
            Templates.Add(template);
        }

        if (Templates.Count == 0)
        {
            Templates.Add(CreateNewTemplate("New template"));
        }

        SelectedTemplate = Templates[0];
    }

    private BuildingTemplate CreateNewTemplate(string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new BuildingTemplate
        {
            Name = name,
            CreatedByTribe = string.IsNullOrWhiteSpace(_status.Tribe) ? "Unknown" : _status.Tribe,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private void LoadRowsFromSelectedTemplate()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= Row_PropertyChanged;
        }

        Rows.Clear();
        if (SelectedTemplate is not null)
        {
            foreach (var row in SelectedTemplate.Rows)
            {
                AddRowView(BuildingTemplateRowView.From(row, BuildingOptions, ResourceOptions));
            }
        }

        RefreshIndexes();
        RefreshPlanPreview();
    }

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (BuildingTemplateRowView row in e.OldItems)
            {
                row.PropertyChanged -= Row_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (BuildingTemplateRowView row in e.NewItems)
            {
                row.PropertyChanged += Row_PropertyChanged;
            }
        }

        RefreshIndexes();
        RefreshPlanPreview();
    }

    private void AddRowView(BuildingTemplateRowView row)
    {
        row.SetOptionSources(BuildingOptions, ResourceOptions);
        Rows.Add(row);
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRefreshingPlanPreview)
        {
            return;
        }

        RefreshPlanPreview();
    }

    private void NewTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var index = Templates.Count + 1;
        var template = CreateNewTemplate($"Template {index}");
        Templates.Add(template);
        SelectedTemplate = template;
        StatusText = "Created template.";
    }

    private void DeleteTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        var result = AppDialog.Show(
            this,
            $"Delete template '{SelectedTemplate.Name}'?",
            "Delete building template",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var index = Templates.IndexOf(SelectedTemplate);
        Templates.Remove(SelectedTemplate);
        if (Templates.Count == 0)
        {
            Templates.Add(CreateNewTemplate("New template"));
        }

        SelectedTemplate = Templates[Math.Clamp(index, 0, Templates.Count - 1)];
        SaveAllTemplates(skipValidation: true);
        StatusText = "Deleted template.";
    }

    private void AddBuildingRowButton_Click(object sender, RoutedEventArgs e)
    {
        AddRowView(new BuildingTemplateRowView
        {
            Kind = "Building",
            SlotText = "Auto",
            TargetLevel = "1",
        });
    }

    private void AddAllResourcesRowButton_Click(object sender, RoutedEventArgs e)
    {
        AddRowView(new BuildingTemplateRowView
        {
            Kind = "Add resources",
            Target = ResourceOptions.FirstOrDefault(),
            SlotText = "Auto",
            TargetLevel = "1",
        });
    }

    private void BuildingOption_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem item
            || item.DataContext is not BuildingTemplateTargetOption option
            || option.Availability != BuildingTemplateAvailability.MissingRequirements
            || ItemsControl.ItemsControlFromItemContainer(item) is not ComboBox comboBox
            || comboBox.DataContext is not BuildingTemplateRowView targetRow
            || option.Gid is not int gid)
        {
            return;
        }

        e.Handled = true;
        comboBox.IsDropDownOpen = false;

        var targetIndex = Rows.IndexOf(targetRow);
        if (targetIndex < 0)
        {
            return;
        }

        var precedingRows = Rows.Take(targetIndex).Select(row => row.ToTemplateRow()).ToList();
        var reservedSlotId = int.TryParse(targetRow.SlotText, out var parsedSlot) ? parsedSlot : (int?)null;
        var prerequisitePlan = _planner.PlanMissingPrerequisites(
            gid,
            precedingRows,
            _status,
            _serverSpeed,
            _mainBuildingLevel,
            reservedSlotId);
        if (prerequisitePlan.Blockers.Count > 0)
        {
            AppDialog.Show(
                this,
                $"{option.Name} cannot be added because its prerequisite chain could not be created:\n\n{string.Join("\n", prerequisitePlan.Blockers)}",
                "Missing building requirements",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var requiredRows = prerequisitePlan.Rows
            .Select(row => $"  • {row.BuildingName} to level {row.TargetLevel}")
            .ToList();
        var message =
            $"{option.Name} cannot be selected yet because its requirements are not fulfilled.\n\n" +
            $"The following rows will be inserted before {option.Name}:\n{string.Join("\n", requiredRows)}\n\n" +
            "Build the required buildings first?";
        var choice = AppDialog.ShowCustom(
            this,
            message,
            "Missing building requirements",
            [("Build required buildings", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var prerequisite in prerequisitePlan.Rows)
        {
            var rowView = BuildingTemplateRowView.From(prerequisite, BuildingOptions, ResourceOptions);
            rowView.SetOptionSources(BuildingOptions, ResourceOptions);
            Rows.Insert(targetIndex++, rowView);
        }

        RefreshPlanPreview();
        var nowAvailable = targetRow.TargetOptionsView
            .Cast<BuildingTemplateTargetOption>()
            .FirstOrDefault(candidate => candidate.Gid == gid && candidate.IsSelectable);
        if (nowAvailable is null)
        {
            StatusText = $"Could not make {option.Name} available after inserting its requirements.";
            return;
        }

        targetRow.Target = nowAvailable;
        StatusText = $"Inserted {prerequisitePlan.Rows.Count} prerequisite row(s) before {option.Name}.";
    }

    private void RemoveRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is null)
        {
            return;
        }

        Rows.Remove(SelectedRow);
    }

    private void MoveRowUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedRow(-1);
    }

    private void MoveRowDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedRow(1);
    }

    private void MoveSelectedRow(int delta)
    {
        if (SelectedRow is null)
        {
            return;
        }

        var index = Rows.IndexOf(SelectedRow);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Rows.Count)
        {
            return;
        }

        Rows.Move(index, target);
        RefreshIndexes();
        RefreshPlanPreview();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAllTemplates(skipValidation: false))
        {
            return;
        }

        StatusText = "Saved template.";
    }

    private void QueueTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAllTemplates(skipValidation: false))
        {
            return;
        }

        var rows = BuildTemplateRowsFromUi();
        var plan = _planner.Plan(rows, _status, _serverSpeed, _mainBuildingLevel);
        if (plan.Errors.Count > 0)
        {
            StatusText = string.Join(" ", plan.Errors.Take(2));
            return;
        }

        if (plan.Actions.Count == 0)
        {
            StatusText = plan.Warnings.Count > 0
                ? string.Join(" ", plan.Warnings.Take(2))
                : "Template has nothing to queue.";
            return;
        }

        QueuePlan = plan;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private bool SaveAllTemplates(bool skipValidation)
    {
        if (SelectedTemplate is not null)
        {
            SelectedTemplate.Rows = BuildTemplateRowsFromUi().ToList();
            SelectedTemplate.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(SelectedTemplate.Name))
            {
                StatusText = "Template name is required.";
                return false;
            }
        }

        if (!skipValidation)
        {
            var plan = _planner.Plan(BuildTemplateRowsFromUi(), _status, _serverSpeed, _mainBuildingLevel);
            if (plan.Errors.Count > 0)
            {
                StatusText = string.Join(" ", plan.Errors.Take(2));
                RefreshPlanPreview(plan);
                return false;
            }
        }

        try
        {
            _store.Save(Templates.ToList());
            _templateLoadWarning = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save templates: {ex.Message}";
            return false;
        }
    }

    private IReadOnlyList<BuildingTemplateRow> BuildTemplateRowsFromUi()
        => Rows.Select(row => row.ToTemplateRow()).ToList();

    private void RefreshPlanPreview(BuildingTemplatePlanResult? existingPlan = null)
    {
        if (_isRefreshingPlanPreview)
        {
            return;
        }

        _isRefreshingPlanPreview = true;
        try
        {
            RefreshBuildingOptionAvailability();
            var plan = existingPlan ?? _planner.Plan(BuildTemplateRowsFromUi(), _status, _serverSpeed, _mainBuildingLevel);
            foreach (var row in Rows)
            {
                row.Status = string.Empty;
            }

            TotalWoodText = plan.Actions.Count > 0 ? QueueItemRowFactory.FormatResourceAmount(plan.Wood) : "-";
            TotalClayText = plan.Actions.Count > 0 ? QueueItemRowFactory.FormatResourceAmount(plan.Clay) : "-";
            TotalIronText = plan.Actions.Count > 0 ? QueueItemRowFactory.FormatResourceAmount(plan.Iron) : "-";
            TotalCropText = plan.Actions.Count > 0 ? QueueItemRowFactory.FormatResourceAmount(plan.Crop) : "-";
            TotalTimeText = plan.Actions.Count > 0
                ? $"Time {QueueItemRowFactory.FormatBuildDuration(plan.Seconds)}"
                : "Time -";
            TotalConstructFasterTimeText = plan.Actions.Count > 0
                ? $"Time (25%) {QueueItemRowFactory.FormatBuildDuration(plan.Seconds * 0.75)}"
                : "Time (25%) -";
            ValidationSummaryText = plan.Errors.Count > 0
                ? $"{plan.Errors.Count} error(s)"
                : plan.Warnings.Count > 0
                    ? $"{plan.Warnings.Count} warning(s)"
                    : string.Empty;
            StatusText = plan.Errors.Count > 0
                ? plan.Errors[0]
                : plan.Warnings.Count > 0
                    ? plan.Warnings[0]
                    : "Ready.";
            if (!string.IsNullOrWhiteSpace(_templateLoadWarning))
            {
                StatusText = $"{_templateLoadWarning} {StatusText}";
            }
        }
        finally
        {
            _isRefreshingPlanPreview = false;
        }
    }

    private void RefreshBuildingOptionAvailability()
    {
        for (var rowIndex = 0; rowIndex < Rows.Count; rowIndex++)
        {
            var row = Rows[rowIndex];
            if (!row.IsBuildingRow)
            {
                continue;
            }

            var precedingRows = Rows.Take(rowIndex).Select(item => item.ToTemplateRow()).ToList();
            var options = BuildingOptions.Select(option =>
            {
                if (option.Gid is not int gid)
                {
                    return option;
                }

                var result = _planner.EvaluateBuildingAvailability(
                    gid,
                    precedingRows,
                    _status,
                    _serverSpeed,
                    _mainBuildingLevel);
                return option with
                {
                    Availability = result.Availability,
                    AvailabilityReason = result.Reason,
                };
            }).ToList();
            row.SetOptionSources(options, ResourceOptions);
        }
    }

    private void RefreshIndexes()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Index = i + 1;
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static int CategorySortOrder(string? category)
        => category switch
        {
            "Infrastructure" => 0,
            "Military" => 1,
            "Resources" => 2,
            "Special buildings" => 3,
            "Wall" => 4,
            _ => 5,
        };

    private static string CategoryDisplayName(int gid, string? category, bool isSpecial)
    {
        if (IsWallGid(gid))
        {
            return "Wall";
        }

        if (isSpecial)
        {
            return "Special buildings";
        }

        return category switch
        {
            "infrastructure" => "Infrastructure",
            "army_buildings" => "Military",
            "resource_buildings" => "Resources",
            _ => "Other",
        };
    }

    private static int? FixedSlotFor(int gid)
        => gid switch
        {
            16 => 39,
            31 or 32 or 33 or 42 or 43 => 40,
            _ => null,
        };

    private static bool IsWallGid(int gid)
        => gid is 31 or 32 or 33 or 42 or 43;
}

public sealed record BuildingTemplateTargetOption(
    int? Gid,
    string Name,
    string Category,
    string? ResourceScope,
    int? FixedSlotId,
    BuildingTemplateAvailability Availability = BuildingTemplateAvailability.Available,
    string AvailabilityReason = "Available")
{
    public bool IsSelectable => Availability == BuildingTemplateAvailability.Available;
    public bool CanInvoke => Availability != BuildingTemplateAvailability.Unavailable;
}

public sealed class BuildingTemplateRowView : INotifyPropertyChanged
{
    private int _index;
    private string _kind = "Building";
    private BuildingTemplateTargetOption? _target;
    private string _slotText = "Auto";
    private string _targetLevel = "1";
    private string _status = string.Empty;
    private IReadOnlyList<BuildingTemplateTargetOption> _buildingOptions = [];
    private IReadOnlyList<BuildingTemplateTargetOption> _resourceOptions = [];
    private ICollectionView _targetOptionsView = CollectionViewSource.GetDefaultView(Array.Empty<BuildingTemplateTargetOption>());

    public Guid Id { get; init; } = Guid.NewGuid();

    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    public string Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsBuildingRow));
                OnPropertyChanged(nameof(IsSlotSelectable));
                RefreshTargetOptionsView();
                EnsureTargetMatchesKind();
            }
        }
    }

    public BuildingTemplateTargetOption? Target
    {
        get => _target;
        set
        {
            if (value is { IsSelectable: false })
            {
                return;
            }

            if (SetProperty(ref _target, value))
            {
                ApplyTargetSlotSelection(value);
                OnPropertyChanged(nameof(IsSlotSelectable));
                OnPropertyChanged(nameof(SlotOptions));
            }
        }
    }

    public string SlotText
    {
        get => _slotText;
        set => SetProperty(ref _slotText, string.IsNullOrWhiteSpace(value) ? "Auto" : value);
    }

    public string TargetLevel
    {
        get => _targetLevel;
        set => SetProperty(ref _targetLevel, string.IsNullOrWhiteSpace(value) ? "1" : value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public ICollectionView TargetOptionsView
    {
        get => _targetOptionsView;
        private set => SetProperty(ref _targetOptionsView, value);
    }

    public bool IsBuildingRow => string.Equals(Kind, "Building", StringComparison.OrdinalIgnoreCase);
    public bool IsSlotSelectable => IsBuildingRow && Target?.FixedSlotId is null;
    public IReadOnlyList<string> SlotOptions => Target?.FixedSlotId is int fixedSlot
        ? [fixedSlot.ToString()]
        : ["Auto", .. Enumerable.Range(19, 20).Select(item => item.ToString())];

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetOptionSources(
        IReadOnlyList<BuildingTemplateTargetOption> buildingOptions,
        IReadOnlyList<BuildingTemplateTargetOption> resourceOptions)
    {
        var currentGid = Target?.Gid;
        var currentResourceScope = Target?.ResourceScope;
        _buildingOptions = buildingOptions;
        _resourceOptions = resourceOptions;
        RefreshTargetOptionsView();

        var options = IsBuildingRow ? _buildingOptions : _resourceOptions;
        var matchingTarget = IsBuildingRow
            ? options.FirstOrDefault(item => item.Gid == currentGid)
            : options.FirstOrDefault(item => string.Equals(item.ResourceScope, currentResourceScope, StringComparison.OrdinalIgnoreCase));
        var selectedTarget = matchingTarget ?? options.FirstOrDefault(item => item.IsSelectable) ?? options.FirstOrDefault();
        if (!ReferenceEquals(_target, selectedTarget))
        {
            _target = selectedTarget;
            ApplyTargetSlotSelection(selectedTarget);
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(IsSlotSelectable));
            OnPropertyChanged(nameof(SlotOptions));
        }
    }

    public static BuildingTemplateRowView From(
        BuildingTemplateRow row,
        IReadOnlyList<BuildingTemplateTargetOption> buildingOptions,
        IReadOnlyList<BuildingTemplateTargetOption> resourceOptions)
    {
        var target = row.Kind == BuildingTemplateRowKind.AllResources
            ? resourceOptions.FirstOrDefault(item => string.Equals(item.ResourceScope, NormalizeResourceScope(row.ResourceScope), StringComparison.OrdinalIgnoreCase))
                ?? resourceOptions.FirstOrDefault()
            : null;
        target ??= row.Gid.HasValue
            ? buildingOptions.FirstOrDefault(item => item.Gid == row.Gid.Value)
            : null;
        target ??= !string.IsNullOrWhiteSpace(row.BuildingName)
            ? buildingOptions.FirstOrDefault(item => string.Equals(item.Name, row.BuildingName, StringComparison.OrdinalIgnoreCase))
            : null;
        target ??= row.Gid.HasValue
            ? new BuildingTemplateTargetOption(row.Gid.Value, row.BuildingName, "Other", null, FixedSlotFor(row.Gid.Value))
            : null;

        return new BuildingTemplateRowView
        {
            Kind = row.Kind == BuildingTemplateRowKind.AllResources ? "Add resources" : "Building",
            Target = target,
            SlotText = target?.FixedSlotId?.ToString() ?? row.PreferredSlotId?.ToString() ?? "Auto",
            TargetLevel = Math.Max(1, row.TargetLevel).ToString(),
        };
    }

    public BuildingTemplateRow ToTemplateRow()
    {
        var isAllResources = !IsBuildingRow;
        _ = int.TryParse(TargetLevel, out var targetLevel);
        int? slotId = int.TryParse(SlotText, out var parsedSlot) && parsedSlot is >= 19 and <= 40
            ? parsedSlot
            : null;
        return new BuildingTemplateRow
        {
            Id = Id,
            Kind = isAllResources ? BuildingTemplateRowKind.AllResources : BuildingTemplateRowKind.Building,
            Gid = isAllResources ? null : Target?.Gid,
            BuildingName = isAllResources ? Target?.Name ?? string.Empty : Target?.Name ?? string.Empty,
            PreferredSlotId = isAllResources ? null : slotId,
            TargetLevel = Math.Clamp(targetLevel, 1, 20),
            ResourceScope = isAllResources ? Target?.ResourceScope ?? "all" : "all",
            ResourceStrategy = "lowest",
        };
    }

    private void RefreshTargetOptionsView()
    {
        var options = IsBuildingRow ? _buildingOptions : _resourceOptions;
        var view = new ListCollectionView(options.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BuildingTemplateTargetOption.Category)));
        TargetOptionsView = view;
    }

    private void EnsureTargetMatchesKind()
    {
        var options = IsBuildingRow ? _buildingOptions : _resourceOptions;
        if (Target is null || !options.Any(item => Equals(item, Target)))
        {
            Target = options.FirstOrDefault(item => item.IsSelectable) ?? options.FirstOrDefault();
        }
    }

    private void ApplyTargetSlotSelection(BuildingTemplateTargetOption? target)
    {
        if (target?.FixedSlotId is int fixedSlot)
        {
            SlotText = fixedSlot.ToString();
            return;
        }

        if (!string.Equals(SlotText, "Auto", StringComparison.OrdinalIgnoreCase)
            && (!int.TryParse(SlotText, out var slotId) || slotId is < 19 or > 38))
        {
            SlotText = "Auto";
        }
    }

    private static int? FixedSlotFor(int gid)
        => gid switch
        {
            16 => 39,
            31 or 32 or 33 or 42 or 43 => 40,
            _ => null,
        };

    private static string NormalizeResourceScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "all";
        }

        if (value.Contains("Wood", StringComparison.OrdinalIgnoreCase)) return "wood";
        if (value.Contains("Clay", StringComparison.OrdinalIgnoreCase)) return "clay";
        if (value.Contains("Iron", StringComparison.OrdinalIgnoreCase)) return "iron";
        if (value.Contains("Crop", StringComparison.OrdinalIgnoreCase)) return "crop";
        return string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) ? "all" : value.Trim().ToLowerInvariant();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
