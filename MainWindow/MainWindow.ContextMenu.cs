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
        var deferral = args.GetDeferral();
        args.Handled = true;

        var menu = new MenuFlyout();
        menu.Closed += (_, _) => deferral.Complete();

        CoreWebView2ContextMenuItem? pasteCommand = null;

        foreach (var item in args.MenuItems)
        {
            if (item.Name == "paste")
            {
                pasteCommand = item;
                break;
            }
        }

        var target = args.ContextMenuTarget;
        var core = webView.CoreWebView2;
        bool hasTextToSelect = false;

        bool hasSelection = target.HasSelection;

        if (target.Kind == CoreWebView2ContextMenuTargetKind.Image &&
            target.HasSourceUri)
        {
            AddImageContextMenuItems(menu, args, target.SourceUri);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

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

            var pasteItem = Helpers.CreateContextMenuItem(
                LocalizerExtensions.GetLocalizedString("Paste"),
                "\uE77F",
                (_, _) =>
                {
                    if (pasteCommand is not null)
                        args.SelectedCommandId = pasteCommand.CommandId;
                },
                "Ctrl+V");

            pasteItem.IsEnabled = pasteCommand is not null;
            menu.Items.Add(pasteItem);

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
        else
            deferral.Complete();
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

    private void AddImageContextMenuItems(
        MenuFlyout menu,
        CoreWebView2ContextMenuRequestedEventArgs args,
        string imageUri)
    {
        var saveImageCommand =
            FindContextMenuCommand(args, "saveImageAs");

        var copyImageCommand =
            FindContextMenuCommand(args, "copyImage");

        var saveImageItem = Helpers.CreateContextMenuItem(
            LocalizerExtensions.GetLocalizedString("SaveImageAs"),
            "\uE792",
            (_, _) =>
            {
                if (saveImageCommand is not null)
                    args.SelectedCommandId = saveImageCommand.CommandId;
            });

        saveImageItem.IsEnabled = saveImageCommand is not null;
        menu.Items.Add(saveImageItem);

        var copyImageItem = Helpers.CreateContextMenuItem(
            LocalizerExtensions.GetLocalizedString("CopyImage"),
            "\uE8C8",
            (_, _) =>
            {
                if (copyImageCommand is not null)
                    args.SelectedCommandId = copyImageCommand.CommandId;
            });

        copyImageItem.IsEnabled = copyImageCommand is not null;
        menu.Items.Add(copyImageItem);

        menu.Items.Add(
            Helpers.CreateContextMenuItem(
                LocalizerExtensions.GetLocalizedString("CopyImageLink"),
                (string)Application.Current.Resources["CopyPath"],
                (_, _) =>
                {
                    var package =
                        new Windows.ApplicationModel.DataTransfer.DataPackage();

                    package.SetText(imageUri);

                    Windows.ApplicationModel.DataTransfer.Clipboard
                        .SetContent(package);
                }));

        menu.Items.Add(
            Helpers.CreateContextMenuItem(
                LocalizerExtensions.GetLocalizedString("OpenImageInNewTab"),
                string.Empty,
                (_, _) => OpenNewTab(imageUri)));
    }

    private static CoreWebView2ContextMenuItem? FindContextMenuCommand(
        CoreWebView2ContextMenuRequestedEventArgs args,
        string name)
    {
        foreach (CoreWebView2ContextMenuItem item in args.MenuItems)
        {
            if (string.Equals(
                    item.Name,
                    name,
                    StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }
}
