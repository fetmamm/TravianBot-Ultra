using System;
using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop;

public sealed class VerticalFirstUniformGrid : Panel
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(VerticalFirstUniformGrid),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var childCount = InternalChildren.Count;
        if (childCount == 0)
        {
            return new Size(0, 0);
        }

        var columns = Math.Max(1, Columns);
        var rows = (int)Math.Ceiling(childCount / (double)columns);
        var measuredWidth = 0d;
        var measuredHeight = 0d;

        var cellWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : availableSize.Width / columns;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            measuredWidth = Math.Max(measuredWidth, child.DesiredSize.Width);
            measuredHeight = Math.Max(measuredHeight, child.DesiredSize.Height);
        }

        var finalWidth = double.IsInfinity(availableSize.Width)
            ? measuredWidth * columns
            : availableSize.Width;

        return new Size(finalWidth, measuredHeight * rows);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var childCount = InternalChildren.Count;
        if (childCount == 0)
        {
            return finalSize;
        }

        var columns = Math.Max(1, Columns);
        var rows = (int)Math.Ceiling(childCount / (double)columns);
        var cellWidth = finalSize.Width / columns;
        var cellHeight = rows > 0 ? finalSize.Height / rows : 0;

        for (var index = 0; index < childCount; index++)
        {
            var column = index / rows;
            var row = index % rows;
            var x = column * cellWidth;
            var y = row * cellHeight;
            InternalChildren[index].Arrange(new Rect(x, y, cellWidth, cellHeight));
        }

        return finalSize;
    }
}
