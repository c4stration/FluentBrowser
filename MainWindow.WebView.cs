using FluentBrowser.Controls;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly TaskCompletionSource<CoreWebView2Profile>
        _browserProfileReady = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Dictionary<WebView2, TaskCompletionSource<bool>>
        _certificateErrorDecisions = [];

    private readonly Dictionary<WebView2, string>
        _navigationUris = [];

    // WinUI keyboard accelerators do not receive input while WebView2 owns focus.
    // Each WebView gets a unique token so that only its document-start bridge can
    // invoke browser commands through WebMessageReceived.
    private readonly Dictionary<WebView2, string>
        _webViewShortcutTokens = [];

    private bool _middleClickPending;
    private InputKeyboardSource? _keyboardSource;

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
            "hard-reload" => TryHandleShortcut(
                VirtualKey.R, ctrl: true, shift: true, alt: false),
            "reload-f5" => TryHandleShortcut(
                VirtualKey.F5, ctrl: false, shift: false, alt: false),
            "next-tab" => TryHandleShortcut(
                VirtualKey.Tab, ctrl: true, shift: false, alt: false),
            "previous-tab" => TryHandleShortcut(
                VirtualKey.Tab, ctrl: true, shift: true, alt: false),
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
                            : code === "Tab" ? (shift ? "previous-tab" : "next-tab")
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
}