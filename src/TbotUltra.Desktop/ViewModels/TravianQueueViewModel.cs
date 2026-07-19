using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the in-game build/smithy queue display: owns the two
/// ObservableCollections the queue DataGrids render and the in-place reconcile
/// that keeps existing row instances stable (rows are updated via ApplySnapshot
/// instead of being replaced, so DataGrid selection/scroll never jumps).
/// Snapshot production stays with the caller (LiveQueueRowFactory).
/// </summary>
public sealed class TravianQueueViewModel : BaseViewModel
{
    /// <summary>Rows shown in the construction-queue DataGrid.</summary>
    public ObservableCollection<TravianBuildQueueRow> BuildQueueRows { get; } = [];

    /// <summary>Rows shown in the smithy-queue DataGrid.</summary>
    public ObservableCollection<TravianSmithyQueueRow> SmithyQueueRows { get; } = [];

    public void ApplyBuildQueueRows(IReadOnlyList<TravianBuildQueueRow> rows)
        => Reconcile(BuildQueueRows, rows, (target, source) => target.ApplySnapshot(source));

    public void ApplySmithyQueueRows(IReadOnlyList<TravianSmithyQueueRow> rows)
        => Reconcile(SmithyQueueRows, rows, (target, source) => target.ApplySnapshot(source));

    private static void Reconcile<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> rows,
        Action<T, T> applySnapshot)
    {
        var sharedCount = Math.Min(target.Count, rows.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            applySnapshot(target[index], rows[index]);
        }

        while (target.Count > rows.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (var index = sharedCount; index < rows.Count; index++)
        {
            target.Add(rows[index]);
        }
    }
}
