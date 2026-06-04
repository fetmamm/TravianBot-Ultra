using System.Windows;
using System.Windows.Media;

namespace TbotUltra.Desktop;

/// <summary>
/// Resolves theme brushes/colors from the central <c>Themes/Palette.xaml</c> resource dictionary
/// by key, so code-behind reads the same design tokens as XAML (which uses DynamicResource).
/// This keeps a single source of truth for color: flipping Palette.xaml flips both XAML and C#.
///
/// Falls back to a visible magenta if a key is missing, so token typos are obvious during testing
/// instead of silently rendering transparent/black.
/// </summary>
internal static class ThemeColors
{
    /// <summary>Returns the palette brush for <paramref name="key"/> (shared instance; do not mutate).</summary>
    public static SolidColorBrush Brush(string key)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Magenta);
    }

    /// <summary>Returns the palette color for <paramref name="key"/>, for callers that build their own brush.</summary>
    public static Color Get(string key) => Brush(key).Color;
}
