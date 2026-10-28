using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using WinUI3Localizer;

namespace FluentBrowser.Shared;

public static class Helpers
{
    public static string FormatFileSize(long bytes)
    {
        bool isArabic = Localizer.Get().GetCurrentLanguage()?.StartsWith("ar") == true;
        CultureInfo culture = GetCurrentCulture();

        string number(double value) =>
            value.ToString("0.##", culture);

        string[] units = isArabic
            ? ["بايت", "ك.ب", "م.ب", "غ.ب"]
            : ["B", "KB", "MB", "GB"];

        if (bytes < 1024)
            return $"{number(bytes)} {units[0]}";

        if (bytes < 1024 * 1024)
            return $"{number(bytes / 1024d)} {units[1]}";

        if (bytes < 1024L * 1024 * 1024)
            return $"{number(bytes / (1024d * 1024))} {units[2]}";

        return $"{number(bytes / (1024d * 1024 * 1024))} {units[3]}";
    }

    public static string FormatFileSize(ulong bytes) =>
        FormatFileSize((long)bytes);

    public static bool UsesWordSpacing(string language)
    {
        return language switch
        {
            "ja-JP" => false,
            "zh-CN" => false,
            "zh-TW" => false,
            "ko-KR" => false,
            _ => true
        };
    }

    public static CultureInfo GetCurrentCulture()
    {
        return Localizer.Get().GetCurrentLanguage() switch
        {
            "ar" => CultureInfo.GetCultureInfo("ar"),
            "en-US" => CultureInfo.GetCultureInfo("en-US"),
            "he" => CultureInfo.GetCultureInfo("he-IL"),
            "ja-JP" => CultureInfo.GetCultureInfo("ja-JP"),
            "ko-KR" => CultureInfo.GetCultureInfo("ko-KR"),
            "ro-RO" => CultureInfo.GetCultureInfo("ro-RO"),
            "th" => CultureInfo.GetCultureInfo("th-TH"),
            "zh-CN" => CultureInfo.GetCultureInfo("zh-CN"),
            "zh-TW" => CultureInfo.GetCultureInfo("zh-TW"),
            _ => CultureInfo.GetCultureInfo("en-US")
        };
    }

    public static string FormatDaysAgo(int days)
    {
        if (days == 1)
            return LocalizerExtensions.GetLocalizedString("DayAgo");

        return string.Format(
            LocalizerExtensions.GetLocalizedString("DaysAgo"),
            days.ToString("N0", GetCurrentCulture()));
    }

    public static MenuFlyoutItem CreateContextMenuItem(
        string text,
        string? icon,
        RoutedEventHandler click,
        string keyboardShortcut = "")
    {
        return CreateContextMenuItem<MenuFlyoutItem>(
            text, icon, click, keyboardShortcut);
    }

    public static T CreateContextMenuItem<T>(
        string text,
        string? icon = null,
        RoutedEventHandler? click = null,
        string keyboardShortcut = "")
        where T : MenuFlyoutItemBase, new()
    {
        var item = new T();
        var iconElement = CreateContextMenuIcon(icon);

        switch (item)
        {
            case MenuFlyoutItem menuItem:
                menuItem.Text = text;
                menuItem.Icon = iconElement;
                menuItem.KeyboardAcceleratorTextOverride = keyboardShortcut;

                if (click is not null)
                    menuItem.Click += click;

                break;

            case MenuFlyoutSubItem subItem:
                if (click is not null || !string.IsNullOrEmpty(keyboardShortcut))
                {
                    throw new ArgumentException(
                        "Submenu items cannot have click handlers or keyboard shortcut overrides.");
                }

                subItem.Text = text;
                subItem.Icon = iconElement;
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported menu item type: {typeof(T).Name}");
        }

        return item;
    }

    private static IconElement? CreateContextMenuIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
            return null;

        if (icon.Length == 1)
        {
            return new FontIcon
            {
                Glyph = icon
            };
        }

        return new PathIcon
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(
                typeof(Geometry),
                icon)
        };
    }

    public static MenuFlyoutItem CreateMenuItem(
        string text,
        string glyph,
        RoutedEventHandler click,
        bool isEnabled = true)
    {
        var item = CreateContextMenuItem(text, glyph, click);
        item.IsEnabled = isEnabled;
        return item;
    }

    public static void RemoveListViewTransitions(ListView listView, params Type[] transitionTypes)
    {
        var transitions = listView.ItemContainerTransitions;

        for (int i = transitions.Count - 1; i >= 0; i--)
        {
            if (transitionTypes.Any(type => type.IsInstanceOfType(transitions[i])))
                transitions.RemoveAt(i);
        }
    }

    // stolen color stuff idk anything about
    public static async Task<Color> GetAverageColorAsync(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        if (imageBytes.Length == 0)
            throw new InvalidDataException("The image data is empty.");

        using var stream = new InMemoryRandomAccessStream();

        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);

        const uint maximumDimension = 64;

        double scale = Math.Min(
            1.0,
            maximumDimension / (double)Math.Max(
                decoder.OrientedPixelWidth,
                decoder.OrientedPixelHeight));

        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(
                1u,
                (uint)Math.Round(decoder.OrientedPixelWidth * scale)),
            ScaledHeight = Math.Max(
                1u,
                (uint)Math.Round(decoder.OrientedPixelHeight * scale))
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        byte[] pixels = pixelData.DetachPixelData();
        int pixelCount = pixels.Length / 4;

        if (pixelCount == 0)
            throw new InvalidDataException("The image contains no pixels.");

        int totalRed = 0;
        int totalGreen = 0;
        int totalBlue = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            totalRed += pixels[i];
            totalGreen += pixels[i + 1];
            totalBlue += pixels[i + 2];
        }

        return Color.FromArgb(
            255,
            (byte)Math.Round(totalRed / (double)pixelCount),
            (byte)Math.Round(totalGreen / (double)pixelCount),
            (byte)Math.Round(totalBlue / (double)pixelCount));
    }

    public static Color DarkenColor(Color color, double overlayOpacity)
    {
        double remaining = 1.0 - Math.Clamp(overlayOpacity, 0.0, 1.0);

        return Color.FromArgb(
            255,
            (byte)Math.Round(color.R * remaining),
            (byte)Math.Round(color.G * remaining),
            (byte)Math.Round(color.B * remaining));
    }

    public static (double Black, double White)
        GetBlackAndWhiteContrastRatios(Color color)
    {
        double denominator = GetRelativeLuminance(color) + 0.05;

        return (
            Black: denominator / 0.05,
            White: 1.05 / denominator);
    }

    private static double GetRelativeLuminance(Color color)
    {
        static double ToLinearSrgb(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * ToLinearSrgb(color.R)
             + 0.7152 * ToLinearSrgb(color.G)
             + 0.0722 * ToLinearSrgb(color.B);
    }
}