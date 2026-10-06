using FluentBrowser.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    // A navigation that turns into a download completes with ConnectionAborted
    // Keep the navigation identity so that result is not presented as a broken page when DownloadStarting arrives immediately before or after it!
    private readonly Dictionary<WebView2, DownloadNavigation>
        _downloadNavigations = [];

    private sealed class DownloadNavigation(ulong navigationId)
    {
        public ulong NavigationId { get; } = navigationId;

        public bool DownloadStarted { get; set; }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWebView is not { CanGoBack: true } webView)
            return;

        webView.GoBack();
        webView.Focus(FocusState.Programmatic);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWebView is not { CanGoForward: true } webView)
            return;

        webView.GoForward();
        webView.Focus(FocusState.Programmatic);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        SelectedWebView?.Reload();

    private static void Navigate(WebView2 webView, Uri uri)
    {
        webView.Source = uri;
        webView.Focus(FocusState.Programmatic);
    }

    private void OpenNewTab(string url, bool autoSelect = true)
    {
        var tab = CreateNewTab(url);

        AddNewTab(tab, autoSelect);
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled =
            SelectedWebView?.CanGoBack == true;

        ForwardButton.IsEnabled =
            SelectedWebView?.CanGoForward == true;
    }

    private void UpdateLoadingUI(WebView2 webView)
    {
        bool loading = _loadingWebViews.Contains(webView);
        bool shouldShowProgress = loading ||
            (_loadProgressTrackers.TryGetValue(
                webView,
                out var tracker) &&
             tracker.ShouldShowProgress);

        if (shouldShowProgress)
            ShowLoadingProgress(webView);
        else
            HideLoadingProgress();

        VisualStateManager.GoToState(
            RefreshButton,
            loading ? "Loading" : "NotLoading",
            false);
    }

    private void UpdateLoadingState(
        WebView2 webView,
        bool isLoading)
    {
        if (!ReferenceEquals(SelectedWebView, webView))
            return;

        if (isLoading)
            ShowLoadingProgress(webView);
        else
            HideLoadingProgress();

        VisualStateManager.GoToState(
            RefreshButton,
            isLoading ? "Loading" : "NotLoading",
            true);
    }

    private void RegisterNavigationEvents(
    BrowserTab tab,
    WebView2 webView,
    TextBlock title,
    ProgressRing progressRing,
    Image favicon)
    {
        webView.NavigationStarting += (_, args) =>
        {
            Debug.WriteLine(
                $"Tab {tab.ExtensionTabId} navigation starting: {args.Uri}");

            _navigationUris[webView] = args.Uri;
            _downloadNavigations[webView] = new DownloadNavigation(
                args.NavigationId);

            if (_certificateErrorDecisions.Remove(
                    webView,
                    out TaskCompletionSource<bool>? decision))
            {
                decision.TrySetResult(false);
            }

            tab.ErrorPage = null;

            if (ReferenceEquals(SelectedWebView, webView))
                CurrentTabContent.Content = tab.Content;

            _loadingWebViews.Add(webView);
            BeginLoadingProgress(webView);

            if (Uri.TryCreate(
                    args.Uri,
                    UriKind.Absolute,
                    out Uri? uri))
            {
                UpdateAddressBar(uri);
            }

            if (ReferenceEquals(SelectedWebView, webView))
            {
                ApplyDefaultThemeVisuals();

                webView.DefaultBackgroundColor =
                    GetDefaultWebViewBackgroundColor();
            }

            favicon.Source = null;
            favicon.Visibility = Visibility.Collapsed;

            UpdateDefaultFavicon(tab, false);

            progressRing.IsActive = true;
            progressRing.Visibility = Visibility.Visible;

            UpdateLoadingState(webView, true);
        };

        webView.NavigationCompleted += async (_, args) =>
        {
            try
            {
                bool isPotentialDownload =
                    !args.IsSuccess &&
                    args.WebErrorStatus ==
                        CoreWebView2WebErrorStatus.ConnectionAborted;

                _loadingWebViews.Remove(webView);

                CompleteLoadingProgress(
                    webView,
                    !args.IsSuccess && !isPotentialDownload);

                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;

                if (!args.IsSuccess)
                {
                    if (args.WebErrorStatus ==
                        CoreWebView2WebErrorStatus.OperationCanceled)
                    {
                        tab.ErrorPage = null;
                    }
                    else if (isPotentialDownload)
                    {
                        // WebView2 can raise NavigationCompleted before
                        // DownloadStarting. Give that handoff a short time
                        // to arrive before declaring the navigation broken.
                        await Task.Delay(500);

                        if (!IsTrackedNavigation(
                                webView,
                                args.NavigationId))
                        {
                            return;
                        }

                        if (IsDownloadNavigation(
                                webView,
                                args.NavigationId))
                        {
                            tab.ErrorPage = null;
                        }
                        else if (args.HttpStatusCode == 0)
                        {
                            CompleteLoadingProgress(
                                webView,
                                hasError: true);

                            ShowNavigationErrorPage(
                                tab,
                                webView,
                                args.WebErrorStatus);
                        }

                        RemoveDownloadNavigation(
                            webView,
                            args.NavigationId);
                    }
                    else if (args.HttpStatusCode == 0)
                    {
                        ShowNavigationErrorPage(
                            tab,
                            webView,
                            args.WebErrorStatus);

                        RemoveDownloadNavigation(
                            webView,
                            args.NavigationId);
                    }
                }
                else if (webView.CoreWebView2 is { } core)
                {
                    RemoveDownloadNavigation(
                        webView,
                        args.NavigationId);

                    tab.ErrorPage = null;
                    webView.Visibility = Visibility.Visible;

                    if (ReferenceEquals(SelectedWebView, webView))
                        CurrentTabContent.Content = tab.Content;

                    title.Text = core.DocumentTitle;

                    if (string.IsNullOrWhiteSpace(core.FaviconUri))
                    {
                        favicon.Source = null;
                        favicon.Visibility = Visibility.Collapsed;

                        UpdateDefaultFavicon(tab, true);
                    }
                    else
                    {
                        try
                        {
                            await UpdateFaviconAsync(tab);
                        }
                        catch (TaskCanceledException)
                        {
                            Debug.WriteLine(
                                $"Favicon update canceled for tab " +
                                $"{tab.ExtensionTabId}.");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(
                                $"Favicon update failed for tab " +
                                $"{tab.ExtensionTabId}: {ex}");
                        }
                    }

                    if (ReferenceEquals(SelectedWebView, webView))
                    {
                        try
                        {
                            await UpdateThemeColorFromPageAsync(
                                webView,
                                core);
                        }
                        catch (TaskCanceledException)
                        {
                            Debug.WriteLine(
                                $"Theme color update canceled for tab " +
                                $"{tab.ExtensionTabId}.");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(
                                $"Theme color update failed for tab " +
                                $"{tab.ExtensionTabId}: {ex}");
                        }
                    }
                }

                if (!ReferenceEquals(SelectedWebView, webView))
                    return;

                VisualStateManager.GoToState(
                    RefreshButton,
                    "NotLoading",
                    true);

                UpdateNavigationButtons();
                UpdateAddressBar(webView);
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine(
                    $"NavigationCompleted task canceled for tab " +
                    $"{tab.ExtensionTabId}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"NavigationCompleted failed for tab " +
                    $"{tab.ExtensionTabId}: {ex}");
            }
        };
    }

    private bool IsDownloadNavigation(WebView2 webView, ulong navigationId) =>
        _downloadNavigations.TryGetValue(
            webView,
            out DownloadNavigation? navigation) &&
        navigation.NavigationId == navigationId &&
        navigation.DownloadStarted;

    private bool IsTrackedNavigation(WebView2 webView, ulong navigationId) =>
        _downloadNavigations.TryGetValue(
            webView,
            out DownloadNavigation? navigation) &&
        navigation.NavigationId == navigationId;

    private void RemoveDownloadNavigation(WebView2 webView, ulong navigationId)
    {
        if (_downloadNavigations.TryGetValue(
                webView,
                out DownloadNavigation? navigation) &&
            navigation.NavigationId == navigationId)
        {
            _downloadNavigations.Remove(webView);
        }
    }

    private void ShowNavigationErrorPage(
        BrowserTab tab,
        WebView2 webView,
        CoreWebView2WebErrorStatus errorStatus)
    {
        string url =
            _navigationUris.TryGetValue(
                webView,
                out string? navigationUrl)
                    ? navigationUrl
                    : webView.Source?.AbsoluteUri ?? string.Empty;

        tab.ErrorPage = new CantOpenPage(errorStatus, url);

        if (tab.ErrorPage is CantOpenPage errorPage)
        {
            errorPage.ContinueRequested += (_, _) =>
            {
                if (_certificateErrorDecisions.TryGetValue(
                        webView,
                        out TaskCompletionSource<bool>? decision))
                {
                    decision.TrySetResult(true);
                }
            };
        }

        webView.Visibility = Visibility.Collapsed;

        if (ReferenceEquals(SelectedWebView, webView))
        {
            CurrentTabContent.Content = tab.ErrorPage;
            RestoreDefaultToolbarBackground();
        }
    }

    private void ConfigureNewWindowHandling(
        CoreWebView2 core)
    {
        core.NewWindowRequested += async (_, args) =>
        {
            var deferral = args.GetDeferral();

            try
            {
                Debug.WriteLine(
                    $"New window requested: {args.Uri}, " +
                    $"middle click: {_middleClickPending}");

                var newTab = CreateNewTab(
                    "about:blank",
                    initializeNavigation: false);

                AddNewTab(
                    newTab,
                    autoSelect: !_middleClickPending,
                    forceAfterCurrent: _middleClickPending);

                int hostTabId = newTab.Tag is BrowserTab createdTab
                    ? createdTab.ExtensionTabId
                    : 0;

                Debug.WriteLine(
                    $"Creating host tab {hostTabId} for requested window " +
                    $"'{args.Uri}'.");

                if (newTab.Tag is BrowserTab
                    {
                        WebView: WebView2 newWebView
                    })
                {
                    try
                    {
                        await newWebView.EnsureCoreWebView2Async(
                            App.WebViewEnvironment);

                        await EnableLoadProgressTrackingAsync(
                            newWebView);

                        if (newWebView.CoreWebView2 is { } newCore)
                        {
                            args.NewWindow = newCore;

                            Debug.WriteLine(
                                $"Bound requested window '{args.Uri}' " +
                                $"to host tab {hostTabId}.");
                        }
                        else
                        {
                            Debug.WriteLine(
                                $"Failed to initialize host tab " +
                                $"{hostTabId} for '{args.Uri}'.");
                            args.Handled = true;
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        Debug.WriteLine(
                            $"New window initialization canceled for " +
                            $"host tab {hostTabId}.");
                        args.Handled = true;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine(
                    $"New window request canceled: {args.Uri}");
                args.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Failed to host new window: {ex}");
                args.Handled = true;
            }
            finally
            {
                _middleClickPending = false;
                deferral.Complete();
            }
        };
    }
}
