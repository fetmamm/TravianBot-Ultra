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
    private MessageBoxResult _result;
    private bool _resultChosen;

    private AppDialog(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        InitializeComponent();

        Owner = owner;
        Title = string.IsNullOrWhiteSpace(title) ? "Dialog" : title;
        MessageTextBlock.Text = message ?? string.Empty;
        _buttons = buttons;
        _defaultResult = defaultResult;
        _result = ResolveDefaultResult(buttons, defaultResult);
        ApplyIcon(icon);
        BuildButtons();
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

    private void BuildButtons()
    {
        ButtonsPanel.Children.Clear();
        foreach (var (label, result) in ResolveButtonSet(_buttons))
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 88,
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(12, 0, 12, 0),
                IsDefault = result == ResolveDefaultResult(_buttons, _defaultResult),
                IsCancel = result == ResolveCancelResult(_buttons),
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
            MessageBoxImage.Warning => ("!", Color.FromRgb(146, 64, 14), Color.FromRgb(254, 243, 199)),
            MessageBoxImage.Error => ("x", Color.FromRgb(153, 27, 27), Color.FromRgb(254, 226, 226)),
            MessageBoxImage.Question => ("?", Color.FromRgb(30, 64, 175), Color.FromRgb(219, 234, 254)),
            MessageBoxImage.Information => ("i", Color.FromRgb(30, 64, 175), Color.FromRgb(219, 234, 254)),
            _ => ("i", Color.FromRgb(31, 41, 55), Color.FromRgb(229, 231, 235)),
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

        _result = ResolveCancelResult(_buttons);
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

        _result = ResolveCancelResult(_buttons);
    }

    private static IReadOnlyList<(string label, MessageBoxResult result)> ResolveButtonSet(MessageBoxButton buttons)
    {
        return buttons switch
        {
            MessageBoxButton.OKCancel => [("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            _ => [("OK", MessageBoxResult.OK)],
        };
    }

    private static MessageBoxResult ResolveCancelResult(MessageBoxButton buttons)
    {
        return buttons switch
        {
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
    }

    private static MessageBoxResult ResolveDefaultResult(MessageBoxButton buttons, MessageBoxResult configuredDefault)
    {
        var allowed = ResolveButtonSet(buttons).Select(item => item.result).ToHashSet();
        if (allowed.Contains(configuredDefault) && configuredDefault != MessageBoxResult.None)
        {
            return configuredDefault;
        }

        return ResolveButtonSet(buttons).First().result;
    }
}
