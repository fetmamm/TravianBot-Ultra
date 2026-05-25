using TbotUltra.Desktop.Common;

namespace TbotUltra.Desktop.ViewModels;

/// <summary>
/// View model backing the Messages / Reports panel and the sidebar
/// "Messages / Reports" badge. State is just two integer counters; all
/// display strings and the unread-flag are computed.
///
/// Mark-as-read button click handlers stay on MainWindow because they
/// drive _botService and the operation-tracking helpers; the panel is
/// not (yet) extracted to a UserControl, so the buttons keep their
/// existing Click handlers and read this view-model only for display.
/// </summary>
public sealed class InboxViewModel : BaseViewModel
{
    private int _unreadMessages;
    private int _unreadReports;
    private bool _autoReadMessages;
    private bool _autoReadReports;

    /// <summary>Number of unread game messages last reported by the worker.</summary>
    public int UnreadMessages
    {
        get => _unreadMessages;
        set
        {
            if (SetProperty(ref _unreadMessages, value))
            {
                OnPropertyChanged(nameof(MessageUnreadText));
                OnPropertyChanged(nameof(NavTooltip));
                OnPropertyChanged(nameof(HasUnreadMessages));
            }
        }
    }

    /// <summary>Number of unread reports last reported by the worker.</summary>
    public int UnreadReports
    {
        get => _unreadReports;
        set
        {
            if (SetProperty(ref _unreadReports, value))
            {
                OnPropertyChanged(nameof(ReportsUnreadText));
                OnPropertyChanged(nameof(NavTooltip));
            }
        }
    }

    /// <summary>"Unread: N" caption shown on the Messages card.</summary>
    public string MessageUnreadText => $"Unread: {_unreadMessages}";

    /// <summary>"Unread: N" caption shown on the Reports card.</summary>
    public string ReportsUnreadText => $"Unread: {_unreadReports}";

    /// <summary>"Messages N | Reports N" tooltip shown on the sidebar nav button.</summary>
    public string NavTooltip => $"Messages {_unreadMessages} | Reports {_unreadReports}";

    /// <summary>
    /// True when there are unread messages. The sidebar nav button uses this
    /// via a Style.DataTrigger to swap to the red-on-white badge look.
    /// </summary>
    public bool HasUnreadMessages => _unreadMessages > 0;

    public bool AutoReadMessages
    {
        get => _autoReadMessages;
        set => SetProperty(ref _autoReadMessages, value);
    }

    public bool AutoReadReports
    {
        get => _autoReadReports;
        set => SetProperty(ref _autoReadReports, value);
    }
}
