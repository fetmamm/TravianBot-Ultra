using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TbotUltra.Desktop;

// Attached property that renders a multi-line status string into a TextBlock with per-line color so the
// Village settings Overview grid reads at a glance: green for "Ready"/"Now", amber for "Waiting"/deferred
// timers, red for "Blocked"/"Dead", muted for "Disabled"/"Not …", and the default cell color for active
// countdowns (e.g. "Bakery · Level 5 · 30:16"). Set OverviewStatusText.Text instead of TextBlock.Text.
public static class OverviewStatusText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(OverviewStatusText),
        new PropertyMetadata(null, OnTextChanged));

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        textBlock.Inlines.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Each cell can hold several status lines (one per build slot / farm list). Color each line on its
        // own so a mixed "active build + free Ready slot" cell shows both states.
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                textBlock.Inlines.Add(new LineBreak());
            }

            var run = new Run(lines[i]);
            var brush = ResolveLineBrush(lines[i]);
            if (brush is not null)
            {
                run.Foreground = brush;
            }

            textBlock.Inlines.Add(run);
        }
    }

    // Returns a color for the line based on its leading status word, or null to inherit the cell's default
    // foreground (used for active countdown rows that are neither ready nor waiting).
    private static Brush? ResolveLineBrush(string line)
    {
        var trimmed = line.TrimStart();
        if (StartsWith(trimmed, "Ready") || IsExactly(trimmed, "Now"))
        {
            return ThemeColors.Brush("SuccessTextBrush");
        }

        if (StartsWith(trimmed, "Waiting") || StartsWith(trimmed, "Earliest"))
        {
            return ThemeColors.Brush("WarningTextBrush");
        }

        if (StartsWith(trimmed, "Blocked") || IsExactly(trimmed, "Dead"))
        {
            return ThemeColors.Brush("DangerTextBrush");
        }

        if (IsExactly(trimmed, "Disabled")
            || StartsWith(trimmed, "Not applicable")
            || StartsWith(trimmed, "Not available")
            || StartsWith(trimmed, "Not loaded"))
        {
            return ThemeColors.Brush("TextMutedBrush");
        }

        return null;
    }

    private static bool StartsWith(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsExactly(string value, string other)
        => string.Equals(value, other, StringComparison.OrdinalIgnoreCase);
}
