using FluentBrowser.Controls;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;

namespace FluentBrowser;

public sealed class BrowserExtensionChangedEventArgs : EventArgs
{
    public string ExtensionId { get; }
    public bool IsEnabled { get; }

    public BrowserExtensionChangedEventArgs(
        string extensionId,
        bool isEnabled)
    {
        ExtensionId = extensionId;
        IsEnabled = isEnabled;
    }
}

public sealed partial class MainWindow
{
    private readonly TaskCompletionSource<CoreWebView2Profile>
        _browserProfileReady = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _extensionOperationLock = new(1, 1);

    private readonly Dictionary<WebView2, TaskCompletionSource<bool>>
        _certificateErrorDecisions = [];

    private readonly Dictionary<WebView2, string>
        _navigationUris = [];

    private readonly ObservableCollection<ExtensionListItem> _extensionItems = [];

    // WinUI keyboard accelerators do not receive input while WebView2 owns focus.
    // Each WebView gets a unique token so that only its document-start bridge can
    // invoke browser commands through WebMessageReceived.
    private readonly Dictionary<WebView2, string>
        _webViewShortcutTokens = [];

    private bool _middleClickPending;
    private InputKeyboardSource? _keyboardSource;
    public event EventHandler<BrowserExtensionChangedEventArgs>? BrowserExtensionChanged;
    public event EventHandler? BrowserExtensionsReset;

    public CoreWebView2Profile? GetBrowserProfile()
    {
        foreach (BrowserTab tab in _browserTabs.Values)
        {
            if (tab.WebView?.CoreWebView2 is { } core)
                return core.Profile;
        }

        return null;
    }

    public async Task<CoreWebView2Profile> GetBrowserProfileAsync()
    {
        await _browserProfileReady.Task;

        return GetBrowserProfile()
            ?? throw new InvalidOperationException(
                "No active WebView2 profile is available");
    }

    private async void ExtensionsFlyout_Opening(
        object sender,
        object e)
    {
        ShowExtensionsList();

        await RefreshExtensionsFlyoutAsync();
    }

    private async Task RefreshExtensionsFlyoutAsync()
    {
        try
        {
            CoreWebView2Profile profile =
                await GetBrowserProfileAsync();

            var extensions =
                await profile.GetBrowserExtensionsAsync();

            var items = new List<ExtensionListItem>();

            foreach (CoreWebView2BrowserExtension extension in extensions)
            {
                IconElement icon;

                string key =
                    $"ExtensionPath_{extension.Id}";

                string? iconPath = null;

                if (_settings.Values[key] is string extensionPath &&
                    Directory.Exists(extensionPath))
                {
                    string manifestPath =
                        Path.Combine(
                            extensionPath,
                            "manifest.json");

                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            string json =
                                await File.ReadAllTextAsync(
                                    manifestPath);

                            using JsonDocument document =
                                JsonDocument.Parse(json);

                            iconPath =
                                GetBestExtensionIconPath(
                                    extensionPath,
                                    document.RootElement);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(
                                $"Failed to read extension icon: {ex}");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(iconPath) &&
                    File.Exists(iconPath))
                {
                    icon = new BitmapIcon
                    {
                        UriSource = new Uri(iconPath),
                        Width = 20,
                        Height = 20,
                        ShowAsMonochrome = false
                    };
                }
                else
                {
                    icon = new FontIcon
                    {
                        Glyph = "\uF158"
                    };
                }

                var iconContainer = new Grid
                {
                    Width = 28,
                    Height = 28
                };

                iconContainer.Children.Add(icon);

                items.Add(
                    new ExtensionListItem(
                        extension.Id,
                        extension.Name,
                        extension.IsEnabled,
                        iconContainer));
            }

            _extensionItems.Clear();

            foreach (ExtensionListItem item in items)
                _extensionItems.Add(item);

            if (!ReferenceEquals(
                ExtensionsList.ItemsSource,
                _extensionItems))
            {
                ExtensionsList.ItemsSource = _extensionItems;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to load extensions: {ex}");
        }
    }

    public async Task SetExtensionEnabledAsync(
        string extensionId,
        bool isEnabled)
    {
        try
        {
            await _extensionOperationLock.WaitAsync();

            try
            {
                CoreWebView2Profile profile =
                    await GetBrowserProfileAsync();

                CoreWebView2BrowserExtension? extension =
                    (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(
                        candidate => string.Equals(
                            candidate.Id,
                            extensionId,
                            StringComparison.Ordinal));

                if (extension is null)
                    return;

                if (extension.IsEnabled == isEnabled)
                    return;

                await extension.EnableAsync(isEnabled);

                BrowserExtensionChanged?.Invoke(
                    this,
                    new BrowserExtensionChangedEventArgs(
                        extensionId,
                        extension.IsEnabled));
            }
            finally
            {
                _extensionOperationLock.Release();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to set extension '{extensionId}' enabled state: {ex}");
            throw;
        }
    }

    public async Task RemoveExtensionAsync(string extensionId)
    {
        try
        {
            await _extensionOperationLock.WaitAsync();
            try
            {
                CoreWebView2Profile profile =
                    await GetBrowserProfileAsync();

                CoreWebView2BrowserExtension? extension =
                    (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(
                        candidate => string.Equals(
                            candidate.Id,
                            extensionId,
                            StringComparison.Ordinal));

                if (extension is null)
                    return;

                await extension.RemoveAsync();
                _settings.Values.Remove($"ExtensionPath_{extensionId}");
            }
            finally
            {
                _extensionOperationLock.Release();
            }
        }
        finally
        {
            BrowserExtensionsReset?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task InstallExtensionAsync(string extensionPath)
    {
        try
        {
            await _extensionOperationLock.WaitAsync();
            try
            {
                CoreWebView2Profile profile =
                    await GetBrowserProfileAsync();

                profile.AreWebViewScriptApisEnabledForServiceWorkers = true;

                CoreWebView2BrowserExtension extension =
                    await profile.AddBrowserExtensionAsync(extensionPath);

                _settings.Values[$"ExtensionPath_{extension.Id}"] =
                    extensionPath;
            }
            finally
            {
                _extensionOperationLock.Release();
            }
        }
        finally
        {
            BrowserExtensionsReset?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ExtensionToggle_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle ||
            toggle.DataContext is not ExtensionListItem item)
        {
            return;
        }

        // Apply the display value before subscribing. Programmatic refreshes
        // must never be interpreted as a user request to change an extension.
        toggle.Toggled -= ExtensionToggle_Toggled;
        toggle.Tag = item.ExtensionId;
        toggle.IsOn = item.IsEnabled;
        toggle.Toggled += ExtensionToggle_Toggled;
    }

    private async void ExtensionToggle_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle ||
            toggle.Tag is not string extensionId)
        {
            return;
        }

        toggle.IsEnabled = false;

        try
        {
            await SetExtensionEnabledAsync(extensionId, toggle.IsOn);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to toggle extension '{extensionId}': {ex}");
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    private async void ExtensionRemove_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item ||
            item.Tag is not string extensionId)
        {
            return;
        }

        try
        {
            await RemoveExtensionAsync(extensionId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to remove extension '{extensionId}': {ex}");
        }
    }

    public void ApplyWebTheme()
    {
        CoreWebView2Profile? profile = GetBrowserProfile();

        if (profile is null)
            return;

        profile.AreWebViewScriptApisEnabledForServiceWorkers = true;

        string webTheme =
            _settings.Values["WebTheme"] as string
            ?? "System";

        string appTheme =
            _settings.Values["Theme"] as string
            ?? "System";

        profile.PreferredColorScheme = webTheme switch
        {
            "FollowAppTheme" => appTheme switch
            {
                "Light" => CoreWebView2PreferredColorScheme.Light,
                "Dark" => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto
            },

            "Light" => CoreWebView2PreferredColorScheme.Light,
            "Dark" => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto
        };
    }

    private void ConfigureWebView(
        BrowserTab tab,
        WebView2 webView,
        TextBlock title,
        ProgressRing progressRing,
        Button audioButton,
        Image favicon,
        string url,
        bool initializeNavigation = true)
    {
        InitializeThemeHandling();

        RegisterNavigationEvents(
            tab,
            webView,
            title,
            progressRing,
            favicon);

        webView.CoreWebView2Initialized += (_, _) =>
        {
            if (webView.CoreWebView2 is not { } core)
                return;

            ApplyDownloadSettings(core.Profile);

            _browserProfileReady.TrySetResult(core.Profile);

            core.Settings.IsWebMessageEnabled = true;
            core.Settings.IsBuiltInErrorPageEnabled = false;

            ConfigureLoadProgressTracking(webView, core);

            core.Profile.AreWebViewScriptApisEnabledForServiceWorkers =
                true;

            webView.DefaultBackgroundColor = Colors.Transparent;

            _browserProfileReady.TrySetResult(core.Profile);

            _extensionServiceWorkerBridgeInitialization ??=
                InitializeExtensionServiceWorkerBridgeAsync(
                    core.Profile);

            RegisterCertificateHandling(webView, core);

            core.Settings.AreDefaultScriptDialogsEnabled =
                false;

            core.DocumentTitleChanged += (_, _) =>
                title.Text = core.DocumentTitle;

            if (webView.Tag is BrowserTab coreTab)
            {
                InitializeWebViewCore(
                    coreTab,
                    webView,
                    audioButton,
                    favicon);
            }
        };

        webView.PointerPressed += (_, args) =>
        {
            if (args.Pointer.PointerDeviceType !=
                Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                return;
            }

            var point = args.GetCurrentPoint(webView);

            if (point.Properties.IsMiddleButtonPressed)
                _middleClickPending = true;
        };

        webView.DefaultBackgroundColor =
            GetDefaultWebViewBackgroundColor();

        if (initializeNavigation)
            _ = InitializeWebViewAsync(webView, url);
    }

    private void InitializeKeyboardHandling()
    {
        if (RootGrid.XamlRoot is null)
            return;

        _keyboardSource = InputKeyboardSource.GetForIsland(
            RootGrid.XamlRoot.ContentIsland);

        _keyboardSource.KeyDown += OnKeyboardSourceKeyDown;
    }

    private void OnKeyboardSourceKeyDown(
        InputKeyboardSource sender,
        Microsoft.UI.Input.KeyEventArgs args)
    {
        VirtualKey key = args.VirtualKey;

        CoreVirtualKeyStates ctrlState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Control);

        CoreVirtualKeyStates shiftState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Shift);

        CoreVirtualKeyStates altState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Menu);

        bool ctrl = (ctrlState & CoreVirtualKeyStates.Down) != 0;
        bool shift = (shiftState & CoreVirtualKeyStates.Down) != 0;
        bool alt = (altState & CoreVirtualKeyStates.Down) != 0;

        if (TryHandleShortcut(key, ctrl, shift, alt))
            args.Handled = true;
    }

    private async Task InitializeWebViewAsync(
        WebView2 webView,
        string url)
    {
        await webView.EnsureCoreWebView2Async(
            App.WebViewEnvironment);

        if (webView.CoreWebView2 is { } core)
            await ConfigureWebMessageHandlingAsync(webView, core);

        await EnableLoadProgressTrackingAsync(webView);

        webView.Source = new Uri(url);
    }

    private void InitializeWebViewCore(
        BrowserTab tab,
        WebView2 webView,
        Button audioButton,
        Image favicon)
    {
        if (webView.CoreWebView2 is not { } core)
            return;

        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        ConfigureDownloadHandlers(core, webView);
        ConfigureDiagnostics(core);
        ConfigureStatusBarHandling(core);
        ConfigureScriptDialogHandling(core, webView);
        ConfigureNewWindowHandling(core);
        ConfigureMediaAndPageHandlers(
            tab,
            webView,
            core,
            audioButton,
            favicon);
    }

    private async Task ConfigureWebMessageHandlingAsync(
        WebView2 webView,
        CoreWebView2 core)
    {
        core.WebMessageReceived += (_, args) =>
        {
            string message = args.TryGetWebMessageAsString();

            if (ReferenceEquals(SelectedWebView, webView) &&
                _webViewShortcutTokens.TryGetValue(
                    webView,
                    out string? token) &&
                message.StartsWith(
                    $"fluentbrowser-shortcut:{token}:",
                    StringComparison.Ordinal))
            {
                TryHandleWebViewShortcut(
                    message[("fluentbrowser-shortcut:".Length +
                             token.Length + 1)..]);
            }
        };

        string token = Guid.NewGuid().ToString("N");
        _webViewShortcutTokens[webView] = token;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            BuildWebViewShortcutBridgeScript(token));
    }

    private bool TryHandleWebViewShortcut(string shortcut)
    {
        return shortcut switch
        {
            "new-tab" => TryInvokeConfigurable(BrowserShortcut.NewTab),
            "close-tab" => TryInvokeConfigurable(BrowserShortcut.CloseTab),
            "reopen-tab" => TryInvokeConfigurable(BrowserShortcut.ReopenTab),
            "focus-address" => TryInvokeConfigurable(BrowserShortcut.FocusAddressBar),
            "reload" => TryInvokeConfigurable(BrowserShortcut.Reload),
            "next-tab" => TryInvokeConfigurable(BrowserShortcut.NextTab),
            "previous-tab" => TryInvokeConfigurable(BrowserShortcut.PreviousTab),
            "find-on-page" => TryInvokeConfigurable(BrowserShortcut.FindOnPage),
            "hard-reload" => TryHandleShortcut(
                VirtualKey.R, ctrl: true, shift: true, alt: false),
            "reload-f5" => TryHandleShortcut(
                VirtualKey.F5, ctrl: false, shift: false, alt: false),
            "zoom-in" => TryHandleShortcut(
                (VirtualKey)187, ctrl: true, shift: false, alt: false),
            "zoom-out" => TryHandleShortcut(
                (VirtualKey)189, ctrl: true, shift: false, alt: false),
            "zoom-reset" => TryHandleShortcut(
                VirtualKey.Number0, ctrl: true, shift: false, alt: false),
            "back" => TryHandleShortcut(
                VirtualKey.Left, ctrl: false, shift: false, alt: true),
            "forward" => TryHandleShortcut(
                VirtualKey.Right, ctrl: false, shift: false, alt: true),
            "duplicate-tab" => TryHandleShortcut(
                VirtualKey.D, ctrl: true, shift: true, alt: false),
            "dev-tools" => TryHandleShortcut(
                VirtualKey.F12, ctrl: false, shift: false, alt: false),
            "dev-tools-inspect" => TryHandleShortcut(
                VirtualKey.I, ctrl: true, shift: true, alt: false),
            "view-source" => TryHandleShortcut(
                VirtualKey.U, ctrl: true, shift: false, alt: false),
            "print" => TryHandleShortcut(
                VirtualKey.P, ctrl: true, shift: false, alt: false),
            "fullscreen" => HandleFullscreenShortcut(),
            _ => false
        };
    }

    private bool TryInvokeConfigurable(BrowserShortcut action)
    {
        if (_shortcuts[action].Keys.Count == 0)
            return false;

        InvokeShortcut(action);
        return true;
    }

    private bool HandleFullscreenShortcut()
    {
        ToggleBrowserFullscreen();
        return true;
    }

    private void RefreshWebViewShortcuts()
    {
        foreach (var pair in _webViewShortcutTokens)
        {
            WebView2 webView = pair.Key;
            string token = pair.Value;

            if (webView.CoreWebView2 is null)
                continue;

            _ = webView.CoreWebView2.ExecuteScriptAsync(
                BuildWebViewShortcutBridgeScript(token));
        }
    }

    private string BuildWebViewShortcutBridgeScript(string token)
    {
        string newTab = BuildJsMatchCondition(BrowserShortcut.NewTab);
        string closeTab = BuildJsMatchCondition(BrowserShortcut.CloseTab);
        string reopenTab = BuildJsMatchCondition(BrowserShortcut.ReopenTab);
        string focusAddress = BuildJsMatchCondition(BrowserShortcut.FocusAddressBar);
        string reload = BuildJsMatchCondition(BrowserShortcut.Reload);
        string nextTab = BuildJsMatchCondition(BrowserShortcut.NextTab);
        string previousTab = BuildJsMatchCondition(BrowserShortcut.PreviousTab);
        string findOnPage = BuildJsMatchCondition(BrowserShortcut.FindOnPage);

        return $$"""
        (() => {
            const token = "{{token}}";
            const postMessage = chrome.webview.postMessage.bind(chrome.webview);
            const handlerFlag = "__fluentBrowserShortcutHandler";

            if (window[handlerFlag]) {
                window.removeEventListener("keydown", window[handlerFlag], true);
                document.removeEventListener("keydown", window[handlerFlag], true);
            }

            const handler = event => {
                const { altKey: alt, code, ctrlKey: ctrl, shiftKey: shift, metaKey: meta } = event;
                let shortcut = null;

                if ({{newTab}}) {
                    shortcut = "new-tab";
                } else if ({{closeTab}}) {
                    shortcut = "close-tab";
                } else if ({{reopenTab}}) {
                    shortcut = "reopen-tab";
                } else if ({{focusAddress}}) {
                    shortcut = "focus-address";
                } else if ({{reload}}) {
                    shortcut = "reload";
                } else if ({{nextTab}}) {
                    shortcut = "next-tab";
                } else if ({{previousTab}}) {
                    shortcut = "previous-tab";
                } else if ({{findOnPage}}) {
                    shortcut = "find-on-page";
                } else if (!ctrl && !shift && !alt) {
                    shortcut = code === "F5" ? "reload-f5"
                        : code === "F11" ? "fullscreen"
                        : code === "F12" ? "dev-tools"
                        : null;
                } else if (alt && !ctrl && !shift) {
                    shortcut = code === "ArrowLeft" ? "back"
                        : code === "ArrowRight" ? "forward"
                        : null;
                } else if (ctrl && !alt) {
                    shortcut = code === "KeyR" && shift ? "hard-reload"
                        : (code === "Equal" || code === "NumpadAdd") ? "zoom-in"
                        : (code === "Minus" || code === "NumpadSubtract") && !shift ? "zoom-out"
                        : code === "Digit0" && !shift ? "zoom-reset"
                        : code === "KeyD" && shift ? "duplicate-tab"
                        : code === "KeyI" && shift ? "dev-tools-inspect"
                        : code === "KeyU" && !shift ? "view-source"
                        : code === "KeyP" && !shift ? "print"
                        : code === "KeyE" && !shift ? "focus-address"
                        : null;
                }

                if (shortcut === null)
                    return;

                event.preventDefault();
                event.stopImmediatePropagation();
                postMessage(`fluentbrowser-shortcut:${token}:${shortcut}`);
            };

            window[handlerFlag] = handler;
            window.addEventListener("keydown", handler, true);
        })();
        """;
    }

    private string BuildJsMatchCondition(BrowserShortcut action)
    {
        HotkeySettings settings = _shortcuts[action];

        if (settings.Keys.Count == 0)
            return "false";

        bool wantCtrl = settings.Keys.Any(IsControlKey);
        bool wantShift = settings.Keys.Any(IsShiftKey);
        bool wantAlt = settings.Keys.Any(IsAltKey);
        bool wantWin = settings.Keys.Any(IsWindowsKey);

        VirtualKey? primary = settings.Keys
            .Where(k => !IsModifier(k))
            .Cast<VirtualKey?>()
            .FirstOrDefault();

        if (primary is null)
            return "false";

        string code = VirtualKeyToJsCode(primary.Value);

        return
            $"ctrl === {(wantCtrl ? "true" : "false")} && " +
            $"shift === {(wantShift ? "true" : "false")} && " +
            $"alt === {(wantAlt ? "true" : "false")} && " +
            $"meta === {(wantWin ? "true" : "false")} && " +
            $"code === \"{code}\"";
    }

    private static string VirtualKeyToJsCode(VirtualKey key)
    {
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
            return "Key" + key;

        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            return "Digit" + ((int)key - (int)VirtualKey.Number0);

        if (key >= VirtualKey.F1 && key <= VirtualKey.F24)
            return key.ToString();

        return key switch
        {
            VirtualKey.Tab => "Tab",
            VirtualKey.Enter => "Enter",
            VirtualKey.Escape => "Escape",
            VirtualKey.Space => "Space",
            VirtualKey.Left => "ArrowLeft",
            VirtualKey.Right => "ArrowRight",
            VirtualKey.Up => "ArrowUp",
            VirtualKey.Down => "ArrowDown",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Back => "Backspace",
            VirtualKey.Add => "NumpadAdd",
            VirtualKey.Subtract => "NumpadSubtract",
            (VirtualKey)187 => "Equal",
            (VirtualKey)189 => "Minus",
            _ => key.ToString()
        };
    }

    private void ConfigureDiagnostics(CoreWebView2 core)
    {
        core.ProcessFailed += (_, args) => // debug
        {
            Debug.WriteLine(
                $"WebView2 process failed: " +
                $"Kind={args.ProcessFailedKind}, " +
                $"Reason={args.Reason}");
        };
    }

    private void ConfigureStatusBarHandling(CoreWebView2 core)
    {
        core.StatusBarTextChanged += (_, _) =>
        {
            string text = core.StatusBarText;

            if (!string.IsNullOrEmpty(text))
            {
                StatusBar.Text = text;
                StatusBarBorder.Opacity = 1;
            }
            else
            {
                StatusBarBorder.Opacity = 0;
            }
        };
    }

    private void ConvertInternalTabToWeb(
        BrowserTab tab,
        Uri target)
    {
        var webView = new WebView2();

        tab.Content = webView;
        tab.ErrorPage = null;
        tab.WebView = webView;
        tab.TabItem.Content = webView;

        webView.Tag = tab;

        ConfigureTabAudioButton(
            webView,
            tab.AudioButton);

        ConfigureWebView(
            tab,
            webView,
            tab.Title,
            tab.ProgressRing,
            tab.AudioButton,
            tab.Favicon,
            target.AbsoluteUri);

        CurrentTabContent.Content = webView;
    }

    private static string? GetBestExtensionIconPath(
    string extensionPath,
    JsonElement manifest)
    {
        int[] preferredSizes = { 32, 48, 16, 64, 128, 24, 20 };

        if (TryGetIconFromObject(
                manifest,
                "icons",
                preferredSizes,
                extensionPath,
                out string? path))
        {
            return path;
        }

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
                    "default_icon",
                    out JsonElement defaultIcon))
            {
                if (defaultIcon.ValueKind == JsonValueKind.String)
                {
                    string? rel = defaultIcon.GetString();

                    if (!string.IsNullOrWhiteSpace(rel))
                    {
                        string full =
                            Path.Combine(extensionPath, rel);

                        if (File.Exists(full))
                            return full;
                    }
                }
                else if (TryGetIconFromObject(
                             action,
                             "default_icon",
                             preferredSizes,
                             extensionPath,
                             out path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static bool TryGetIconFromObject(
        JsonElement parent,
        string propertyName,
        int[] preferredSizes,
        string extensionPath,
        out string? fullPath)
    {
        fullPath = null;

        if (!parent.TryGetProperty(
                propertyName,
                out JsonElement icons) ||
            icons.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (int size in preferredSizes)
        {
            if (!icons.TryGetProperty(
                    size.ToString(),
                    out JsonElement iconEl))
            {
                continue;
            }

            string? rel = iconEl.GetString();

            if (string.IsNullOrWhiteSpace(rel))
                continue;

            string candidate =
                Path.Combine(extensionPath, rel);

            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }
        }

        foreach (JsonProperty prop in icons.EnumerateObject())
        {
            string? rel = prop.Value.GetString();

            if (string.IsNullOrWhiteSpace(rel))
                continue;

            string candidate =
                Path.Combine(extensionPath, rel);

            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }
        }

        return false;
    }
}

public sealed class ExtensionListItem : System.ComponentModel.INotifyPropertyChanged
{
    public string ExtensionId { get; }
    public string Name { get; }

    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;

            _isEnabled = value;

            PropertyChanged?.Invoke(
                this,
                new System.ComponentModel.PropertyChangedEventArgs(
                    nameof(IsEnabled)));
        }
    }

    public UIElement Icon { get; }

    public event System.ComponentModel.PropertyChangedEventHandler?
        PropertyChanged;

    public ExtensionListItem(
        string extensionId,
        string name,
        bool isEnabled,
        UIElement icon)
    {
        ExtensionId = extensionId;
        Name = name;
        _isEnabled = isEnabled;
        Icon = icon;
    }
}