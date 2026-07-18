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

    public static readonly DependencyProperty HighlightTrailingParentheticalProperty = DependencyProperty.RegisterAttached(
        "HighlightTrailingParenthetical",
        typeof(bool),
        typeof(OverviewStatusText),
        new PropertyMetadata(false, OnTextChanged));

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetHighlightTrailingParenthetical(DependencyObject element, bool value)
        => element.SetValue(HighlightTrailingParentheticalProperty, value);

    public static bool GetHighlightTrailingParenthetical(DependencyObject element)
        => (bool)element.GetValue(HighlightTrailingParentheticalProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        textBlock.Inlines.Clear();
        var text = textBlock.GetValue(TextProperty) as string;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (GetHighlightTrailingParenthetical(textBlock)
            && TrySplitTrailingParenthetical(text, out var normalText, out var highlightedText))
        {
            textBlock.Inlines.Add(new Run(normalText));
            textBlock.Inlines.Add(new Run(highlightedText)
            {
                Foreground = ThemeColors.Brush("ConstructFasterTextBrush"),
            });
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

        // "Ready (after previous task)" is ready in itself but queued behind another task, so it will not run
        // right now. Blue marks it as "queued, runs in turn" — distinct from the task that can start now.
        if (StartsWith(trimmed, "Ready (after"))
        {
            return ThemeColors.Brush("InfoTextBrush");
        }

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

    private static bool TrySplitTrailingParenthetical(
        string value,
        out string normalText,
        out string highlightedText)
    {
        var separatorIndex = value.LastIndexOf(" (", StringComparison.Ordinal);
        if (separatorIndex <= 0 || !value.EndsWith(')'))
        {
            normalText = value;
            highlightedText = string.Empty;
            return false;
        }

        normalText = value[..separatorIndex];
        highlightedText = value[separatorIndex..];
        return true;
    }
}
