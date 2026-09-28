using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private int _nextExtensionTabId = 1;

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

        AddNewTab(tab, active);

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
}
