using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using WinUI3Localizer;

namespace FluentBrowser.Shared;

public static class Helpers
{
    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.##} KB";

        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024):0.##} MB";

        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }

    public static string FormatFileSize(ulong bytes) =>
        FormatFileSize((long)bytes);

    public static T? FindDescendant<T>(
        DependencyObject parent,
        Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            if (child is T result && (predicate == null || predicate(result)))
                return result;

            T? descendant = FindDescendant(child, predicate);

            if (descendant != null)
                return descendant;
        }

        return null;
    }

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
        ILocalizer localizer = Localizer.Get();

        if (days == 1)
            return localizer.GetLocalizedString("DayAgo");

        return string.Format(
            localizer.GetLocalizedString("DaysAgo"),
            days.ToString("N0", GetCurrentCulture()));
    }

    public static MenuFlyoutItem CreateContextMenuItem(
        string text,
        string glyph,
        RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem
        {
            Text = text
        };

        if (!string.IsNullOrEmpty(glyph))
        {
            item.Icon = new FontIcon
            {
                Glyph = glyph
            };
        }

        item.Click += click;
        return item;
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