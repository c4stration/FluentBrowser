using FluentBrowser.Pages;
using FluentBrowser.Shared;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

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

    private bool _middleClickPending;

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

    private async Task InitializeWebViewAsync(
        WebView2 webView,
        string url)
    {
        await webView.EnsureCoreWebView2Async(
            App.WebViewEnvironment);

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
        ConfigureWebMessageHandling(core);
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

    private void ConfigureWebMessageHandling(
        CoreWebView2 core)
    {
        core.WebMessageReceived += (_, args) =>
        {
            if (args.TryGetWebMessageAsString() == "fluentbrowser_f11")
                ToggleBrowserFullscreen();
        };

        _ = core.AddScriptToExecuteOnDocumentCreatedAsync("""
            document.addEventListener("keydown", event => {
                if (event.key === "F11") {
                    event.preventDefault();
                    event.stopPropagation();
                    chrome.webview.postMessage("fluentbrowser_f11");
                }
            }, true);
            """);
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
