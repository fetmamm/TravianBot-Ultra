using System.Collections.ObjectModel;
using System.Linq;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the farm-lists panel: owns the farm-list status rows,
/// the placeholder-row invariant and the status line. The send/refresh/scan
/// logic stays in MainWindow code-behind and mutates the collection in place.
/// </summary>
public sealed class FarmListsViewModel : BaseViewModel
{
    /// <summary>
    /// Farm-list status rows shown on the farming tab. Created once and mutated
    /// in place so the panel's ItemsSource assignment stays stable.
    /// </summary>
    public ObservableCollection<FarmListStatusRow> FarmLists { get; } = [];

    public static bool IsRealRow(FarmListStatusRow row) => !row.IsPlaceholder;

    /// <summary>
    /// Keeps exactly one placeholder row while no real farm lists are loaded,
    /// and none once real rows exist.
    /// </summary>
    public void EnsurePlaceholderRow()
    {
        if (FarmLists.Any(IsRealRow))
        {
            foreach (var row in FarmLists.Where(row => row.IsPlaceholder).ToList())
            {
                FarmLists.Remove(row);
            }

            return;
        }

        if (!FarmLists.Any(row => row.IsPlaceholder))
        {
            FarmLists.Add(new FarmListStatusRow
            {
                IsPlaceholder = true,
                IsEnabled = false,
            });
        }
    }

    /// <summary>Status line for the farming tab under the current rows.</summary>
    public string DescribeStatus()
    {
        var realFarmLists = FarmLists.Where(IsRealRow).ToList();
        if (realFarmLists.Count <= 0)
        {
            return "No farm lists loaded. Click Analyze Farmlists.";
        }

        var readyCount = realFarmLists.Count(item => item.IsReady && !item.IsEmpty);
        return $"Loaded {realFarmLists.Count} farm list(s). Ready: {readyCount}.";
    }
}
