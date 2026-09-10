using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Views;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class BuildingTemplatesWindow : Window, INotifyPropertyChanged
{
    private readonly BuildingTemplateStore _store;
    private readonly BuildingTemplateExchangeService _exchangeService = new();
    private readonly BuildingTemplatePlanner _planner = new();
    private readonly string _projectRoot;
    private readonly VillageStatus _selectedVillageStatus;
    private readonly VillageStatus _status;
    private readonly IReadOnlyList<BuildingTemplateVillageTarget> _queueTargets;
    private readonly double _serverSpeed;
    private readonly int _mainBuildingLevel;
    private readonly int _storageUpgradeLevelsAhead;
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
    private bool _isLoadingTemplateRows;
    private CancellationTokenSource? _templateLoadCts;
    private readonly DispatcherTimer _planPreviewTimer;
    private readonly Dictionary<Guid, string> _dismissedStoragePrompts = [];
    private readonly HashSet<BuildingTemplateRowView> _pendingStorageCheckRows = [];
    private bool _isApplyingStoragePrerequisites;

    public ObservableCollection<BuildingTemplate> Templates { get; } = [];
    public ObservableCollection<BuildingTemplateRowView> Rows { get; } = [];
    public ObservableCollection<BuildingTemplateTargetOption> BuildingOptions { get; } = [];
    public ObservableCollection<BuildingTemplateTargetOption> ResourceOptions { get; } = [];
    public IReadOnlyList<string> RowKinds { get; } = ["Building", "Add resources"];
    public IReadOnlyList<string> LevelOptions { get; } =
        Enumerable.Range(1, 20).Select(item => item.ToString()).ToList();

    public BuildingTemplatePlanResult? QueuePlan { get; private set; }
    public IReadOnlyList<BuildingTemplateQueueSelection> QueueSelections { get; private set; } = [];

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
        IReadOnlyList<BuildingTemplateVillageTarget> queueTargets,
        double serverSpeed,
        int storageUpgradeLevelsAhead)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        DataContext = this;

        _projectRoot = projectRoot;
        _store = new BuildingTemplateStore(projectRoot);
        _selectedVillageStatus = status;
        _status = BuildingTemplateBaselineFactory.Create(status.Tribe);
        _queueTargets = queueTargets;
        _serverSpeed = serverSpeed;
        _mainBuildingLevel = 1;
        _storageUpgradeLevelsAhead = storageUpgradeLevelsAhead;

        Rows.CollectionChanged += Rows_CollectionChanged;
        _planPreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _planPreviewTimer.Tick += (_, _) =>
        {
            _planPreviewTimer.Stop();
            var storageCheckRows = Rows.Where(_pendingStorageCheckRows.Contains).ToList();
            _pendingStorageCheckRows.Clear();
            RefreshPlanPreview();
            foreach (var storageCheckRow in storageCheckRows)
            {
                OfferStoragePrerequisites(storageCheckRow);
            }
        };
        LoadBuildingOptions(status.Tribe);
        Loaded += BuildingTemplatesWindow_Loaded;
        Closed += BuildingTemplatesWindow_Closed;
    }

    private void ShowBuildingSlotsButton_Click(object sender, RoutedEventArgs e)
    {
        new BuildingSlotsWindow { Owner = this }.ShowDialog();
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

    private async void BuildingTemplatesWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= BuildingTemplatesWindow_Loaded;
        LoadingOverlay.Show("Building templates", "Loading templates...");
        var loadCts = new CancellationTokenSource();
        _templateLoadCts = loadCts;
        try
        {
            IReadOnlyList<BuildingTemplate> loadedTemplates;
            string? loadWarning = null;
            try
            {
                loadedTemplates = await _store.LoadAsync(loadCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                loadWarning = $"Could not load templates: {ex.Message}";
                loadedTemplates = [];
            }

            if (!IsLoaded || loadCts.IsCancellationRequested)
            {
                return;
            }

            Templates.Clear();
            _templateLoadWarning = loadWarning ?? _store.LastLoadWarning;
            foreach (var template in loadedTemplates)
            {
                Templates.Add(template);
            }

            var createdDefaultTemplate = Templates.Count == 0;
            if (createdDefaultTemplate)
            {
                Templates.Add(CreateNewTemplate("New template"));
            }

            SelectedTemplate = Templates[0];
            if (createdDefaultTemplate)
            {
                SaveAllTemplates(skipValidation: true);
            }
            _planPreviewTimer.Stop();
            RefreshPlanPreview();
            LoadingOverlay.Hide();
        }
        finally
        {
            if (ReferenceEquals(_templateLoadCts, loadCts))
            {
                _templateLoadCts = null;
            }

            loadCts.Dispose();
        }
    }

    private void LoadingOverlay_Cancelled(object sender, EventArgs e)
    {
        _templateLoadCts?.Cancel();
        Close();
    }

    private void BuildingTemplatesWindow_Closed(object? sender, EventArgs e)
    {
        _planPreviewTimer.Stop();
        _templateLoadCts?.Cancel();
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

        _isLoadingTemplateRows = true;
        Rows.CollectionChanged -= Rows_CollectionChanged;
        try
        {
            Rows.Clear();
            if (SelectedTemplate is not null)
            {
                foreach (var row in SelectedTemplate.Rows)
                {
                    var rowView = BuildingTemplateRowView.From(row, BuildingOptions, ResourceOptions);
                    rowView.SetOptionSources(BuildingOptions, ResourceOptions);
                    Rows.Add(rowView);
                }
            }
        }
        finally
        {
            Rows.CollectionChanged += Rows_CollectionChanged;
            _isLoadingTemplateRows = false;
        }

        foreach (var row in Rows)
        {
            row.PropertyChanged -= Row_PropertyChanged;
            row.PropertyChanged += Row_PropertyChanged;
        }

        RefreshIndexes();
        RequestPlanPreviewRefresh();
    }

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isLoadingTemplateRows)
        {
            return;
        }

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
                if (!_isApplyingStoragePrerequisites)
                {
                    _pendingStorageCheckRows.Add(row);
                }
            }
        }

        RefreshIndexes();
        RequestPlanPreviewRefresh();
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

        if (!_isApplyingStoragePrerequisites
            && sender is BuildingTemplateRowView row
            && e.PropertyName is nameof(BuildingTemplateRowView.Target) or nameof(BuildingTemplateRowView.TargetLevel))
        {
            _pendingStorageCheckRows.Add(row);
        }

        RequestPlanPreviewRefresh();
    }

    private void OfferStoragePrerequisites(BuildingTemplateRowView targetRow)
    {
        var targetIndex = Rows.IndexOf(targetRow);
        if (targetIndex < 0 || targetRow.Target is null)
        {
            return;
        }

        var precedingRows = Rows.Take(targetIndex).Select(row => row.ToTemplateRow()).ToList();
        var rowsThroughTarget = precedingRows.Append(targetRow.ToTemplateRow()).ToList();
        var precedingStorage = _planner.PlanStoragePrerequisites(
            precedingRows,
            _status,
            _serverSpeed,
            _mainBuildingLevel,
            _storageUpgradeLevelsAhead,
            enforceVillageLocationRules: false);
        var requiredStorage = _planner.PlanStoragePrerequisites(
            rowsThroughTarget,
            _status,
            _serverSpeed,
            _mainBuildingLevel,
            _storageUpgradeLevelsAhead,
            enforceVillageLocationRules: false);
        if (!string.IsNullOrWhiteSpace(requiredStorage.CannotPlanReason))
        {
            return;
        }

        var precedingTargets = precedingStorage.Rows
            .GroupBy(row => (row.Gid, row.PreferredSlotId))
            .ToDictionary(group => group.Key, group => group.Max(row => row.TargetLevel));
        var rowsToInsert = requiredStorage.Rows
            .Where(row => row.TargetLevel > precedingTargets.GetValueOrDefault((row.Gid, row.PreferredSlotId)))
            .ToList();
        if (rowsToInsert.Count == 0)
        {
            _dismissedStoragePrompts.Remove(targetRow.Id);
            return;
        }

        var signature = string.Join(
            ";",
            rowsThroughTarget.Select(row =>
                $"{row.Id:N}:{row.Kind}:{row.Gid}:{row.PreferredSlotId}:{row.TargetLevel}:{row.ResourceScope}:{row.ResourceStrategy}"));
        if (_dismissedStoragePrompts.TryGetValue(targetRow.Id, out var dismissedSignature)
            && string.Equals(dismissedSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        var targetName = targetRow.Target.Name;
        var requiredRows = string.Join(
            "\n",
            rowsToInsert.Select(row => $"  • {row.BuildingName} to level {row.TargetLevel}"));
        var choice = AppDialog.ShowCustom(
            this,
            $"{targetName} to level {targetRow.TargetLevel} needs more storage capacity.\n\n" +
            $"The following rows will be inserted immediately before {targetName}:\n{requiredRows}\n\n" +
            "Add required storage upgrades?",
            "Storage capacity required",
            [("Add required storage", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel,
            MessageBoxResult.Cancel);
        if (choice != MessageBoxResult.Yes)
        {
            _dismissedStoragePrompts[targetRow.Id] = signature;
            return;
        }

        _isApplyingStoragePrerequisites = true;
        try
        {
            foreach (var storageRow in rowsToInsert)
            {
                var rowView = BuildingTemplateRowView.From(storageRow, BuildingOptions, ResourceOptions);
                rowView.SetOptionSources(BuildingOptions, ResourceOptions);
                Rows.Insert(targetIndex++, rowView);
            }
        }
        finally
        {
            _isApplyingStoragePrerequisites = false;
        }

        _dismissedStoragePrompts.Remove(targetRow.Id);
        RefreshPlanPreview();
        StatusText = $"Inserted {rowsToInsert.Count} storage prerequisite row(s) before {targetName}.";
    }

    private void NewTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var index = Templates.Count + 1;
        var template = CreateNewTemplate($"Template {index}");
        Templates.Add(template);
        SelectedTemplate = template;
        StatusText = SaveAllTemplates(skipValidation: true)
            ? "Created and saved template."
            : StatusText;
    }

    private void DuplicateTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is null)
        {
            return;
        }

        var duplicate = CreateDuplicateTemplate(
            SelectedTemplate,
            BuildTemplateRowsFromUi(),
            Templates);
        var sourceIndex = Templates.IndexOf(SelectedTemplate);
        Templates.Insert(sourceIndex + 1, duplicate);
        SelectedTemplate = duplicate;
        StatusText = SaveAllTemplates(skipValidation: true)
            ? "Duplicated and saved template."
            : StatusText;
    }

    private void ImportTemplatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAllTemplates(skipValidation: true))
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import building templates",
            Filter = "Tbot Ultra templates (*.tbot-template.json)|*.tbot-template.json|JSON files (*.json)|*.json",
            DefaultExt = BuildingTemplateExchangeService.FileExtension,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var candidates = _exchangeService.Import(dialog.FileName, _status.Tribe);
            var preview = new BuildingTemplateImportWindow(
                candidates,
                Templates.Select(template => template.Id).ToHashSet())
            {
                Owner = this,
            };
            if (preview.ShowDialog() != true)
            {
                return;
            }

            var result = _exchangeService.ApplyImport(Templates.ToList(), preview.Selections, DateTimeOffset.UtcNow);
            _store.Save(result.Templates);

            SelectedTemplate = null;
            Templates.Clear();
            foreach (var template in result.Templates)
            {
                Templates.Add(template);
            }

            SelectedTemplate = result.ImportedTemplateIds.Count > 0
                ? Templates.FirstOrDefault(template => template.Id == result.ImportedTemplateIds[0])
                : Templates.FirstOrDefault();
            StatusText = $"Imported {result.ImportedCount}, overwritten {result.OverwrittenCount}, copied {result.CopiedCount}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppDialog.Show(
                this,
                ex.Message,
                "Import building templates",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ExportTemplatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ExportSelectedTemplateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is null)
        {
            StatusText = "Select a template to export.";
            return;
        }

        ExportTemplates([SelectedTemplate], BuildingTemplateStore.SanitizeTemplateFileName(SelectedTemplate.Name));
    }

    private void ExportAllTemplatesMenuItem_Click(object sender, RoutedEventArgs e)
        => ExportTemplates(Templates.ToList(), $"building-templates-{DateTime.Now:yyyyMMdd}");

    private void ExportTemplates(IReadOnlyList<BuildingTemplate> templates, string suggestedName)
    {
        if (!SaveAllTemplates(skipValidation: true))
        {
            return;
        }

        if (templates.Count == 0)
        {
            StatusText = "There are no templates to export.";
            return;
        }

        try
        {
            Directory.CreateDirectory(_store.DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not open the building templates folder: {ex.Message}";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export building templates",
            FileName = suggestedName + BuildingTemplateExchangeService.FileExtension,
            Filter = "Tbot Ultra templates (*.tbot-template.json)|*.tbot-template.json",
            DefaultExt = BuildingTemplateExchangeService.FileExtension,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = _store.DirectoryPath,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var appVersion = UpdateChecker.ReadCurrentVersion(Path.Combine(_projectRoot, "VERSION"));
            _exchangeService.Export(dialog.FileName, templates, appVersion, DateTimeOffset.UtcNow);
            StatusText = $"Exported {templates.Count} template(s) to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppDialog.Show(
                this,
                ex.Message,
                "Export building templates",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    internal static BuildingTemplate CreateDuplicateTemplate(
        BuildingTemplate source,
        IReadOnlyList<BuildingTemplateRow> currentRows,
        IReadOnlyCollection<BuildingTemplate> existingTemplates)
    {
        var sourceName = string.IsNullOrWhiteSpace(source.Name) ? "Template" : source.Name.Trim();
        var copyName = $"{sourceName} copy";
        var copyNumber = 2;
        while (existingTemplates.Any(template =>
                   string.Equals(template.Name, copyName, StringComparison.OrdinalIgnoreCase)))
        {
            copyName = $"{sourceName} copy {copyNumber++}";
        }

        var now = DateTimeOffset.UtcNow;
        return new BuildingTemplate
        {
            Name = copyName,
            CreatedByTribe = source.CreatedByTribe,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Rows = currentRows.Select(row => new BuildingTemplateRow
            {
                Kind = row.Kind,
                Gid = row.Gid,
                BuildingName = row.BuildingName,
                PreferredSlotId = row.PreferredSlotId,
                TargetLevel = row.TargetLevel,
                ResourceScope = row.ResourceScope,
                ResourceStrategy = row.ResourceStrategy,
            }).ToList(),
        };
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
            reservedSlotId,
            enforceVillageLocationRules: false);
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

        RefreshBuildingOptionAvailability(targetRow);
        var nowAvailable = targetRow.TargetOptionsView
            .Cast<BuildingTemplateTargetOption>()
            .FirstOrDefault(candidate => candidate.Gid == gid && candidate.IsSelectable);
        if (nowAvailable is null)
        {
            StatusText = $"Could not make {option.Name} available after inserting its requirements.";
            return;
        }

        targetRow.Target = nowAvailable;
        RefreshPlanPreview();
        StatusText = $"Inserted {prerequisitePlan.Rows.Count} prerequisite row(s) before {option.Name}.";
    }

    private void DeleteRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BuildingTemplateRowView row })
        {
            RemoveTemplateRow(row);
        }
    }

    private void RemoveTemplateRow(BuildingTemplateRowView row)
    {
        var rowIndex = Rows.IndexOf(row);
        if (rowIndex < 0)
        {
            return;
        }

        var rows = BuildTemplateRowsFromUi();
        var losses = _planner.FindLaterRowsLosingRequirementsAfterRemoval(
            rows,
            rowIndex,
            _status,
            _serverSpeed,
            _mainBuildingLevel,
            enforceVillageLocationRules: false);
        if (losses.Count > 0)
        {
            var affectedRows = string.Join(
                "\n",
                losses.Select(loss => $"• {loss.BuildingName}: {loss.Reason}"));
            var choice = AppDialog.ShowCustom(
                this,
                "Deleting this row removes a prerequisite for later building rows:\n\n"
                + $"{affectedRows}\n\nDelete anyway?",
                "Delete building template row",
                [("Delete anyway", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel,
                MessageBoxResult.Cancel,
                dangerResult: MessageBoxResult.Yes);
            if (choice != MessageBoxResult.Yes)
            {
                return;
            }
        }

        Rows.Remove(row);
    }

    private void MoveRowUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedRow(-1);
    }

    private void MoveRowDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedRow(1);
    }

    private void BuildingOptions_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is not ComboBox { DataContext: BuildingTemplateRowView row } || !row.IsBuildingRow)
        {
            return;
        }

        RefreshBuildingOptionAvailability(row);
    }

    private void MoveRowTopButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedRowTo(0);
    }

    private void MoveRowBottomButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedRowTo(Rows.Count - 1);
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
    }

    private void MoveSelectedRowTo(int target)
    {
        if (SelectedRow is null)
        {
            return;
        }

        var index = Rows.IndexOf(SelectedRow);
        if (index < 0 || target < 0 || target >= Rows.Count || index == target)
        {
            return;
        }

        Rows.Move(index, target);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPrepareTemplateForSave()
            || !SaveAllTemplates(skipValidation: false))
        {
            return;
        }

        StatusText = "Saved template.";
    }

    private bool TryPrepareTemplateForSave()
    {
        _planPreviewTimer.Stop();
        _pendingStorageCheckRows.Clear();
        var rows = BuildTemplateRowsFromUi();
        var plan = _planner.Plan(rows, _status, _serverSpeed, _mainBuildingLevel, enforceVillageLocationRules: false);
        if (plan.Errors.Count > 0)
        {
            StatusText = string.Join(" ", plan.Errors.Take(2));
            RefreshPlanPreview(plan);
            AppDialog.Show(
                this,
                string.Join("\n", plan.Errors),
                "Cannot save template",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var storagePlan = _planner.PlanStoragePrerequisiteInsertions(
            rows,
            _status,
            _serverSpeed,
            _mainBuildingLevel,
            _storageUpgradeLevelsAhead,
            enforceVillageLocationRules: false);
        if (!string.IsNullOrWhiteSpace(storagePlan.CannotPlanReason))
        {
            StatusText = storagePlan.CannotPlanReason;
            AppDialog.Show(
                this,
                $"The storage requirement could not be planned safely.\n\n{storagePlan.CannotPlanReason}",
                "Cannot save template",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (storagePlan.Insertions.Count == 0)
        {
            return true;
        }

        var upgrades = storagePlan.Insertions.SelectMany(insertion => insertion.Upgrades).ToList();
        var bufferText = _storageUpgradeLevelsAhead > ConstructionDefaults.StorageUpgradeLevelsAhead
            ? $" Construction setting: {_storageUpgradeLevelsAhead} storage levels ahead."
            : string.Empty;
        var content = new StoragePreflightPlanView(
            "This template needs Warehouse and/or Granary rows before the affected resource or building actions. " +
            "All required storage actions are shown together below." + bufferText,
            StoragePreflightPlanView.CreateStages(upgrades));
        var choice = AppDialog.ShowCustomContent(
            this,
            content,
            "Storage upgrades required",
            [("Add required storage upgrades", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Warning,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel,
            successResult: MessageBoxResult.Yes,
            width: 600);
        if (choice != MessageBoxResult.Yes)
        {
            StatusText = "Template was not saved.";
            return false;
        }

        _isApplyingStoragePrerequisites = true;
        try
        {
            foreach (var insertion in storagePlan.Insertions)
            {
                var targetRow = Rows.FirstOrDefault(row => row.Id == insertion.BeforeRowId);
                if (targetRow is null)
                {
                    StatusText = $"Could not locate {insertion.TargetName} while inserting storage prerequisites.";
                    return false;
                }
                var targetIndex = Rows.IndexOf(targetRow);

                foreach (var storageRow in insertion.Rows)
                {
                    var rowView = BuildingTemplateRowView.From(storageRow, BuildingOptions, ResourceOptions);
                    rowView.SetOptionSources(BuildingOptions, ResourceOptions);
                    Rows.Insert(targetIndex++, rowView);
                }

                _dismissedStoragePrompts.Remove(insertion.BeforeRowId);
            }
        }
        finally
        {
            _isApplyingStoragePrerequisites = false;
        }

        RefreshPlanPreview();
        StatusText = $"Inserted {storagePlan.Insertions.Sum(item => item.Rows.Count)} storage prerequisite row(s). Review the template and click Save again.";
        return false;
    }

    private void QueueTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAllTemplates(skipValidation: true))
        {
            return;
        }

        var rows = BuildTemplateRowsFromUi();
        var selectedTarget = _queueTargets.FirstOrDefault();
        var targetStatus = selectedTarget?.PlanningStatus ?? _selectedVillageStatus;
        var sourceStatus = selectedTarget?.SourceStatus ?? _selectedVillageStatus;
        var mainBuildingLevel = targetStatus.Buildings
            .Where(building => building.Gid == 15 || string.Equals(building.Name, "Main Building", StringComparison.OrdinalIgnoreCase))
            .Select(building => building.Level ?? 0)
            .DefaultIfEmpty(1)
            .Max();
        var plan = _planner.Plan(rows, targetStatus, _serverSpeed, Math.Max(1, mainBuildingLevel));
        if (plan.Errors.Count > 0)
        {
            var message = string.Join("\n", plan.Errors);
            StatusText = string.Join(" ", plan.Errors.Take(2));
            AppDialog.Show(
                this,
                message,
                "Cannot queue template",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (plan.Actions.Count == 0)
        {
            StatusText = plan.Warnings.Count > 0
                ? string.Join(" ", plan.Warnings.Take(2))
                : "Template has nothing to queue.";
            AppDialog.Show(
                this,
                StatusText,
                "Cannot queue template",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        QueuePlan = plan;
        if (selectedTarget is not null)
        {
            QueueSelections =
            [
                new BuildingTemplateQueueSelection(
                    selectedTarget.Village,
                    sourceStatus,
                    selectedTarget.ExistingQueueItems,
                    plan),
            ];
        }
        DialogResult = true;
    }

    private void QueueMultipleVillagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAllTemplates(skipValidation: true))
        {
            return;
        }

        var window = new BuildingTemplateVillageQueueWindow(_queueTargets, BuildTemplateRowsFromUi(), _serverSpeed)
        {
            Owner = this,
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        QueueSelections = window.Selections;
        QueuePlan = QueueSelections.FirstOrDefault()?.Plan;
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
            var plan = _planner.Plan(BuildTemplateRowsFromUi(), _status, _serverSpeed, _mainBuildingLevel, enforceVillageLocationRules: false);
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

    private void RequestPlanPreviewRefresh()
    {
        _planPreviewTimer.Stop();
        _planPreviewTimer.Start();
    }

    private void RefreshPlanPreview(BuildingTemplatePlanResult? existingPlan = null)
    {
        if (_isRefreshingPlanPreview)
        {
            return;
        }

        _isRefreshingPlanPreview = true;
        try
        {
            var plan = existingPlan ?? _planner.Plan(BuildTemplateRowsFromUi(), _status, _serverSpeed, _mainBuildingLevel, enforceVillageLocationRules: false);
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

    private void RefreshBuildingOptionAvailability(BuildingTemplateRowView row)
    {
        var rowIndex = Rows.IndexOf(row);
        if (rowIndex < 0)
        {
            return;
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
                _mainBuildingLevel,
                enforceVillageLocationRules: false);
            return option with
            {
                Availability = result.Availability,
                AvailabilityReason = result.Reason,
            };
        }).ToList();
        row.SetOptionSources(options, ResourceOptions);
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
    private string _resourceStrategy = "lowest";
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

    public string ResourceStrategy
    {
        get => _resourceStrategy;
        set => SetProperty(ref _resourceStrategy, string.IsNullOrWhiteSpace(value) ? "lowest" : value);
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
            ResourceStrategy = row.ResourceStrategy,
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
            ResourceStrategy = ResourceStrategy,
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
