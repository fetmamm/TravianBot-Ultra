using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TbotUltra.Desktop;

public partial class AppDialog : Window
{
    private readonly MessageBoxButton _buttons;
    private readonly MessageBoxResult _defaultResult;
    private readonly MessageBoxResult _cancelResult;
    private readonly IReadOnlyList<(string Label, MessageBoxResult Result)>? _customButtons;
    private MessageBoxResult _result;
    private bool _resultChosen;

    private AppDialog(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult,
        MessageBoxResult? cancelResult = null,
        IReadOnlyList<(string Label, MessageBoxResult Result)>? customButtons = null)
    {
        InitializeComponent();

        Owner = owner;
        Title = string.IsNullOrWhiteSpace(title) ? "Dialog" : title;
        MessageTextBlock.Text = message ?? string.Empty;
        _buttons = buttons;
        _customButtons = customButtons;
        _defaultResult = defaultResult;
        _cancelResult = ResolveCancelResult(buttons, cancelResult, customButtons);
        _result = ResolveDefaultResult(buttons, defaultResult, customButtons);
        ApplyIcon(icon);
        BuildButtons();
    }

    private AppDialog(
        Window? owner,
        object content,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult,
        MessageBoxResult? cancelResult = null)
        : this(owner, string.Empty, title, buttons, icon, defaultResult, cancelResult)
    {
        MessageContentControl.Content = content;
    }

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon)
    {
        return Show(owner: null, message, title, buttons, icon, ResolveDefaultResult(buttons, MessageBoxResult.None));
    }

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        return Show(owner: null, message, title, buttons, icon, defaultResult);
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon)
    {
        return Show(owner, message, title, buttons, icon, ResolveDefaultResult(buttons, MessageBoxResult.None));
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        var dialog = new AppDialog(owner, message, title, buttons, icon, defaultResult);
        _ = dialog.ShowDialog();
        return dialog._result;
    }

    public static AppDialog ShowModeless(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon)
    {
        var dialog = new AppDialog(owner, message, title, buttons, icon, ResolveDefaultResult(buttons, MessageBoxResult.None));
        dialog.Show();
        return dialog;
    }

    public static AppDialog ShowModelessContent(
        Window? owner,
        object content,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        var dialog = new AppDialog(owner, content, title, buttons, icon, defaultResult);
        dialog.Show();
        return dialog;
    }

    public static MessageBoxResult ShowCustom(
        Window? owner,
        string message,
        string title,
        IReadOnlyList<(string Label, MessageBoxResult Result)> buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult,
        MessageBoxResult cancelResult)
    {
        var dialog = new AppDialog(
            owner,
            message,
            title,
            MessageBoxButton.OK,
            icon,
            defaultResult,
            cancelResult,
            buttons);
        _ = dialog.ShowDialog();
        return dialog._result;
    }

    public static MessageBoxResult ShowContent(
        Window? owner,
        object content,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        var dialog = new AppDialog(owner, content, title, buttons, icon, defaultResult);
        _ = dialog.ShowDialog();
        return dialog._result;
    }

    private void BuildButtons()
    {
        ButtonsPanel.Children.Clear();
        var buttons = ResolveButtonSet(_buttons, _customButtons);
        var defaultResult = ResolveDefaultResult(_buttons, _defaultResult, _customButtons);
        foreach (var (label, result) in buttons)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 88,
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(12, 0, 12, 0),
                IsDefault = result == defaultResult,
                IsCancel = result == _cancelResult,
            };

            button.Click += (_, _) =>
            {
                _result = result;
                _resultChosen = true;
                DialogResult = true;
                Close();
            };

            ButtonsPanel.Children.Add(button);
        }
    }

    private void ApplyIcon(MessageBoxImage icon)
    {
        var (glyph, fg, bg) = icon switch
        {
            MessageBoxImage.Warning => ("!", ThemeColors.Get("WarningTextDeepBrush"), ThemeColors.Get("WarningBgBrush")),
            MessageBoxImage.Error => ("x", ThemeColors.Get("DangerTextBrush"), ThemeColors.Get("DangerBgBrush")),
            MessageBoxImage.Question => ("?", ThemeColors.Get("InfoTextStrongBrush"), ThemeColors.Get("InfoBgBrush")),
            MessageBoxImage.Information => ("i", ThemeColors.Get("InfoTextStrongBrush"), ThemeColors.Get("InfoBgBrush")),
            _ => ("i", ThemeColors.Get("DataGridCellTextBrush"), ThemeColors.Get("ControlBackgroundBrush")),
        };

        IconTextBlock.Text = glyph;
        IconTextBlock.Foreground = new SolidColorBrush(fg);
        if (IconTextBlock.Parent is Border border)
        {
            border.Background = new SolidColorBrush(bg);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        _result = _cancelResult;
        _resultChosen = true;
        DialogResult = false;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_resultChosen)
        {
            return;
        }

        _result = _cancelResult;
    }

    private static IReadOnlyList<(string label, MessageBoxResult result)> ResolveButtonSet(
        MessageBoxButton buttons,
        IReadOnlyList<(string Label, MessageBoxResult Result)>? customButtons = null)
    {
        if (customButtons is not null && customButtons.Count > 0)
        {
            return customButtons.Select(item => (item.Label, item.Result)).ToList();
        }

        return buttons switch
        {
            MessageBoxButton.OKCancel => [("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            _ => [("OK", MessageBoxResult.OK)],
        };
    }

    private static MessageBoxResult ResolveCancelResult(
        MessageBoxButton buttons,
        MessageBoxResult? configuredCancel = null,
        IReadOnlyList<(string Label, MessageBoxResult Result)>? customButtons = null)
    {
        var allowed = ResolveButtonSet(buttons, customButtons).Select(item => item.result).ToHashSet();
        if (configuredCancel is not null && allowed.Contains(configuredCancel.Value))
        {
            return configuredCancel.Value;
        }

        return buttons switch
        {
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
    }

    private static MessageBoxResult ResolveDefaultResult(
        MessageBoxButton buttons,
        MessageBoxResult configuredDefault,
        IReadOnlyList<(string Label, MessageBoxResult Result)>? customButtons = null)
    {
        var allowed = ResolveButtonSet(buttons, customButtons).Select(item => item.result).ToHashSet();
        if (allowed.Contains(configuredDefault) && configuredDefault != MessageBoxResult.None)
        {
            return configuredDefault;
        }

        return ResolveButtonSet(buttons, customButtons).First().result;
    }
}
