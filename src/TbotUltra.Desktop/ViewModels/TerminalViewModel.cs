using System.Collections.ObjectModel;
using TbotUltra.Desktop.Common;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services.Logging;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the terminal/log output: owns the terminal entry
/// collection and the clean-mode/category filter state with its visibility
/// rule. The filtered <c>ICollectionView</c> plumbing and append/trim stay in
/// MainWindow code-behind and mutate the collection in place.
/// </summary>
public sealed class TerminalViewModel : BaseViewModel
{
    private LogCategory _filterCategory = LogCategory.All;
    private bool _cleanMode = true;

    /// <summary>
    /// Terminal log entries. Created once and mutated in place so the default
    /// collection view bound to the terminal stays stable.
    /// </summary>
    public ObservableCollection<TerminalEntryRow> Entries { get; } = [];

    /// <summary>Selected log-category view; All shows every category.</summary>
    public LogCategory FilterCategory
    {
        get => _filterCategory;
        set => SetProperty(ref _filterCategory, value);
    }

    /// <summary>Clean mode hides verbose rows (and noisy alarm rows via the caller's classifier).</summary>
    public bool CleanMode
    {
        get => _cleanMode;
        set => SetProperty(ref _cleanMode, value);
    }

    /// <summary>
    /// Visibility rule for a terminal row under the current filter state. The Pacing
    /// view is an explicit opt-in to the high-volume session/action/wait lines, which
    /// are otherwise verbose. When it is selected, verbose rows show even in Clean
    /// mode — Clean would hide almost all of them and the tab would look empty.
    /// </summary>
    public bool ShouldShow(TerminalEntryRow row)
    {
        var pacingViewSelected = FilterCategory == LogCategory.Pacing;
        if (CleanMode && row.IsVerbose && !pacingViewSelected)
        {
            return false;
        }

        return FilterCategory == LogCategory.All || row.Category == FilterCategory;
    }
}
