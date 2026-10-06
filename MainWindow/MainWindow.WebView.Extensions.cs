using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly HashSet<CoreWebView2ServiceWorker>
        _extensionServiceWorkers = [];

    private CoreWebView2ServiceWorkerManager?
        _serviceWorkerManager;

    private Task? _extensionServiceWorkerBridgeInitialization;

    private readonly Dictionary<int, BrowserTab> _extensionTabMap = [];

    private readonly ObservableCollection<ExtensionListItem>
        _filteredExtensionItems = [];

    private int _nextExtensionTabId = 1;

    private bool _extensionPopupNewWindowHooked;

    private async Task InitializeExtensionServiceWorkerBridgeAsync(
        CoreWebView2Profile profile)
    {
        try
        {
            Debug.WriteLine("Initializing extension service worker bridge.");

            _serviceWorkerManager = profile.ServiceWorkerManager;

            _serviceWorkerManager.ServiceWorkerRegistered +=
                (_, args) =>
                {
                    CoreWebView2ServiceWorker? worker =
                        args.ServiceWorkerRegistration.ActiveServiceWorker;

                    if (worker is not null)
                    {
                        Debug.WriteLine(
                            $"Extension service worker registered: {worker.ScriptUri}");
                        AttachExtensionServiceWorker(worker);
                    }
                    else
                    {
                        Debug.WriteLine(
                            "A service worker registered without an active worker.");
                    }
                };

            IReadOnlyList<CoreWebView2ServiceWorkerRegistration> registrations =
                await _serviceWorkerManager.GetServiceWorkerRegistrationsAsync();

            Debug.WriteLine(
                $"Service worker bridge found {registrations.Count} registration(s).");

            foreach (CoreWebView2ServiceWorkerRegistration registration
                in registrations)
            {
                CoreWebView2ServiceWorker? worker =
                    registration.ActiveServiceWorker;

                if (worker is not null)
                {
                    Debug.WriteLine(
                        $"Existing extension service worker: {worker.ScriptUri}");
                    AttachExtensionServiceWorker(worker);
                }
                else
                {
                    Debug.WriteLine(
                        "An existing service-worker registration has no active worker.");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to initialize extension service worker bridge: {ex}");
        }
    }

    private void ExtensionServiceWorker_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (sender is not CoreWebView2ServiceWorker worker)
            return;

        // Service worker events aren't raised on the WinUI thread.  TabView and
        // WebView2 controls must only be accessed from the window's UI queue.
        string message = args.WebMessageAsJson;

        if (!DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Normal,
                () => ProcessExtensionTabMessage(worker, message)))
        {
            Debug.WriteLine("Unable to dispatch an extension tab message.");
        }
    }

    private void ProcessExtensionTabMessage(
        CoreWebView2ServiceWorker worker,
        string message)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(message);

            JsonElement root = document.RootElement;

            if (!root.TryGetProperty(
                    "type",
                    out JsonElement typeElement))
            {
                return;
            }

            if (typeElement.GetString() == "fluentbrowser.debug")
            {
                string debugMessage = root.TryGetProperty(
                    "message",
                    out JsonElement messageElement)
                    ? messageElement.GetString() ?? "<empty>"
                    : "<missing message>";

                Debug.WriteLine(
                    $"Extension debug [{worker.ScriptUri}]: {debugMessage}");
                return;
            }

            if (typeElement.GetString() != "fluentbrowser.tabs")
                return;

            if (!root.TryGetProperty(
                    "command",
                    out JsonElement commandElement))
            {
                return;
            }

            string? command = commandElement.GetString();

            switch (command)
            {
                case "create":
                    HandleExtensionTabCreate(worker, root);
                    break;

                case "remove":
                    HandleExtensionTabRemove(worker, root);
                    break;

                case "update":
                    HandleExtensionTabUpdate(worker, root);
                    break;

                case "query":
                    HandleExtensionTabQuery(worker, root);
                    break;

                case "get":
                    HandleExtensionTabGet(worker, root);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Extension tab message failed: {ex}");
        }
    }

    private void HandleExtensionTabCreate(
    CoreWebView2ServiceWorker worker,
    JsonElement message)
    {
        string url = "about:blank";

        if (message.TryGetProperty(
                "url",
                out JsonElement urlElement) &&
            urlElement.ValueKind == JsonValueKind.String)
        {
            url = urlElement.GetString() ?? "about:blank";
        }

        bool active = true;

        if (message.TryGetProperty(
                "active",
                out JsonElement activeElement) &&
            (activeElement.ValueKind == JsonValueKind.True ||
             activeElement.ValueKind == JsonValueKind.False))
        {
            active = activeElement.GetBoolean();
        }

        var tab = CreateNewTab(url);

        AddNewTab(tab, active, forceAfterCurrent: true);

        if (tab.Tag is not BrowserTab browserTab)
            return;

        worker.PostWebMessageAsJson(
            JsonSerializer.Serialize(new
            {
                type = "fluentbrowser.tabs.result",
                command = "create",
                requestId =
                    message.TryGetProperty(
                        "requestId",
                        out JsonElement requestId)
                        ? requestId.GetInt32()
                        : 0,
                tab = new
                {
                    id = browserTab.ExtensionTabId,
                    index = MainTabView.TabItems.IndexOf(tab),
                    active =
                        ReferenceEquals(
                            MainTabView.SelectedItem,
                            tab),
                    url
                }
            }));
    }

    private void HandleExtensionTabRemove(
    CoreWebView2ServiceWorker worker,
    JsonElement message)
    {
        int requestId =
            message.TryGetProperty(
                "requestId",
                out JsonElement requestIdElement)
                ? requestIdElement.GetInt32()
                : 0;

        if (!message.TryGetProperty(
                "tabId",
                out JsonElement tabIdElement) ||
            !tabIdElement.TryGetInt32(out int tabId))
        {
            return;
        }

        if (_extensionTabMap.TryGetValue(
                tabId,
                out BrowserTab? browserTab))
        {
            CloseTab(browserTab.TabItem);
        }

        worker.PostWebMessageAsJson(
            JsonSerializer.Serialize(new
            {
                type = "fluentbrowser.tabs.result",
                command = "remove",
                requestId
            }));
    }

    private void HandleExtensionTabUpdate(
    CoreWebView2ServiceWorker worker,
    JsonElement message)
    {
        int requestId =
            message.TryGetProperty(
                "requestId",
                out JsonElement requestIdElement)
                ? requestIdElement.GetInt32()
                : 0;

        if (!message.TryGetProperty(
                "tabId",
                out JsonElement tabIdElement) ||
            !tabIdElement.TryGetInt32(out int tabId))
        {
            return;
        }

        if (!_extensionTabMap.TryGetValue(
                tabId,
                out BrowserTab? browserTab))
        {
            return;
        }

        if (!message.TryGetProperty(
                "properties",
                out JsonElement properties))
        {
            return;
        }

        if (properties.TryGetProperty(
                "url",
                out JsonElement urlElement) &&
            urlElement.ValueKind == JsonValueKind.String &&
            browserTab.WebView is WebView2 webView)
        {
            string? url = urlElement.GetString();

            if (!string.IsNullOrWhiteSpace(url))
                webView.CoreWebView2?.Navigate(url);
        }

        if (properties.TryGetProperty(
                "active",
                out JsonElement activeElement) &&
            activeElement.ValueKind == JsonValueKind.True &&
            browserTab.TabItem is TabViewItem tab)
        {
            MainTabView.SelectedItem = tab;
        }

        worker.PostWebMessageAsJson(
            JsonSerializer.Serialize(new
            {
                type = "fluentbrowser.tabs.result",
                command = "update",
                requestId,
                tab = new
                {
                    id = browserTab.ExtensionTabId
                }
            }));
    }

    private void HandleExtensionTabQuery(
        CoreWebView2ServiceWorker worker,
        JsonElement message)
    {
        int requestId =
            message.TryGetProperty(
                "requestId",
                out JsonElement requestIdElement)
                ? requestIdElement.GetInt32()
                : 0;

        bool? filterActive = null;
        bool currentWindowOnly = true;

        if (message.TryGetProperty(
                "queryInfo",
                out JsonElement queryInfo) &&
            queryInfo.ValueKind == JsonValueKind.Object)
        {
            if (queryInfo.TryGetProperty(
                    "active",
                    out JsonElement activeElement) &&
                (activeElement.ValueKind == JsonValueKind.True ||
                 activeElement.ValueKind == JsonValueKind.False))
            {
                filterActive = activeElement.GetBoolean();
            }

            // Single-window browser: currentWindow is always true.
            // Accept the property so callers like SponsorBlock succeed.
            if (queryInfo.TryGetProperty(
                    "currentWindow",
                    out JsonElement currentWindowElement) &&
                (currentWindowElement.ValueKind == JsonValueKind.True ||
                 currentWindowElement.ValueKind == JsonValueKind.False))
            {
                currentWindowOnly = currentWindowElement.GetBoolean();
            }
        }

        // Single-window host: currentWindow is always this window.
        _ = currentWindowOnly;

        var tabs = new List<object>();

        for (int i = 0; i < MainTabView.TabItems.Count; i++)
        {
            if (MainTabView.TabItems[i] is not TabViewItem item ||
                item.Tag is not BrowserTab browserTab ||
                browserTab.IsClosed)
            {
                continue;
            }

            if (!_extensionTabMap.ContainsKey(browserTab.ExtensionTabId))
                continue;

            bool isActive = ReferenceEquals(
                MainTabView.SelectedItem,
                browserTab.TabItem);

            if (filterActive is true && !isActive)
                continue;

            if (filterActive is false && isActive)
                continue;

            tabs.Add(BuildExtensionTabInfo(browserTab, i));
        }

        worker.PostWebMessageAsJson(
            JsonSerializer.Serialize(new
            {
                type = "fluentbrowser.tabs.result",
                command = "query",
                requestId,
                tabs
            }));
    }

    private void HandleExtensionTabGet(
        CoreWebView2ServiceWorker worker,
        JsonElement message)
    {
        int requestId =
            message.TryGetProperty(
                "requestId",
                out JsonElement requestIdElement)
                ? requestIdElement.GetInt32()
                : 0;

        if (!message.TryGetProperty(
                "tabId",
                out JsonElement tabIdElement) ||
            !tabIdElement.TryGetInt32(out int tabId) ||
            !_extensionTabMap.TryGetValue(tabId, out BrowserTab? browserTab) ||
            browserTab.IsClosed)
        {
            worker.PostWebMessageAsJson(
                JsonSerializer.Serialize(new
                {
                    type = "fluentbrowser.tabs.result",
                    command = "get",
                    requestId,
                    tab = (object?)null
                }));
            return;
        }

        int index = MainTabView.TabItems.IndexOf(browserTab.TabItem);

        worker.PostWebMessageAsJson(
            JsonSerializer.Serialize(new
            {
                type = "fluentbrowser.tabs.result",
                command = "get",
                requestId,
                tab = BuildExtensionTabInfo(browserTab, index)
            }));
    }

    private object BuildExtensionTabInfo(
        BrowserTab browserTab,
        int index)
    {
        string url = "about:blank";
        string title = browserTab.Title.Text ?? string.Empty;

        if (browserTab.WebView is WebView2 webView)
        {
            if (webView.CoreWebView2 is { } core &&
                !string.IsNullOrWhiteSpace(core.Source))
            {
                url = core.Source;
            }
            else if (webView.Source is Uri sourceUri)
            {
                url = sourceUri.AbsoluteUri;
            }

            if (webView.CoreWebView2 is { } coreTitle &&
                !string.IsNullOrWhiteSpace(coreTitle.DocumentTitle))
            {
                title = coreTitle.DocumentTitle;
            }
        }

        bool isActive = ReferenceEquals(
            MainTabView.SelectedItem,
            browserTab.TabItem);

        return new
        {
            id = browserTab.ExtensionTabId,
            index,
            active = isActive,
            url,
            title,
            status = "complete",
            windowId = 1
        };
    }

    private void AttachExtensionServiceWorker(
        CoreWebView2ServiceWorker worker)
    {
        if (!_extensionServiceWorkers.Add(worker))
            return;

        Debug.WriteLine(
            $"Attached extension worker bridge: {worker.ScriptUri}");

        worker.WebMessageReceived +=
            ExtensionServiceWorker_WebMessageReceived;

        worker.Destroying += (_, _) =>
        {
            Debug.WriteLine(
                $"Extension service worker destroying: {worker.ScriptUri}");
            _extensionServiceWorkers.Remove(worker);
        };
    }

    private void ExtensionsSearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        UpdateExtensionSearchResults(sender.Text);
    }

    private void UpdateExtensionSearchResults(string query)
    {
        query = query.Trim();

        var matchingItems = new List<ExtensionListItem>();

        foreach (ExtensionListItem item in _extensionItems)
        {
            if (string.IsNullOrWhiteSpace(query) ||
                item.Name.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase))
            {
                matchingItems.Add(item);
            }
        }

        for (int i = _filteredExtensionItems.Count - 1; i >= 0; i--)
        {
            if (!matchingItems.Contains(_filteredExtensionItems[i]))
                _filteredExtensionItems.RemoveAt(i);
        }

        for (int i = 0; i < matchingItems.Count; i++)
        {
            ExtensionListItem item = matchingItems[i];

            if (i >= _filteredExtensionItems.Count)
            {
                _filteredExtensionItems.Add(item);
                continue;
            }

            if (ReferenceEquals(_filteredExtensionItems[i], item))
                continue;

            int existingIndex = _filteredExtensionItems.IndexOf(item);

            if (existingIndex >= 0)
                _filteredExtensionItems.Move(existingIndex, i);
            else
                _filteredExtensionItems.Insert(i, item);
        }
    }

    private async void ExtensionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not ExtensionListItem item)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            IsInsideToggleSwitch(source, button))
        {
            return;
        }

        try
        {
            string? extensionPath =
                _settings.Values[$"ExtensionPath_{item.ExtensionId}"]
                as string;

            if (string.IsNullOrWhiteSpace(extensionPath))
                return;

            string manifestPath =
                Path.Combine(extensionPath, "manifest.json");

            if (!File.Exists(manifestPath))
                return;

            string json = await File.ReadAllTextAsync(manifestPath);

            using JsonDocument document =
                JsonDocument.Parse(json);

            JsonElement manifest = document.RootElement;

            string? popupPath = null;

            foreach (string key in new[]
            {
            "action",
            "browser_action",
            "page_action"
        })
            {
                if (!manifest.TryGetProperty(
                        key,
                        out JsonElement action))
                {
                    continue;
                }

                if (action.TryGetProperty(
                        "default_popup",
                        out JsonElement popup) &&
                    popup.ValueKind == JsonValueKind.String)
                {
                    popupPath = popup.GetString();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(popupPath))
                return;

            popupPath = popupPath.Replace('\\', '/').TrimStart('/');

            string popupFile =
                Path.GetFullPath(
                    Path.Combine(extensionPath, popupPath));

            if (!File.Exists(popupFile))
                return;

            await ExtensionWebView.EnsureCoreWebView2Async(
                App.WebViewEnvironment);

            if (ExtensionWebView.CoreWebView2 is not { } core)
                return;

            core.Profile.AreWebViewScriptApisEnabledForServiceWorkers =
                true;

            EnsureExtensionPopupNewWindowHandling();

            string preferredUrl = "about:blank";
            string preferredTitle = string.Empty;

            if (MainTabView.SelectedItem is TabViewItem selectedItem &&
                selectedItem.Tag is BrowserTab selectedTab &&
                selectedTab.WebView is WebView2 selectedWebView)
            {
                if (selectedWebView.CoreWebView2 is { } selectedCore &&
                    !string.IsNullOrWhiteSpace(selectedCore.Source))
                {
                    preferredUrl = selectedCore.Source;
                    preferredTitle = selectedCore.DocumentTitle ?? string.Empty;
                }
                else if (selectedWebView.Source is Uri sourceUri)
                {
                    preferredUrl = sourceUri.AbsoluteUri;
                }
            }

            string polyfill = BuildExtensionPopupTabsPolyfill(
                preferredUrl,
                preferredTitle);

            // MUST use chrome-extension:// — file:// only paints the background.
            string extensionUri =
                $"chrome-extension://{item.ExtensionId}/{popupPath}";

            string virtualHost =
                $"extension-{item.ExtensionId}.local";

            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    virtualHost,
                    extensionPath,
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception mapEx)
            {
                Debug.WriteLine(
                    $"Virtual host mapping for extension popup failed: {mapEx}");
            }

            ExtensionWebView.DefaultBackgroundColor =
                Colors.Transparent;

            ShowExtensionPopup();

            async void OnNavigationCompleted(
                object? s,
                CoreWebView2NavigationCompletedEventArgs args)
            {
                core.NavigationCompleted -= OnNavigationCompleted;

                if (!args.IsSuccess)
                {
                    Debug.WriteLine(
                        $"chrome-extension:// popup navigation failed " +
                        $"(status={args.HttpStatusCode}, error={args.WebErrorStatus}); " +
                        "falling back to virtual host.");

                    core.Navigate(
                        $"https://{virtualHost}/{popupPath}");
                    return;
                }

                try
                {
                    await core.ExecuteScriptAsync(polyfill);
                }
                catch (Exception injectEx)
                {
                    Debug.WriteLine(
                        $"Failed to inject tabs polyfill: {injectEx}");
                }
            }

            core.NavigationCompleted += OnNavigationCompleted;
            core.Navigate(extensionUri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to open extension popup: {ex}");
        }
    }

    private void EnsureExtensionPopupNewWindowHandling()
    {
        if (_extensionPopupNewWindowHooked ||
            ExtensionWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        _extensionPopupNewWindowHooked = true;

        core.NewWindowRequested += async (_, args) =>
        {
            var deferral = args.GetDeferral();

            try
            {
                string url = string.IsNullOrWhiteSpace(args.Uri)
                    ? "about:blank"
                    : args.Uri;

                Debug.WriteLine(
                    $"Extension popup requested new window: {url}");

                var newTab = CreateNewTab(
                    "about:blank",
                    initializeNavigation: false);

                AddNewTab(
                    newTab,
                    autoSelect: true,
                    forceAfterCurrent: true);

                if (newTab.Tag is not BrowserTab
                    {
                        WebView: WebView2 newWebView
                    })
                {
                    args.Handled = true;
                    return;
                }

                await newWebView.EnsureCoreWebView2Async(
                    App.WebViewEnvironment);

                await EnableLoadProgressTrackingAsync(newWebView);

                if (newWebView.CoreWebView2 is { } newCore)
                {
                    args.NewWindow = newCore;
                }
                else
                {
                    args.Handled = true;
                    OpenNewTab(url);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Failed to host extension popup window as tab: {ex}");
                args.Handled = true;

                if (!string.IsNullOrWhiteSpace(args.Uri))
                    OpenNewTab(args.Uri);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    private static string BuildExtensionPopupTabsPolyfill(
        string preferredUrl,
        string preferredTitle)
    {
        string urlJson = JsonSerializer.Serialize(preferredUrl);
        string titleJson = JsonSerializer.Serialize(preferredTitle);

        return $$"""
        (() => {
            if (!globalThis.chrome || !chrome.tabs || !chrome.tabs.query)
                return;

            const preferredUrl = {{urlJson}};
            const preferredTitle = {{titleJson}};
            const originalQuery = globalThis.__fluentBrowserOriginalTabsQuery
                || chrome.tabs.query.bind(chrome.tabs);
            globalThis.__fluentBrowserOriginalTabsQuery = originalQuery;

            function isExtensionPage(url) {
                if (!url || typeof url !== "string")
                    return false;
                return url.startsWith("chrome-extension://")
                    || url.startsWith("edge-extension://")
                    || url.startsWith("extension://")
                    || url.startsWith("about:")
                    || url.startsWith("chrome://")
                    || url.startsWith("edge://");
            }

            function scoreTab(tab) {
                const url = tab && tab.url ? String(tab.url) : "";
                if (!url || isExtensionPage(url))
                    return -1;
                if (preferredUrl && url === preferredUrl)
                    return 100;
                try {
                    if (preferredUrl) {
                        const a = new URL(url);
                        const b = new URL(preferredUrl);
                        if (a.hostname === b.hostname)
                            return 80;
                    }
                } catch (_) {}
                if (/youtube\.com|youtu\.be|youtube-nocookie\.com/i.test(url))
                    return 60;
                return 10;
            }

            function pickTabs(tabs, wantsActive) {
                const list = Array.isArray(tabs) ? tabs.slice() : [];
                const pageTabs = list.filter(t => scoreTab(t) >= 0);
                if (!wantsActive)
                    return pageTabs.length ? pageTabs : list.filter(t => !isExtensionPage(t && t.url));

                pageTabs.sort((a, b) => scoreTab(b) - scoreTab(a));
                if (pageTabs.length > 0)
                    return [pageTabs[0]];

                if (preferredUrl && !isExtensionPage(preferredUrl)) {
                    return [{
                        id: -1,
                        index: 0,
                        active: true,
                        url: preferredUrl,
                        title: preferredTitle || preferredUrl,
                        status: "complete",
                        windowId: 1
                    }];
                }
                return [];
            }

            chrome.tabs.query = function(queryInfo, callback) {
                const qi = queryInfo && typeof queryInfo === "object"
                    ? { ...queryInfo }
                    : {};
                const wantsActive = qi.active === true;
                if (wantsActive)
                    delete qi.active;
                delete qi.currentWindow;
                delete qi.lastFocusedWindow;

                const finish = (tabs) => {
                    const result = pickTabs(tabs, wantsActive);
                    if (typeof callback === "function")
                        callback(result);
                    return result;
                };

                try {
                    const ret = originalQuery(qi, typeof callback === "function"
                        ? (tabs) => finish(tabs)
                        : undefined);

                    if (ret && typeof ret.then === "function")
                        return ret.then(finish);

                    return ret;
                } catch (err) {
                    const fallback = pickTabs([], wantsActive);
                    if (typeof callback === "function")
                        callback(fallback);
                    return Promise.resolve(fallback);
                }
            };
        })();
        """;
    }

    private void ExtensionsBackButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowExtensionsList();
    }

    private void ShowExtensionPopup()
    {
        ExtensionsList.Visibility = Visibility.Collapsed;
        ExtensionsTextBlock.Visibility = Visibility.Collapsed;
        ExtensionsBackButton.Visibility = Visibility.Visible;
        ExtensionWebView.Visibility = Visibility.Visible;
    }

    private void ShowExtensionsList()
    {
        ExtensionWebView.Visibility = Visibility.Collapsed;
        ExtensionsBackButton.Visibility = Visibility.Collapsed;
        ExtensionsTextBlock.Visibility = Visibility.Visible;
        ExtensionsList.Visibility = Visibility.Visible;
    }

    private static bool IsInsideToggleSwitch(
        DependencyObject source,
        DependencyObject button)
    {
        while (source is not null &&
               !ReferenceEquals(source, button))
        {
            if (source is ToggleSwitch)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}