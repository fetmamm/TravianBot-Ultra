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

        // Use the natural (uniform) cell height instead of dividing the available
        // height. When the panel is given more height than its content needs (e.g.
        // hosted in a stretching ScrollViewer row), this keeps cards top-packed at
        // their measured size rather than stretching them apart.
        var cellHeight = 0d;
        foreach (UIElement child in InternalChildren)
        {
            cellHeight = Math.Max(cellHeight, child.DesiredSize.Height);
        }

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
