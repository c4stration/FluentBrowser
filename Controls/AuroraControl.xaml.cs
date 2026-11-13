using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FluentBrowser.Controls;

// This is a WinUI 3 port of the AuroraControl from the WinUI 2 sample gallery! Just change the namespace and class name to avoid conflicts with the original control.

public sealed partial class AuroraControl : UserControl
{
    private static readonly Color PaletteColor =
        Color.FromArgb(255, 0x85, 0x99, 0xB4);

    private Storyboard? _colorStoryboard;
    private Storyboard? _ambientStoryboard;

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(
            nameof(Color),
            typeof(Color),
            typeof(AuroraControl),
            new PropertyMetadata(PaletteColor, OnColorChanged));

    public TimeSpan AnimationDuration
    {
        get => (TimeSpan)GetValue(AnimationDurationProperty);
        set => SetValue(AnimationDurationProperty, value);
    }

    public static readonly DependencyProperty AnimationDurationProperty =
        DependencyProperty.Register(
            nameof(AnimationDuration),
            typeof(TimeSpan),
            typeof(AuroraControl),
            new PropertyMetadata(TimeSpan.FromSeconds(0.5)));

    public AuroraControl()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;

        // Apply the default palette to the current application theme.
        AnimateColor(Color);
    }

    private static void OnColorChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var control = (AuroraControl)dependencyObject;
        control.AnimateColor((Color)e.NewValue);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        AnimateColor(Color);
    }

    private void AnimateColor(Color newColor)
    {
        var hsl = ToHsl(newColor);

        if (ActualTheme == ElementTheme.Dark)
        {
            var backgroundColor = FromHsl(
                hsl.H,
                hsl.S,
                hsl.L * 0.7);

            AnimateAurora(newColor, backgroundColor);
        }
        else
        {
            var foregroundColor = FromHsl(
                hsl.H,
                hsl.S,
                Math.Clamp(hsl.L * 1.25, 0, 1));

            AnimateAurora(foregroundColor, newColor);
        }
    }

    private void AnimateAurora(
        Color auroraColor,
        Color backgroundColor)
    {
        _colorStoryboard?.Stop();

        if (BackgroundLayer.Background is not SolidColorBrush backgroundBrush)
        {
            backgroundBrush = new SolidColorBrush();
            BackgroundLayer.Background = backgroundBrush;
        }

        var storyboard = new Storyboard();

        var backgroundAnimation = new ColorAnimation
        {
            From = backgroundBrush.Color,
            To = backgroundColor,
            Duration = new Duration(AnimationDuration),
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(backgroundAnimation, backgroundBrush);
        Storyboard.SetTargetProperty(backgroundAnimation, nameof(SolidColorBrush.Color));
        storyboard.Children.Add(backgroundAnimation);

        // The artwork's gradient stops are authored with this base palette.
        // Comparing against it allows the accent color to be changed repeatedly.
        AdjustAurora(storyboard, PaletteColor, auroraColor, BackgroundLayer);

        _colorStoryboard = storyboard;
        storyboard.Begin();
    }

    private void AdjustAurora(
        Storyboard storyboard,
        Color baseColor,
        Color newColor,
        UIElement element)
    {
        if (element is Shape shape)
        {
            if (shape.Fill is GradientBrush fill)
                AdjustLinearGradient(storyboard, fill, baseColor, newColor);

            if (shape.Stroke is GradientBrush stroke)
                AdjustLinearGradient(storyboard, stroke, baseColor, newColor);
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            if (VisualTreeHelper.GetChild(element, i) is UIElement child)
                AdjustAurora(storyboard, baseColor, newColor, child);
        }
    }

    private void AdjustLinearGradient(
        Storyboard storyboard,
        GradientBrush gradient,
        Color baseColor,
        Color newColor)
    {
        foreach (var gradientStop in gradient.GradientStops)
        {
            var stopColor = gradientStop.Color;

            // Ignore the stop's alpha when matching the artwork's base color.
            var opaqueStopColor = Color.FromArgb(
                255,
                stopColor.R,
                stopColor.G,
                stopColor.B);

            var targetColor = AreClose(opaqueStopColor, baseColor)
                ? Color.FromArgb(
                    stopColor.A,
                    newColor.R,
                    newColor.G,
                    newColor.B)
                : stopColor;

            if (AreClose(targetColor, stopColor))
                continue;

            var animation = new ColorAnimation
            {
                From = stopColor,
                To = targetColor,
                Duration = new Duration(AnimationDuration),
                EnableDependentAnimation = true
            };

            Storyboard.SetTarget(animation, gradientStop);
            Storyboard.SetTargetProperty(animation, nameof(GradientStop.Color));
            storyboard.Children.Add(animation);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimateColor(Color);
        StartAmbientAnimations();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _ambientStoryboard?.Stop();
        _ambientStoryboard = null;
    }

    private void StartAmbientAnimations()
    {
        if (_ambientStoryboard is not null)
            return;

        var storyboard = new Storyboard();

        // Subtle movement across the two layers of lights.
        AddDoubleAnimation(
            storyboard, BL1, nameof(TranslateTransform.X),
            0, -500, TimeSpan.FromMinutes(1), autoReverse: true);

        AddDoubleAnimation(
            storyboard, BL2, nameof(TranslateTransform.X),
            48, 400, TimeSpan.FromSeconds(20), autoReverse: true);

        // Right-side curtain movement and opacity fades.
        AddDoubleAnimation(
            storyboard, BL4, nameof(TranslateTransform.X),
            970, 980, TimeSpan.FromSeconds(8), autoReverse: false);

        AddDoubleAnimation(
            storyboard, BL3, nameof(UIElement.Opacity),
            0, 1, TimeSpan.FromSeconds(4), autoReverse: true);

        AddDoubleAnimation(
            storyboard, BL6, nameof(TranslateTransform.X),
            935, 950, TimeSpan.FromSeconds(6), autoReverse: false);

        AddDoubleAnimation(
            storyboard, BL5, nameof(UIElement.Opacity),
            0, 1, TimeSpan.FromSeconds(3), autoReverse: true);

        AddDoubleAnimation(
            storyboard, BL8, nameof(TranslateTransform.X),
            870, 880, TimeSpan.FromSeconds(6), autoReverse: false);

        AddDoubleAnimation(
            storyboard, BL7, nameof(UIElement.Opacity),
            0, 1, TimeSpan.FromSeconds(3), autoReverse: true);

        AddDoubleAnimation(
            storyboard, BL10, nameof(TranslateTransform.X),
            880, 900, TimeSpan.FromSeconds(6), autoReverse: false);

        AddDoubleAnimation(
            storyboard, BL9, nameof(UIElement.Opacity),
            0, 1, TimeSpan.FromSeconds(3), autoReverse: true);

        AddDoubleAnimation(
            storyboard, BL12, nameof(TranslateTransform.X),
            827, 857, TimeSpan.FromSeconds(8), autoReverse: false);

        AddDoubleAnimation(
            storyboard, BL11, nameof(UIElement.Opacity),
            0, 1, TimeSpan.FromSeconds(4), autoReverse: true);

        _ambientStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void AddDoubleAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string propertyPath,
        double from,
        double to,
        TimeSpan duration,
        bool autoReverse)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            AutoReverse = autoReverse,
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, propertyPath);
        storyboard.Children.Add(animation);
    }

    private static (double H, double S, double L) ToHsl(Color color)
    {
        var red = color.R / 255.0;
        var green = color.G / 255.0;
        var blue = color.B / 255.0;

        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;

        var lightness = (max + min) / 2.0;

        if (delta == 0)
            return (0, 0, lightness);

        var saturation = delta / (1.0 - Math.Abs(2.0 * lightness - 1.0));

        double hue;
        if (max == red)
            hue = 60.0 * (((green - blue) / delta) % 6.0);
        else if (max == green)
            hue = 60.0 * (((blue - red) / delta) + 2.0);
        else
            hue = 60.0 * (((red - green) / delta) + 4.0);

        if (hue < 0)
            hue += 360.0;

        return (hue, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360.0) + 360.0) % 360.0;
        saturation = Math.Clamp(saturation, 0.0, 1.0);
        lightness = Math.Clamp(lightness, 0.0, 1.0);

        var chroma = (1.0 - Math.Abs(2.0 * lightness - 1.0)) * saturation;
        var hueSector = hue / 60.0;
        var secondary = chroma * (1.0 - Math.Abs((hueSector % 2.0) - 1.0));

        var (red, green, blue) = hueSector switch
        {
            < 1 => (chroma, secondary, 0.0),
            < 2 => (secondary, chroma, 0.0),
            < 3 => (0.0, chroma, secondary),
            < 4 => (0.0, secondary, chroma),
            < 5 => (secondary, 0.0, chroma),
            _ => (chroma, 0.0, secondary)
        };

        var offset = lightness - (chroma / 2.0);

        return Color.FromArgb(
            255,
            ToByte(red + offset),
            ToByte(green + offset),
            ToByte(blue + offset));
    }

    private static byte ToByte(double value)
    {
        return (byte)Math.Clamp(
            (int)Math.Round(value * 255.0),
            0,
            255);
    }

    private static bool AreClose(Color color1, Color color2)
    {
        return AreClose(color1.R, color2.R)
            && AreClose(color1.G, color2.G)
            && AreClose(color1.B, color2.B)
            && AreClose(color1.A, color2.A);
    }

    private const float FloatEpsilon = 1.1920929E-07f;

    private static bool AreClose(float a, float b)
    {
        if (a == b)
            return true;

        var epsilon = (Math.Abs(a) + Math.Abs(b) + 10f) * FloatEpsilon;
        var difference = a - b;

        if (-epsilon < difference)
            return epsilon > difference;

        return false;
    }
}