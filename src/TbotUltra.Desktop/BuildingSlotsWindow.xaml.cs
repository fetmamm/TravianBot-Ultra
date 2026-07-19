using System.Windows;

namespace TbotUltra.Desktop;

/// <summary>
/// Read-only reference window showing which building slot number sits where in a village.
/// Pure display: no bot state, no services, nothing to save.
/// </summary>
public partial class BuildingSlotsWindow : Window
{
    public BuildingSlotsWindow()
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
