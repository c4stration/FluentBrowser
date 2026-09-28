using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private static MenuFlyoutItem CreateContextMenuItem(
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

    private static MenuFlyoutItem CreateMenuItem(
        string text,
        string glyph,
        RoutedEventHandler click,
        bool isEnabled = true)
    {
        var item = CreateContextMenuItem(
            text,
            glyph,
            click);

        item.IsEnabled = isEnabled;
        return item;
    }
}
