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
        CultureInfo culture = GetCurrentCulture();

        string number(double value) =>
            value.ToString("0.##", culture);

        string unit;
        double value;

        if (bytes < 1024)
        {
            value = bytes;
            unit = LocalizerExtensions.GetLocalizedString("FileSizeBytes");
        }
        else if (bytes < 1024 * 1024)
        {
            value = bytes / 1024d;
            unit = LocalizerExtensions.GetLocalizedString("FileSizeKB");
        }
        else if (bytes < 1024L * 1024 * 1024)
        {
            value = bytes / (1024d * 1024);
            unit = LocalizerExtensions.GetLocalizedString("FileSizeMB");
        }
        else
        {
            value = bytes / (1024d * 1024 * 1024);
            unit = LocalizerExtensions.GetLocalizedString("FileSizeGB");
        }

        return $"{number(value)} {unit}";
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
        string language = Localizer.Get().GetCurrentLanguage();

        string cultureName = language switch
        {
            "ar" => "ar",
            "he" => "he-IL",
            "th" => "th-TH",
            "en-UWU" or "en-LOL" => "en-US",
            _ => language
        };

        try
        {
            return CultureInfo.GetCultureInfo(cultureName);
        }
        catch (CultureNotFoundException)
        {
            string baseLanguage = language.Split('-')[0];

            try
            {
                return CultureInfo.GetCultureInfo(baseLanguage);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.GetCultureInfo("en-US");
            }
        }
    }

    public static string FormatDaysAgo(int days)
    {
        if (days == 1)
            return LocalizerExtensions.GetLocalizedString("DayAgo");

        CultureInfo culture = GetCurrentCulture();
        string number = days.ToString("N0", culture);

        if (culture.TwoLetterISOLanguageName is "ru" or "uk"
            && days % 100 is not (>= 11 and <= 14))
        {
            switch (days % 10)
            {
                case 1:
                    return $"{number} {LocalizerExtensions.GetLocalizedString("DayAgo")}";
                case >= 2 and <= 4:
                    return string.Format(LocalizerExtensions.GetLocalizedString("DaysAgoFew"), number);
            }
        }

        return string.Format(LocalizerExtensions.GetLocalizedString("DaysAgo"), number);
    }

    public static string? FormatDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        bool includesTime = System.Text.RegularExpressions.Regex.IsMatch(
            value,
            @"(?:T|\s)\d{1,2}:\d{2}");

        string[] formats =
        [
            "yyyy:MM:dd HH:mm:ss",
            "yyyy:MM:dd HH:mm",
            "yyyy-MM-dd",
            "yyyy"
        ];

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTimeOffset date) &&
            !DateTimeOffset.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out date))
        {
            return null;
        }

        CultureInfo culture = GetCurrentCulture();

        string formattedDate = date.ToString(
            includesTime ? "f" : "D",
            culture);

        string firstTextElement = StringInfo.GetNextTextElement(formattedDate);

        return culture.TextInfo.ToUpper(firstTextElement) +
               formattedDate[firstTextElement.Length..];
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
