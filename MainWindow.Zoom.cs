using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly Dictionary<WebView2, double> _zoomFactors = new();

    private double GetZoomFactor(WebView2 webView) =>
        _zoomFactors.TryGetValue(webView, out double zoom)
            ? zoom
            : 1.0;

    private async Task SetZoomFactorAsync(
        WebView2 webView,
        double zoom)
    {
        _zoomFactors[webView] = zoom;

        if (webView.CoreWebView2 is not { } core)
            return;

        string value = zoom.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        await core.ExecuteScriptAsync(
            $"document.documentElement.style.zoom = '{value}';");
    }

    private void NewTabMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        OpenNewTab("https://www.google.com");

    private void DuplicateTabMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedWebView?.Source is Uri uri)
            OpenNewTab(uri.AbsoluteUri);
    }

    private async void ZoomInMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedWebView is not WebView2 webView)
            return;

        await SetZoomFactorAsync(
            webView,
            Math.Min(GetZoomFactor(webView) + 0.25, 3.0));
    }

    private async void ZoomOutMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedWebView is not WebView2 webView)
            return;

        await SetZoomFactorAsync(
            webView,
            Math.Max(GetZoomFactor(webView) - 0.25, 0.25));
    }

    private async void ResetZoomMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedWebView is WebView2 webView)
            await SetZoomFactorAsync(webView, 1.0);
    }

    private void FindMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SelectedWebView?.Focus(FocusState.Programmatic);

    private void PrintMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SelectedWebView?.CoreWebView2?.ShowPrintUI(
            CoreWebView2PrintDialogKind.Browser);

    private void ViewSourceMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedWebView?.Source is Uri uri)
            OpenNewTab($"view-source:{uri}");
    }

    private void DevToolsMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SelectedWebView?.CoreWebView2?.OpenDevToolsWindow();
}
