using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using System;
using System.Linq;

namespace FluentBrowser.Controls;

// STILL IN WIP!!!!!!!!!!!!!!!!!!!!!!!!!!

public sealed class TickBar : Canvas
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(
            nameof(Values),
            typeof(double[]),
            typeof(TickBar),
            new PropertyMetadata(null, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(
            nameof(Minimum),
            typeof(double),
            typeof(TickBar),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum),
            typeof(double),
            typeof(TickBar),
            new PropertyMetadata(100.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty TickBrushProperty =
        DependencyProperty.Register(
            nameof(TickBrush),
            typeof(Brush),
            typeof(TickBar),
            new PropertyMetadata(null, OnLayoutPropertyChanged));

    public static readonly DependencyProperty TickWidthProperty =
        DependencyProperty.Register(
            nameof(TickWidth),
            typeof(double),
            typeof(TickBar),
            new PropertyMetadata(1.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty TickHeightProperty =
        DependencyProperty.Register(
            nameof(TickHeight),
            typeof(double),
            typeof(TickBar),
            new PropertyMetadata(10.0, OnLayoutPropertyChanged));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush? TickBrush
    {
        get => (Brush?)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public double TickWidth
    {
        get => (double)GetValue(TickWidthProperty);
        set => SetValue(TickWidthProperty, value);
    }

    public double TickHeight
    {
        get => (double)GetValue(TickHeightProperty);
        set => SetValue(TickHeightProperty, value);
    }

    public TickBar()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => RenderTicks();
    }

    private static void OnLayoutPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        ((TickBar)sender).RenderTicks();
    }

    private void RenderTicks()
    {
        Children.Clear();

        if (Values is not { Length: > 0 } values ||
            TickBrush is not { } brush ||
            !double.IsFinite(ActualWidth) ||
            !double.IsFinite(ActualHeight) ||
            !double.IsFinite(Minimum) ||
            !double.IsFinite(Maximum) ||
            Maximum <= Minimum ||
            TickWidth <= 0 ||
            TickHeight <= 0 ||
            ActualWidth <= 0 ||
            ActualHeight <= 0)
        {
            return;
        }

        double height = Math.Min(TickHeight, ActualHeight);
        double top = (ActualHeight - height) / 2;

        foreach (double value in values
            .Where(double.IsFinite)
            .Where(value => value >= Minimum && value <= Maximum)
            .Distinct()
            .OrderBy(value => value))
        {
            double position =
                (value - Minimum) / (Maximum - Minimum) * ActualWidth;

            Rectangle tick = new()
            {
                Width = TickWidth,
                Height = height,
                Fill = brush,
                IsHitTestVisible = false
            };

            SetLeft(tick, position - TickWidth / 2);
            SetTop(tick, top);
            Children.Add(tick);
        }
    }
}