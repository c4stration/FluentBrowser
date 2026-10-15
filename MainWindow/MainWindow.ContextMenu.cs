using FluentBrowser.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using WinUI3Localizer;

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

        if (target.HasLinkUri)
        {
            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    LocalizerExtensions.GetLocalizedString("OpenLinkInNewTab"),
                    "\uE8A7",
                    (_, _) => OpenNewTab(target.LinkUri)));

            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    LocalizerExtensions.GetLocalizedString("CopyLink"),
                    "\uE71B",
                    (_, _) =>
                    {
                        var package =
                            new Windows.ApplicationModel.DataTransfer.DataPackage();

                        package.SetText(target.LinkUri);

                        Windows.ApplicationModel.DataTransfer.Clipboard
                            .SetContent(package);
                    }));

            menu.Items.Add(new MenuFlyoutSeparator());
        }

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

        bool hasExtensionContextMenuItems = args.MenuItems.Any(
            item => item.Name == "extension");

        if (hasExtensionContextMenuItems)
        {
            var moreMenu =
                Helpers.CreateContextMenuItem<MenuFlyoutSubItem>(
                    "Show more options",
                    "\uE712");

            await AddExtensionContextMenuItemsAsync(moreMenu, args);

            if (moreMenu.Items.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(moreMenu);
            }
        }

        if (menu.Items.Count > 0)
        {
            foreach (MenuFlyoutItemBase item in menu.Items)
                SetContextMenuItemFlowDirection(item, App.CurrentFlowDirection);

            menu.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter));
            menu.MenuFlyoutPresenterStyle.Setters.Add(
                new Setter(
                    FrameworkElement.FlowDirectionProperty,
                    App.CurrentFlowDirection));

            menu.ShowAt(webView, args.Location);
        }
        else
        {
            deferral.Complete();
        }
    }

    private static void SetContextMenuItemFlowDirection(
        MenuFlyoutItemBase item,
        FlowDirection direction)
    {
        if (item is FrameworkElement element)
            element.FlowDirection = direction;

        if (item is MenuFlyoutSubItem submenu)
        {
            foreach (MenuFlyoutItemBase child in submenu.Items)
                SetContextMenuItemFlowDirection(child, direction);
        }
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

    private static async Task AddExtensionContextMenuItemsAsync(
        MenuFlyoutSubItem parent,
        CoreWebView2ContextMenuRequestedEventArgs args)
    {
        foreach (CoreWebView2ContextMenuItem item in args.MenuItems)
        {
            if (item.Name != "extension")
                continue;

            parent.Items.Add(
                await CreateExtensionContextMenuItemAsync(
                    item,
                    args,
                    "extension-root"));
        }
    }

    private static async Task<MenuFlyoutItemBase>
    CreateExtensionContextMenuItemAsync(
        CoreWebView2ContextMenuItem source,
        CoreWebView2ContextMenuRequestedEventArgs args,
        string radioGroupName)
    {
        MenuFlyoutItemBase item;

        switch (source.Kind)
        {
            case CoreWebView2ContextMenuItemKind.Separator:
                return new MenuFlyoutSeparator();

            case CoreWebView2ContextMenuItemKind.Submenu:
                {
                    var submenu = new MenuFlyoutSubItem
                    {
                        Text = source.Label.Replace("&", string.Empty),
                        IsEnabled = source.IsEnabled
                    };

                    if (source.Children is { } children)
                    {
                        string childGroupName =
                            $"{radioGroupName}_{source.CommandId}";

                        foreach (CoreWebView2ContextMenuItem child in children)
                        {
                            submenu.Items.Add(
                                await CreateExtensionContextMenuItemAsync(
                                    child,
                                    args,
                                    childGroupName));
                        }
                    }

                    item = submenu;
                    break;
                }

            case CoreWebView2ContextMenuItemKind.CheckBox:
                {
                    var checkbox = new ToggleMenuFlyoutItem
                    {
                        Text = source.Label.Replace("&", string.Empty),
                        IsChecked = source.IsChecked,
                        IsEnabled = source.IsEnabled
                    };

                    checkbox.Click += (_, _) =>
                        args.SelectedCommandId = source.CommandId;

                    item = checkbox;
                    break;
                }

            case CoreWebView2ContextMenuItemKind.Radio:
                {
                    var radio = new RadioMenuFlyoutItem
                    {
                        Text = source.Label.Replace("&", string.Empty),
                        IsChecked = source.IsChecked,
                        IsEnabled = source.IsEnabled,
                        GroupName = radioGroupName
                    };

                    radio.Click += (_, _) =>
                        args.SelectedCommandId = source.CommandId;

                    item = radio;
                    break;
                }

            default:
                {
                    var command = Helpers.CreateContextMenuItem(
                        source.Label.Replace("&", string.Empty),
                        string.Empty,
                        (_, _) => args.SelectedCommandId = source.CommandId,
                        source.ShortcutKeyDescription);

                    command.IsEnabled = source.IsEnabled;
                    item = command;
                    break;
                }
        }

        IconElement? icon =
            await CreateExtensionContextMenuIconAsync(
                source.Icon,
                source.Label);

        if (icon is not null)
            SetExtensionContextMenuIcon(item, icon);

        return item;
    }

    private static async Task<IconElement?>
        CreateExtensionContextMenuIconAsync(
            IRandomAccessStream? source,
            string label)
    {
        try
        {
            if (source is null || source.Size == 0)
            {
                Debug.WriteLine(
                    $"No icon stream available for extension menu item: {label}");

                return null;
            }

            // Use an independent stream so decoding does not depend on
            // the original WebView2-provided stream's lifetime or position.
            using var imageStream = new InMemoryRandomAccessStream();

            source.Seek(0);
            await RandomAccessStream.CopyAsync(source, imageStream);
            imageStream.Seek(0);

            // Inspect the header to distinguish SVG from raster images.
            uint headerLength =
                (uint)Math.Min(imageStream.Size, 512UL);

            byte[] headerBytes = new byte[checked((int)headerLength)];

            using (var reader = new DataReader(
                imageStream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(headerLength);
                reader.ReadBytes(headerBytes);
            }

            string header = Encoding.UTF8.GetString(headerBytes);
            imageStream.Seek(0);

            ImageSource imageSource;

            if (header.Contains(
                    "<svg",
                    StringComparison.OrdinalIgnoreCase))
            {
                var svg = new SvgImageSource();
                await svg.SetSourceAsync(imageStream);
                imageSource = svg;
            }
            else
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(imageStream);
                imageSource = bitmap;
            }

            return new ImageIcon
            {
                Source = imageSource,
                Width = 16,
                Height = 16
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to load extension context-menu icon " +
                $"for '{label}': {ex}");

            return null;
        }
    }

    private static void SetExtensionContextMenuIcon(
        MenuFlyoutItemBase item,
        IconElement icon)
    {
        switch (item)
        {
            case MenuFlyoutItem menuItem:
                menuItem.Icon = icon;
                break;

            case MenuFlyoutSubItem submenu:
                submenu.Icon = icon;
                break;
        }
    }

    private static MenuFlyoutItemBase CreateExtensionContextMenuItem(
        CoreWebView2ContextMenuItem source,
        CoreWebView2ContextMenuRequestedEventArgs args,
        string radioGroupName)
    {
        switch (source.Kind)
        {
            case CoreWebView2ContextMenuItemKind.Separator:
                return new MenuFlyoutSeparator();

            case CoreWebView2ContextMenuItemKind.Submenu:
                {
                    var submenu = new MenuFlyoutSubItem
                    {
                        Text = source.Label.Replace("&", string.Empty),
                        IsEnabled = source.IsEnabled
                    };

                    if (source.Children is { } children)
                    {
                        string childGroupName =
                            $"{radioGroupName}_{source.CommandId}";

                        foreach (CoreWebView2ContextMenuItem child in children)
                        {
                            submenu.Items.Add(
                                CreateExtensionContextMenuItem(
                                    child,
                                    args,
                                    childGroupName));
                        }
                    }

                    return submenu;
                }

            case CoreWebView2ContextMenuItemKind.CheckBox:
                {
                    var item = new ToggleMenuFlyoutItem
                    {
                        Text = source.Label.Replace("&", string.Empty),
                        IsChecked = source.IsChecked,
                        IsEnabled = source.IsEnabled
                    };

                    item.Click += (_, _) =>
                        args.SelectedCommandId = source.CommandId;

                    return item;
                }

            case CoreWebView2ContextMenuItemKind.Radio:
                {
                    var item = new RadioMenuFlyoutItem
                    {
                        Text = source.Label.Replace("&", string.Empty),
                        IsChecked = source.IsChecked,
                        IsEnabled = source.IsEnabled,
                        GroupName = radioGroupName
                    };

                    item.Click += (_, _) =>
                        args.SelectedCommandId = source.CommandId;

                    return item;
                }

            default:
                {
                    var item = Helpers.CreateContextMenuItem(
                        source.Label.Replace("&", string.Empty),
                        string.Empty,
                        (_, _) => args.SelectedCommandId = source.CommandId,
                        source.ShortcutKeyDescription);

                    item.IsEnabled = source.IsEnabled;
                    return item;
                }
        }
    }
}