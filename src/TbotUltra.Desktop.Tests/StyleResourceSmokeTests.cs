using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TbotUltra.Desktop.Tests;

/// <summary>
/// Guards the keyed styles and embedded assets the XAML refers to by name. A renamed or dropped key
/// still compiles and only fails when the control is first rendered, so these assert the keys exist,
/// resolve to the expected type, and — for the icon button — actually produce a visible glyph.
/// </summary>
[Collection(WpfSmokeCollection.Name)]
public sealed class StyleResourceSmokeTests
{
    private readonly WpfSmokeFixture _wpf;

    public StyleResourceSmokeTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    [Theory]
    [InlineData("RefreshIconButtonStyle")]
    [InlineData("SidebarNavButtonStyle")]
    [InlineData("ResourceLevelBadgeStyle")]
    [InlineData("SettingInfoIconStyle")]
    [InlineData("InfoTooltipIconStyle")]
    [InlineData("AutomationToggleStyle")]
    public void KeyedStyle_IsResolvableFromApplicationResources(string key)
    {
        _wpf.Run(() =>
        {
            var resource = Application.Current.TryFindResource(key);
            Assert.NotNull(resource);
            Assert.IsType<Style>(resource);
        });
    }

    [Fact]
    public void RefreshIconButtonStyle_RendersTheArrowGlyph()
    {
        _wpf.Run(() =>
        {
            var button = new Button { Style = (Style)Application.Current.FindResource("RefreshIconButtonStyle") };
            button.Measure(new Size(200, 200));
            button.Arrange(new Rect(0, 0, 200, 200));
            button.UpdateLayout();

            // The style supplies the glyph through ContentTemplate while Content stays null. If that
            // ever stops rendering, the buttons silently turn into empty squares.
            var path = FindVisualChild<System.Windows.Shapes.Path>(button);
            Assert.NotNull(path);
            Assert.NotNull(path!.Data);
            Assert.NotNull(path.Fill);
            Assert.True(path.ActualWidth > 0, "Refresh glyph rendered with zero width.");
        });
    }

    [Fact]
    public void BuildingSlotsImage_IsEmbeddedAndDecodable()
    {
        _wpf.Run(() =>
        {
            // The window references this by pack URI; a missing csproj <Resource> entry only fails at
            // runtime, when the user clicks "Show building slots".
            var uri = new Uri("pack://application:,,,/TbotUltra.Desktop;component/Assets/travian_dorf2_slots.png");
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
