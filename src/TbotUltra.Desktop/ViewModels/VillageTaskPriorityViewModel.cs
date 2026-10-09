using System.Collections.ObjectModel;
using System.Windows.Input;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.ViewModels;

public sealed record VillageTaskPriorityRow(string Key, string Title, string Description, bool IsAutomationEnabled);

public sealed class VillageTaskPriorityViewModel
{
    private readonly RelayCommand<VillageTaskPriorityRow> _moveUpCommand;
    private readonly RelayCommand<VillageTaskPriorityRow> _moveDownCommand;
    private readonly RelayCommand _resetCommand;

    public VillageTaskPriorityViewModel()
    {
        _moveUpCommand = new RelayCommand<VillageTaskPriorityRow>(row => Move(row, -1), row => Rows.IndexOf(row) > 0);
        _moveDownCommand = new RelayCommand<VillageTaskPriorityRow>(row => Move(row, 1), row =>
            Rows.IndexOf(row) >= 0 && Rows.IndexOf(row) < Rows.Count - 1);
        _resetCommand = new RelayCommand(ResetToDefault);
    }

    public ObservableCollection<VillageTaskPriorityRow> Rows { get; } = [];
    public bool IsEditable { get; set; } = true;
    public ICommand MoveUpCommand => _moveUpCommand;
    public ICommand MoveDownCommand => _moveDownCommand;
    public ICommand ResetCommand => _resetCommand;
    public IReadOnlyList<string> OrderKeys => Rows.Select(row => row.Key).ToList();
    public event Action? Changed;

    public void Load(IEnumerable<string>? order, IReadOnlyCollection<string>? enabledGroups)
    {
        var enabled = new HashSet<string>(enabledGroups ?? [], StringComparer.OrdinalIgnoreCase);
        Rows.Clear();
        foreach (var key in VillageTaskPriorityOrder.Resolve(order))
        {
            QueueGroupCatalog.TryParse(key, out var group);
            Rows.Add(new VillageTaskPriorityRow(
                key,
                QueueGroupCatalog.GetTitle(group),
                QueueGroupCatalog.GetDescription(group),
                enabled.Contains(key) || group is QueueGroup.NpcTrade or QueueGroup.Demolish));
        }
        _moveUpCommand.RaiseCanExecuteChanged();
        _moveDownCommand.RaiseCanExecuteChanged();
    }

    public void ResetToDefault()
    {
        var enabled = Rows.Where(row => row.IsAutomationEnabled)
            .Select(row => row.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (OrderKeys.SequenceEqual(VillageTaskPriorityOrder.DefaultKeys, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Load(VillageTaskPriorityOrder.DefaultKeys, enabled);
        Changed?.Invoke();
    }

    private void Move(VillageTaskPriorityRow row, int offset)
    {
        var oldIndex = Rows.IndexOf(row);
        var newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Rows.Count)
        {
            return;
        }

        Rows.Move(oldIndex, newIndex);
        _moveUpCommand.RaiseCanExecuteChanged();
        _moveDownCommand.RaiseCanExecuteChanged();
        Changed?.Invoke();
    }
}
