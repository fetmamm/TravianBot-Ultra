using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class UpdateConfirmationView : UserControl
{
    public UpdateConfirmationView(string version)
    {
        InitializeComponent();
        VersionTextBlock.Text = $"Ready to install v{version}";
    }
}
