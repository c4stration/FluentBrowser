using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly double[] s_zoomLevels =
        [25, 50, 75, 100, 110, 125, 150, 175, 200, 250, 300, 400, 500];
    private const double MinimumZoomPercentage = 25;
    private const double MaximumZoomPercentage = 500;

    private readonly Dictionary<WebView2, double> _zoomFactors = new();
    private readonly HashSet<WebView2> _zoomHandlingInitialized = new();

    private bool _isUpdatingZoomControls;
    private bool _zoomControlsReady;

    private const string ZoomInputScript = """
        (() => {
            if (window.__fluentBrowserZoomInputInstalled)
                return;

            window.__fluentBrowserZoomInputInstalled = true;

            const post = command =>
                window.chrome?.webview?.postMessage(command);

            window.addEventListener("wheel", event => {
                if (!event.ctrlKey || event.deltaY === 0)
                    return;

                event.preventDefault();

                post(event.deltaY < 0
                    ? "fluentbrowser-zoom-in"
                    : "fluentbrowser-zoom-out");
            }, { capture: true, passive: false });

            window.addEventListener("keydown", event => {
                if (!event.ctrlKey || event.altKey || event.metaKey)
                    return;

                let command = null;

                if (event.key === "+" ||
                    event.key === "=" ||
                    event.code === "NumpadAdd") {
                    command = "fluentbrowser-zoom-in";
                } else if (event.key === "-" ||
                           event.key === "_" ||
                           event.code === "NumpadSubtract") {
                    command = "fluentbrowser-zoom-out";
                } else if (event.key === "0" ||
                           event.code === "Numpad0") {
                    command = "fluentbrowser-zoom-reset";
                }

                if (!command)
                    return;

                event.preventDefault();
                event.stopPropagation();
                post(command);
            }, true);
        })();
        """;

    private double GetZoomFactor(WebView2 webView) =>
        _zoomFactors.GetValueOrDefault(webView, 1.0);

    private double GetZoomPercentage(WebView2 webView) =>
        Math.Round(
             GetZoomFactor(webView) * 100,
             MidpointRounding.AwayFromZero);

    private double GetSelectedZoomPercentage() =>
        SelectedWebView is WebView2 webView
            ? GetZoomPercentage(webView)
            : 100;

    private double NormalizeZoomPercentage(double percentage) =>
        Math.Clamp(
            Math.Round(percentage, MidpointRounding.AwayFromZero),
            MinimumZoomPercentage,
            MaximumZoomPercentage);

    private static bool TryParseZoomPercentage(
        string? text,
        out double percentage)
    {
        string value = text?.Trim().TrimEnd('%').Trim()
            ?? string.Empty;

        return double.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out percentage);
    }

    private async Task SetZoomLevelAsync(double percentage)
    {
        if (SelectedWebView is not WebView2 webView)
        {
            UpdateZoomControls(100);
            return;
        }

        await SetZoomLevelAsync(webView, percentage);
    }

    private async Task SetZoomLevelAsync(
        WebView2 webView,
        double percentage)
    {
        percentage = NormalizeZoomPercentage(percentage);

        if (ReferenceEquals(SelectedWebView, webView))
            UpdateZoomControls(percentage);

        await SetZoomFactorAsync(webView, percentage / 100.0);
    }

    private async Task SetZoomFactorAsync(
    WebView2 webView,
    double zoomFactor)
    {
        _zoomFactors[webView] = zoomFactor;

        await ApplyZoomFactorAsync(webView, zoomFactor);
    }

    private async Task ApplyZoomFactorAsync(
        WebView2 webView,
        double zoomFactor)
    {
        if (webView.CoreWebView2 is not { } core)
            return;

        string value = zoomFactor.ToString(
            CultureInfo.InvariantCulture);

        await core.ExecuteScriptAsync(
            $"document.documentElement.style.zoom = '{value}';");
    }

    private void ZoomControls_Loaded(object sender, RoutedEventArgs e)
    {
        ZoomSlider.Minimum = MinimumZoomPercentage;
        ZoomSlider.Maximum = MaximumZoomPercentage;

        UpdateZoomControls(GetSelectedZoomPercentage());
        _zoomControlsReady = true;
    }

    private void UpdateZoomControls(double percentage)
    {
        if (ZoomSlider is null || ZoomComboBox is null)
            return;

        percentage = NormalizeZoomPercentage(percentage);

        _ = UpdateZoomButtonVisibilityAsync(percentage);

        _isUpdatingZoomControls = true;
        try
        {
            ZoomSlider.Value = percentage;

            string content = $"{percentage:0}%";

            ZoomComboBox.SelectedItem = ZoomComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                    item.Content?.ToString() == content);

            if (ZoomComboBox.SelectedItem is null)
                ZoomComboBox.Text = content;
        }
        finally
        {
            _isUpdatingZoomControls = false;
        }
    }

    private Task UpdateZoomButtonVisibilityAsync(double percentage)
    {
        bool shouldShow = percentage != 100 || ZoomTeachingTip.IsOpen;

        SetAddressBarTrailingButtonVisible(ZoomButton, shouldShow);

        if (shouldShow)
            ZoomTeachingTip.Target = ZoomButton;

        return Task.CompletedTask;
    }

    private async void ZoomComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_zoomControlsReady ||
            _isUpdatingZoomControls ||
            ZoomComboBox.SelectedItem is not ComboBoxItem item ||
            !TryParseZoomPercentage(item.Content?.ToString(), out double percentage))
        {
            return;
        }

        await SetZoomLevelAsync(percentage);
    }

    private async void ZoomComboBox_TextSubmitted(
        ComboBox sender,
        ComboBoxTextSubmittedEventArgs e)
    {
        if (!_zoomControlsReady || _isUpdatingZoomControls)
            return;

        if (!TryParseZoomPercentage(e.Text, out double percentage))
        {
            e.Handled = true;
            UpdateZoomControls(GetSelectedZoomPercentage());
            return;
        }

        percentage = NormalizeZoomPercentage(percentage);

        await SetZoomLevelAsync(percentage);

        UpdateZoomControls(percentage);
        sender.Text = $"{percentage:0}%";
    }

    private async void ZoomSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (!_zoomControlsReady || _isUpdatingZoomControls)
            return;

        await SetZoomLevelAsync(e.NewValue);
    }

    private async Task AdjustSelectedZoomAsync(double delta, double limit)
    {
        if (SelectedWebView is not WebView2 webView)
            return;

        double percentage = GetZoomPercentage(webView) + delta;

        percentage = delta > 0
            ? Math.Min(percentage, limit)
            : Math.Max(percentage, limit);

        await SetZoomLevelAsync(percentage);
    }

    private async void ZoomInMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        await AdjustSelectedZoomAsync(25, 300);
    }

    private async void ZoomOutMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        await AdjustSelectedZoomAsync(-25, 25);
    }

    private async void ResetZoomMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        await SetZoomLevelAsync(100);
    }

    private void ZoomButton_Click(object sender, RoutedEventArgs e)
    {
        ZoomTeachingTip.Target = ZoomButton;
        ZoomTeachingTip.IsOpen = !ZoomTeachingTip.IsOpen;

        if (ZoomTeachingTip.IsOpen)
            UpdateZoomControls(GetSelectedZoomPercentage());
    }

    private Task ChangeZoomLevelAsync(bool zoomIn)
    {
        return SelectedWebView is WebView2 webView
            ? ChangeZoomLevelAsync(webView, zoomIn)
            : Task.CompletedTask;
    }

    private async Task ChangeZoomLevelAsync(
        WebView2 webView,
        bool zoomIn)
    {
        double currentPercentage = GetZoomPercentage(webView);

        double newPercentage = zoomIn
            ? s_zoomLevels.FirstOrDefault(
                level => level > currentPercentage,
                s_zoomLevels[^1])
            : s_zoomLevels.LastOrDefault(
                level => level < currentPercentage,
                s_zoomLevels[0]);

        await SetZoomLevelAsync(webView, newPercentage);
    }

    private async void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        await ChangeZoomLevelAsync(zoomIn: true);
    }

    private async void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        await ChangeZoomLevelAsync(zoomIn: false);
    }

    private void ZoomTeachingTip_Closed(
        TeachingTip sender,
        TeachingTipClosedEventArgs args)
    {
        _ = UpdateZoomButtonVisibilityAsync(GetSelectedZoomPercentage());
    }

    private async Task InitializeZoomHandlingAsync(WebView2 webView)
    {
        if (webView.CoreWebView2 is not { } core ||
            !_zoomHandlingInitialized.Add(webView))
        {
            return;
        }

        core.Settings.IsZoomControlEnabled = false;

        core.WebMessageReceived += async (_, e) =>
        {
            switch (e.WebMessageAsJson)
            {
                case "\"fluentbrowser-zoom-in\"":
                    await ChangeZoomLevelAsync(webView, zoomIn: true);
                    break;

                case "\"fluentbrowser-zoom-out\"":
                    await ChangeZoomLevelAsync(webView, zoomIn: false);
                    break;

                case "\"fluentbrowser-zoom-reset\"":
                    await SetZoomLevelAsync(webView, 100);
                    break;
            }
        };

        core.NavigationCompleted += async (_, _) =>
        {
            await ApplyZoomFactorAsync(
                webView,
                GetZoomFactor(webView));

            if (ReferenceEquals(SelectedWebView, webView))
                UpdateZoomControls(GetZoomPercentage(webView));
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            ZoomInputScript);

        await core.ExecuteScriptAsync(ZoomInputScript);

        await ApplyZoomFactorAsync(
            webView,
            GetZoomFactor(webView));
    }
}