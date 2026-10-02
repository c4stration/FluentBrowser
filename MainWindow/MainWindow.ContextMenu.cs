using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Text.Json;
using System.Threading.Tasks;

using FluentBrowser.Shared;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private void ShowWebViewContextMenu(
        WebView2 webView,
        CoreWebView2ContextMenuRequestedEventArgs args)
    {
        args.Handled = true;

        var menu = new MenuFlyout();
        var target = args.ContextMenuTarget;
        var core = webView.CoreWebView2;

        if (target.Kind == CoreWebView2ContextMenuTargetKind.SelectedText &&
            target.IsEditable)
        {
            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    "Cut",
                    "\uE8C6",
                    async (_, _) =>
                        await core.ExecuteScriptAsync(
                            "document.execCommand('cut')")));
        }

        if (target.Kind == CoreWebView2ContextMenuTargetKind.SelectedText)
        {
            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    "Copy",
                    "\uE8C8",
                    async (_, _) =>
                        await core.ExecuteScriptAsync(
                            "document.execCommand('copy')")));
        }

        if (target.IsEditable)
        {
            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    "Paste",
                    "\uE77F",
                    async (_, _) =>
                    {
                        string text = await Windows.ApplicationModel.DataTransfer.Clipboard
                            .GetContent()
                            .GetTextAsync();

                        string escaped = JsonSerializer.Serialize(text);

                        await core.ExecuteScriptAsync(
                            $"document.execCommand('insertText', false, {escaped});");
                    }));
        }
        else
        {
            AddNavigationContextMenuItems(menu, webView);
        }

        if (menu.Items.Count > 0)
            menu.ShowAt(webView, args.Location);
    }

    private void AddNavigationContextMenuItems(
        MenuFlyout menu,
        WebView2 webView)
    {
        menu.Items.Add(
            Helpers.CreateMenuItem(
                "Back",
                "\uE72B",
                (_, _) => webView.GoBack(),
                webView.CanGoBack));

        menu.Items.Add(
            Helpers.CreateMenuItem(
                "Refresh",
                "\uE72C",
                (_, _) => webView.Reload()));

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(
            Helpers.CreateMenuItem(
                "View page source",
                string.Empty,
                (_, _) =>
                {
                    if (webView.Source is Uri uri)
                        OpenNewTab($"view-source:{uri}");
                },
                webView.Source is not null));
    }
}
