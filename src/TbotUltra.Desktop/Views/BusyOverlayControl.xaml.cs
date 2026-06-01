using System;
using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

/// <summary>
/// Shared modal "busy" overlay: a dark scrim with a centered white card containing a title,
/// an indeterminate progress bar, a status line and a Cancel button. It captures all clicks so the
/// only action available during an in-flight operation is Cancel.
///
/// Drive it either through the bindable <see cref="Title"/>/<see cref="Text"/>/<see cref="IsBusy"/>
/// properties, or imperatively via <see cref="Show"/>/<see cref="Hide"/>. Subscribe to
/// <see cref="Cancelled"/> to react to the Cancel button (e.g. cancel a CancellationTokenSource);
/// the control automatically switches the status line to "Cancelling…" and disables the button.
/// </summary>
public partial class BusyOverlayControl : UserControl
{
    public BusyOverlayControl()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user clicks Cancel. The host should cancel its operation.</summary>
    public event EventHandler? Cancelled;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(BusyOverlayControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(BusyOverlayControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(BusyOverlayControl), new PropertyMetadata(false, OnIsBusyChanged));

    public static readonly DependencyProperty ShowCancelProperty = DependencyProperty.Register(
        nameof(ShowCancel), typeof(bool), typeof(BusyOverlayControl), new PropertyMetadata(true));

    public static readonly DependencyProperty CancelEnabledProperty = DependencyProperty.Register(
        nameof(CancelEnabled), typeof(bool), typeof(BusyOverlayControl), new PropertyMetadata(true));

    /// <summary>Heading shown at the top of the card (e.g. "Logging in").</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Status line shown under the progress bar (e.g. "Logging in and loading account data…").</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>When true the overlay is visible and blocks interaction; when false it is collapsed.</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>Whether the Cancel button is shown. Defaults to true.</summary>
    public bool ShowCancel
    {
        get => (bool)GetValue(ShowCancelProperty);
        set => SetValue(ShowCancelProperty, value);
    }

    /// <summary>Whether the Cancel button is clickable. Reset to true by <see cref="Show"/>.</summary>
    public bool CancelEnabled
    {
        get => (bool)GetValue(CancelEnabledProperty);
        set => SetValue(CancelEnabledProperty, value);
    }

    /// <summary>Shows the overlay with the given title and status text, re-enabling Cancel.</summary>
    public void Show(string title, string text)
    {
        Title = title;
        Text = text;
        CancelEnabled = true;
        IsBusy = true;
    }

    /// <summary>Hides the overlay.</summary>
    public void Hide()
    {
        IsBusy = false;
    }

    private static void OnIsBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((BusyOverlayControl)d).Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Reflect the cancelling state immediately so the user sees feedback, then notify the host.
        CancelEnabled = false;
        Text = "Cancelling…";
        Cancelled?.Invoke(this, EventArgs.Empty);
    }
}
