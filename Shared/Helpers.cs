using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Globalization;
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
}