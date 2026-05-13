using System.Windows;

namespace TbotUltra.Desktop;

public partial class FunctionTestWindow : Window
{
    public event RoutedEventHandler? ResourceProductionTestRequested;

    public FunctionTestWindow()
    {
        InitializeComponent();
    }

    private void TestResourceProductionButton_Click(object sender, RoutedEventArgs e)
    {
        ResourceProductionTestRequested?.Invoke(sender, e);
    }
}
