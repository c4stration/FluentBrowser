using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using WinUI3Localizer;

using FluentBrowser.Shared;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private async void ShowWebViewContextMenu(
        WebView2 webView,
        CoreWebView2ContextMenuRequestedEventArgs args)
    {
        args.Handled = true;

        var menu = new MenuFlyout();
        var target = args.ContextMenuTarget;
        var core = webView.CoreWebView2;
        bool hasTextToSelect = false;

        bool hasSelection = target.HasSelection;

        if (target.IsEditable)
        {
            var cutItem = Helpers.CreateContextMenuItem(
                LocalizerExtensions.GetLocalizedString("Cut"),
                "\uE8C6",
                async (_, _) =>
                    await core.ExecuteScriptAsync(
                        "document.execCommand('cut')"),
                "Ctrl+X");

            cutItem.IsEnabled = hasSelection;
            menu.Items.Add(cutItem);
        }

        if (target.IsEditable || hasSelection)
        {
            var copyItem = Helpers.CreateContextMenuItem(
                LocalizerExtensions.GetLocalizedString("Copy"),
                "\uE8C8",
                async (_, _) =>
                    await core.ExecuteScriptAsync(
                        "document.execCommand('copy')"),
                "Ctrl+C");

            copyItem.IsEnabled = hasSelection;
            menu.Items.Add(copyItem);
        }

        if (target.IsEditable)
        {
            string result = await core.ExecuteScriptAsync(
                """
                (() => {
                    const element = document.activeElement;

                    if (element instanceof HTMLInputElement ||
                        element instanceof HTMLTextAreaElement) {
                        return element.value.trim().length > 0;
                    }

                    if (element instanceof HTMLElement && element.isContentEditable) {
                        return (element.innerText || element.textContent || "").trim().length > 0;
                    }

                    return (document.body?.innerText || "").trim().length > 0;
                })();
                """);

            hasTextToSelect = bool.TryParse(result, out bool hasText) && hasText;

            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    LocalizerExtensions.GetLocalizedString("Paste"),
                    "\uE77F",
                    async (_, _) =>
                    {
                        string text = await Windows.ApplicationModel.DataTransfer.Clipboard
                            .GetContent()
                            .GetTextAsync();

                        string escaped = JsonSerializer.Serialize(text);

                        await core.ExecuteScriptAsync(
                            $"document.execCommand('insertText', false, {escaped});");
                    },
                    "Ctrl+V"));

            var deleteItem = Helpers.CreateContextMenuItem(
                LocalizerExtensions.GetLocalizedString("Delete"),
                "",
                async (_, _) =>
                    await core.ExecuteScriptAsync(
                        "document.execCommand('delete')"));

            deleteItem.IsEnabled =
                target.Kind == CoreWebView2ContextMenuTargetKind.SelectedText;

            menu.Items.Add(deleteItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var selectAllItem = Helpers.CreateContextMenuItem(
                LocalizerExtensions.GetLocalizedString("SelectAll"),
                "",
                async (_, _) =>
                    await core.ExecuteScriptAsync(
                        """
                        (() => {
                            const element = document.activeElement;

                            if (element instanceof HTMLInputElement ||
                                element instanceof HTMLTextAreaElement) {
                                element.select();
                            } else {
                                document.execCommand('selectAll');
                            }
                        })();
                        """),
                "Ctrl+A");
            selectAllItem.IsEnabled = hasTextToSelect;
            menu.Items.Add(selectAllItem);
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
                LocalizerExtensions.GetLocalizedString("WebBack"),
                "\uE72B",
                (_, _) => webView.GoBack(),
                webView.CanGoBack));

        menu.Items.Add(
            Helpers.CreateMenuItem(
                LocalizerExtensions.GetLocalizedString("TabContextRefresh"),
                "\uE72C",
                (_, _) => webView.Reload()));

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(
            Helpers.CreateMenuItem(
                LocalizerExtensions.GetLocalizedString("ViewSourceMenuItem"),
                string.Empty,
                (_, _) =>
                {
                    if (webView.Source is Uri uri)
                        OpenNewTab($"view-source:{uri}");
                },
                webView.Source is not null));
    }
}
