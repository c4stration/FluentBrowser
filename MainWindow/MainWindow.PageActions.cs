using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using System;

namespace FluentBrowser;

public sealed partial class MainWindow
{
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
