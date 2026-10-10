using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public sealed record QueueDisplayRows(
    IReadOnlyList<QueueItemRow> ActiveRows,
    IReadOnlyList<QueueItem> HistoryItems);

public static class QueueDisplayProjection
{
    public static QueueDisplayRows Build(
        IReadOnlyList<QueueItem> items,
        Func<QueueItem, QueueItemRow> createActiveRow)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(createActiveRow);

        var activeRows = new List<QueueItemRow>();
        var historyItems = new List<QueueItem>();
        foreach (var item in items)
        {
            if (IsActive(item))
            {
                activeRows.Add(createActiveRow(item));
            }
            else if (IsHistory(item))
            {
                historyItems.Add(item);
            }
        }

        return new QueueDisplayRows(activeRows, historyItems);
    }

    private static bool IsActive(QueueItem item) =>
        item.Status is QueueStatus.Pending or QueueStatus.Running or QueueStatus.Paused;

    private static bool IsHistory(QueueItem item) =>
        item.Group == QueueGroup.Construction
        && item.Status is QueueStatus.Succeeded or QueueStatus.Canceled or QueueStatus.Failed;
}
