using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Resources tab. First slice of the Resources MVVM
/// migration — owns just the four per-resource collections that the XAML
/// columns bind to (Wood / Clay / Iron / Cropland). Subsequent commits
/// will fold the pending-target / click-cooldown dictionaries, the active
/// village max-level int, and the pure-logic helpers
/// (RepopulateResourceGroups, GetBucket, ApplyResourceStatusToUi,
/// ResolveQueuedResourceTarget, etc.) here too. Async / service-bound
/// methods will stay on MainWindow.
/// </summary>
public sealed class ResourcesViewModel : BaseViewModel
{
    /// <summary>Resource fields grouped into the Wood column on the Resources tab.</summary>
    public ObservableCollection<ResourceFieldRow> WoodFields { get; } = [];

    /// <summary>Resource fields grouped into the Clay column on the Resources tab.</summary>
    public ObservableCollection<ResourceFieldRow> ClayFields { get; } = [];

    /// <summary>Resource fields grouped into the Iron column on the Resources tab.</summary>
    public ObservableCollection<ResourceFieldRow> IronFields { get; } = [];

    /// <summary>Resource fields grouped into the Cropland column on the Resources tab.</summary>
    public ObservableCollection<ResourceFieldRow> CroplandFields { get; } = [];
}
