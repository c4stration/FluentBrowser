using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private bool _themeHandlingInitialized;
    private bool _pageThemeColorApplied;

    private readonly SolidColorBrush _tabViewSelectedBackgroundBrush = new();

    private void InitializeThemeHandling()
    {
        if (_themeHandlingInitialized)
            return;

        _themeHandlingInitialized = true;

        Toolbar.ActualThemeChanged += (_, _) =>
        {
            if (_pageThemeColorApplied)
                return;

            ApplyDefaultThemeVisuals();

            if (SelectedWebView is { } webView)
                webView.DefaultBackgroundColor =
                    GetDefaultWebViewBackgroundColor();
        };
    }

    private async Task UpdateThemeColorFromPageAsync(
        WebView2 webView,
        CoreWebView2 core)
    {
        try
        {
            var settings =
                ApplicationData.Current.LocalSettings;

            bool enabled =
                settings.Values["ThemeColorTint"] as bool?
                ?? true;

            if (!enabled)
            {
                RestoreDefaultTheme(webView);
                return;
            }

            var json =
                await core.ExecuteScriptAsync("""
                (() => {
                    const meta = [
                        ...document.querySelectorAll(
                            'meta[name="theme-color"]'
                        )
                    ].find(
                        m => !m.media ||
                        matchMedia(m.media).matches
                    );

                    return meta?.content ?? null;
                })()
                """);

            if (string.IsNullOrEmpty(json) ||
                json == "null")
            {
                RestoreDefaultTheme(webView);
                return;
            }

            var value =
                json.Trim('"');

            if (!TryParseCssColor(
                    value,
                    out var color))
            {
                RestoreDefaultTheme(webView);
                return;
            }

            _pageThemeColorApplied = true;

            var requestedTheme =
                GetContrastingTheme(color);

            Toolbar.RequestedTheme =
                requestedTheme;

            MainTabView.RequestedTheme =
                requestedTheme;

            Toolbar.Background =
                new SolidColorBrush(color);

            _tabViewSelectedBackgroundBrush.Color =
                color;

            webView.DefaultBackgroundColor =
                Windows.UI.Color.FromArgb(
                    255,
                    color.R,
                    color.G,
                    color.B);

            int dimAmount =
                requestedTheme == ElementTheme.Light
                    ? 6
                    : 8;

            var tabViewBackground =
                Windows.UI.Color.FromArgb(
                    color.A,
                    (byte)Math.Max(
                        0,
                        color.R - dimAmount),
                    (byte)Math.Max(
                        0,
                        color.G - dimAmount),
                    (byte)Math.Max(
                        0,
                        color.B - dimAmount));

            MainTabView.Background =
                new SolidColorBrush(
                    tabViewBackground);

            RightTabViewRectangle.Fill =
                MainTabView.Background;
        }
        catch
        {
            RestoreDefaultTheme(webView);
        }
    }

    public async Task ApplyThemeColorTintAsync(
        bool enabled)
    {
        if (!enabled)
        {
            RestoreDefaultTheme(SelectedWebView);
            return;
        }

        if (SelectedWebView is { } webView &&
            webView.CoreWebView2 is { } core)
        {
            await UpdateThemeColorFromPageAsync(
                webView,
                core);
        }
        else
        {
            RestoreDefaultTheme(null);
        }
    }

    private void RestoreDefaultTheme(
        WebView2? webView)
    {
        _pageThemeColorApplied = false;

        Toolbar.RequestedTheme =
            ElementTheme.Default;

        MainTabView.RequestedTheme =
            ElementTheme.Default;

        Toolbar.ClearValue(
            Panel.BackgroundProperty);

        MainTabView.Background =
            new SolidColorBrush(
                Windows.UI.Color.FromArgb(
                    0,
                    0,
                    0,
                    0));

        RightTabViewRectangle.ClearValue(
            Shape.FillProperty);

        ApplyDefaultThemeVisuals();

        if (webView is not null)
        {
            webView.DefaultBackgroundColor =
                GetDefaultWebViewBackgroundColor();
        }
    }

    private void RestoreDefaultToolbarBackground()
    {
        RestoreDefaultTheme(SelectedWebView);
    }

    private void ApplyDefaultThemeVisuals()
    {
        Toolbar.RequestedTheme =
            ElementTheme.Default;

        MainTabView.RequestedTheme =
            ElementTheme.Default;

        MainTabView.Background =
            new SolidColorBrush(
                Windows.UI.Color.FromArgb(
                    0,
                    0,
                    0,
                    0));

        RightTabViewRectangle.ClearValue(
            Shape.FillProperty);

        var defaultColor =
            Toolbar.ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(
                    0x4C,
                    0x3A,
                    0x3A,
                    0x3A)
                : Windows.UI.Color.FromArgb(
                    0x80,
                    0xFF,
                    0xFF,
                    0xFF);

        Toolbar.Background =
            new SolidColorBrush(
                defaultColor);

        _tabViewSelectedBackgroundBrush.Color =
            defaultColor;
    }

    private Windows.UI.Color GetDefaultWebViewBackgroundColor()
    {
        return Toolbar.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(
                255,
                0x20,
                0x20,
                0x20)
            : Windows.UI.Color.FromArgb(
                255,
                0xF3,
                0xF3,
                0xF3);
    }

    private static ElementTheme GetContrastingTheme(
        Windows.UI.Color color)
    {
        double brightness =
            Math.Sqrt(
                0.299 * color.R * color.R +
                0.587 * color.G * color.G +
                0.114 * color.B * color.B);

        return brightness >= 128
            ? ElementTheme.Light
            : ElementTheme.Dark;
    }

    private static bool TryParseCssColor(
        string value,
        out Windows.UI.Color color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        value =
            value.Trim().ToLowerInvariant();

        if (value[0] == '#')
        {
            var hex =
                value[1..];

            if (hex.Length == 3)
            {
                hex = string.Concat(
                    hex.SelectMany(
                        c => new[] { c, c }));
            }

            if (hex.Length is not (6 or 8))
                return false;

            try
            {
                var r =
                    Convert.ToByte(
                        hex[..2],
                        16);

                var g =
                    Convert.ToByte(
                        hex[2..4],
                        16);

                var b =
                    Convert.ToByte(
                        hex[4..6],
                        16);

                var a =
                    hex.Length == 8
                        ? Convert.ToByte(
                            hex[6..8],
                            16)
                        : (byte)255;

                color =
                    Windows.UI.Color.FromArgb(
                        a,
                        r,
                        g,
                        b);

                return true;
            }
            catch
            {
                return false;
            }
        }

        if (value.StartsWith("rgb"))
        {
            var start =
                value.IndexOf('(');

            var end =
                value.LastIndexOf(')');

            if (start < 0 ||
                end <= start)
            {
                return false;
            }

            var parts =
                value[(start + 1)..end]
                    .Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries);

            if (parts.Length is not (3 or 4) ||
                !byte.TryParse(
                    parts[0],
                    out var r) ||
                !byte.TryParse(
                    parts[1],
                    out var g) ||
                !byte.TryParse(
                    parts[2],
                    out var b))
            {
                return false;
            }

            byte a = 255;

            if (parts.Length == 4)
            {
                if (!double.TryParse(
                        parts[3],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var alpha))
                {
                    return false;
                }

                a =
                    (byte)Math.Clamp(
                        alpha <= 1
                            ? Math.Round(alpha * 255)
                            : alpha,
                        0,
                        255);
            }

            color =
                Windows.UI.Color.FromArgb(
                    a,
                    r,
                    g,
                    b);

            return true;
        }

        return false;
    }
}
