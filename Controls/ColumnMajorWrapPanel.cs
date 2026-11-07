using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;

namespace FluentBrowser.Controls;

public sealed class ColumnMajorWrapPanel : Panel
{
    public static readonly DependencyProperty PreferredColumnWidthProperty =
        DependencyProperty.Register(
            nameof(PreferredColumnWidth),
            typeof(double),
            typeof(ColumnMajorWrapPanel),
            new PropertyMetadata(240d, OnPreferredColumnWidthChanged));

    private int _visibleChildCount;
    private int _columnCount = 1;

    public double PreferredColumnWidth
    {
        get => (double)GetValue(PreferredColumnWidthProperty);
        set => SetValue(PreferredColumnWidthProperty, value);
    }

    private static void OnPreferredColumnWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColumnMajorWrapPanel panel)
            panel.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int childCount = Children.Count;
        _visibleChildCount = 0;

        for (int i = 0; i < childCount; i++)
        {
            if (Children[i].Visibility != Visibility.Collapsed)
                _visibleChildCount++;
        }

        if (_visibleChildCount == 0)
        {
            _columnCount = 1;
            return new Size(0, 0);
        }

        bool infiniteWidth = double.IsInfinity(availableSize.Width);
        double availableWidth = infiniteWidth ? double.PositiveInfinity : availableSize.Width;

        double preferredWidth = PreferredColumnWidth;
        if (!double.IsFinite(preferredWidth) || preferredWidth < 1)
            preferredWidth = 240;

        _columnCount = infiniteWidth
            ? 1
            : (int)Math.Clamp(
                availableWidth / preferredWidth,
                1d,
                (double)_visibleChildCount);

        double columnWidth = availableWidth / _columnCount;

        int itemsPerColumn = _visibleChildCount / _columnCount;
        int extraItems = _visibleChildCount % _columnCount;

        double maxColumnHeight = 0;
        double maxChildWidth = 0;
        double columnHeight = 0;

        int column = 0;
        int row = 0;
        int itemsInColumn = itemsPerColumn + (column < extraItems ? 1 : 0);

        for (int i = 0; i < childCount; i++)
        {
            UIElement child = Children[i];

            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(new Size(0, 0));
                continue;
            }

            child.Measure(new Size(columnWidth, availableSize.Height));

            columnHeight += child.DesiredSize.Height;

            if (infiniteWidth)
                maxChildWidth = Math.Max(maxChildWidth, child.DesiredSize.Width);

            row++;

            if (row == itemsInColumn)
            {
                maxColumnHeight = Math.Max(maxColumnHeight, columnHeight);
                column++;
                row = 0;
                columnHeight = 0;
                itemsInColumn = itemsPerColumn + (column < extraItems ? 1 : 0);
            }
        }

        // Handle the last (possibly incomplete) column
        maxColumnHeight = Math.Max(maxColumnHeight, columnHeight);

        return new Size(
            infiniteWidth ? maxChildWidth : availableWidth,
            maxColumnHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int childCount = Children.Count;

        if (_visibleChildCount == 0)
        {
            for (int i = 0; i < childCount; i++)
                Children[i].Arrange(new Rect(0, 0, 0, 0));
            return finalSize;
        }

        double columnWidth = finalSize.Width / _columnCount;

        int itemsPerColumn = _visibleChildCount / _columnCount;
        int extraItems = _visibleChildCount % _columnCount;

        int column = 0;
        int row = 0;
        int itemsInColumn = itemsPerColumn + (column < extraItems ? 1 : 0);

        double columnX = 0;
        double itemY = 0;

        for (int i = 0; i < childCount; i++)
        {
            UIElement child = Children[i];

            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            double itemHeight = child.DesiredSize.Height;

            child.Arrange(new Rect(columnX, itemY, columnWidth, itemHeight));

            itemY += itemHeight;
            row++;

            if (row == itemsInColumn)
            {
                columnX += columnWidth;
                column++;
                row = 0;
                itemY = 0;
                itemsInColumn = itemsPerColumn + (column < extraItems ? 1 : 0);
            }
        }

        return finalSize;
    }
}