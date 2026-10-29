using FluentBrowser.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WinUI3Localizer;

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
        if (TryNavigateBackToInternalPage())
            return;

        if (SelectedWebView is not { CanGoBack: true } webView)
            return;

        webView.GoBack();
        webView.Focus(FocusState.Programmatic);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTab is BrowserTab tab &&
            tab.HistoryPage is not null &&
            ReferenceEquals(tab.Content, tab.HistoryPage) &&
            tab.WebView is WebView2 startPageWebView &&
            tab.WebHistoryIndex >= 0 &&
            tab.WebHistoryAfterInternalPage.Count > 0)
        {
            tab.Content = startPageWebView;
            startPageWebView.Visibility = Visibility.Visible;
            CurrentTabContent.Content = startPageWebView;

            if (startPageWebView.CoreWebView2 is { } core)
            {
                tab.Title.Text = core.DocumentTitle;
                _ = UpdateFaviconAsync(tab);
                _ = UpdateThemeColorFromPageAsync(
                    startPageWebView,
                    core);
            }

            UpdateAddressBar(startPageWebView, force: true);
            UpdateLoadingUI(startPageWebView);
            UpdateAudioButton();
            UpdateNavigationButtons();
            UpdateZoomControls(GetZoomPercentage(startPageWebView));
            startPageWebView.Focus(FocusState.Programmatic);
            return;
        }

        if (SelectedWebView is not { CanGoForward: true } webView)
            return;

        if (SelectedTab is BrowserTab historyTab &&
            historyTab.HistoryPage is not null &&
            !ReferenceEquals(historyTab.Content, historyTab.HistoryPage))
        {
            historyTab.PendingWebHistoryMove = 1;
        }

        webView.GoForward();
        webView.Focus(FocusState.Programmatic);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        SelectedWebView?.Reload();

    private void Navigate(BrowserTab tab, Uri uri)
    {
        if (TryGetInternalPageType(uri, out Type? pageType))
        {
            NavigateToInternalPage(tab, pageType);
            return;
        }

        if (tab.HistoryPage is not null &&
            ReferenceEquals(tab.Content, tab.HistoryPage))
        {
            if (tab.WebView is WebView2)
                ReplaceRetainedInternalPageWebView(tab, uri);
            else
                ConvertInternalTabToWeb(tab, uri);

            return;
        }

        if (tab.WebView is not WebView2 webView)
        {
            ConvertInternalTabToWeb(tab, uri);
            return;
        }

        webView.Source = uri;
        webView.Focus(FocusState.Programmatic);
    }

    private static bool IsInternalPageUri(Uri uri) =>
        TryGetInternalPageType(uri, out _);

    private static bool TryGetInternalPageType(Uri uri, out Type? pageType)
    {
        pageType = null;

        if (!string.Equals(
                uri.Scheme,
                "fluentbrowser",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(
                uri.Host,
                "settings",
                StringComparison.OrdinalIgnoreCase))
        {
            pageType = typeof(SettingsPage);
            return true;
        }

        if (string.Equals(
                uri.Host,
                "start",
                StringComparison.OrdinalIgnoreCase))
        {
            pageType = typeof(StartPage);
            return true;
        }

        return false;
    }

    private static string? GetInternalPageUri(FrameworkElement page) => page switch
    {
        SettingsPage => "fluentbrowser://settings",
        StartPage => "fluentbrowser://start",
        _ => null
    };

    private void NavigateToInternalPage(BrowserTab tab, Type? pageType)
    {
        if (pageType is null)
            return;

        string targetUri = pageType == typeof(SettingsPage)
            ? "fluentbrowser://settings"
            : "fluentbrowser://start";

        if (tab.HistoryPage is FrameworkElement retainedPage &&
            string.Equals(
                tab.HistoryPageUri,
                targetUri,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!ReferenceEquals(tab.Content, retainedPage) &&
                tab.WebView is WebView2 retainedWebView)
            {
                tab.WebHistoryIndex = tab.WebHistoryAfterInternalPage.Count - 1;
                RestoreInternalPage(tab, retainedWebView);
            }
            else
            {
                tab.Content = retainedPage;
                PresentInternalPage(tab);
            }

            return;
        }

        FrameworkElement? previousPage = tab.HistoryPage;
        if (previousPage is null &&
            tab.Content is not WebView2 &&
            tab.Content is IDisposable currentDisposable)
        {
            currentDisposable.Dispose();
        }
        else if (previousPage is IDisposable previousDisposable)
            previousDisposable.Dispose();

        FrameworkElement page = CreatePageInstance(pageType);

        tab.HistoryPage = page;
        tab.HistoryPageUri = targetUri;
        tab.WebHistoryAfterInternalPage.Clear();
        tab.WebHistoryIndex = -1;
        tab.PendingWebHistoryMove = 0;
        tab.ErrorPage = null;
        tab.Content = page;

        if (tab.WebView?.Source is Uri currentUri && IsHttpUri(currentUri))
        {
            tab.WebHistoryAfterInternalPage.Add(currentUri.AbsoluteUri);
            tab.WebHistoryIndex = 0;
        }

        if (page is SettingsPage settingsPage)
            _ = LoadSettingsPageExtensionsAsync(settingsPage);

        PresentInternalPage(tab);
    }

    private void PresentInternalPage(BrowserTab tab)
    {
        if (tab.HistoryPage is not FrameworkElement page)
            return;

        tab.Content = page;
        tab.ErrorPage = null;
        ApplyInternalPageTabHeader(tab, page);

        if (tab.WebView is WebView2 webView)
            webView.Visibility = Visibility.Collapsed;

        if (!ReferenceEquals(SelectedTab, tab))
            return;

        CurrentTabContent.Content = page;

        if (page is StartPage)
            ClearAddressBarForStartPage();
        else if (tab.HistoryPageUri is string internalUri &&
                 Uri.TryCreate(internalUri, UriKind.Absolute, out Uri? uri))
            UpdateAddressBar(uri, force: true);

        LoadingProgressBar.Visibility = Visibility.Collapsed;
        SetAddressBarTrailingButtonVisible(AudioToggleButton, false);
        UpdateNavigationButtons();
        UpdateZoomControls(100);
        RestoreDefaultToolbarBackground();
    }

    private void ApplyInternalPageTabHeader(BrowserTab tab, FrameworkElement page)
    {
        tab.Title.Text = page switch
        {
            SettingsPage => LocalizerExtensions.GetLocalizedString("SettingsTitle"),
            StartPage => LocalizerExtensions.GetLocalizedString("StartTitle"),
            _ => page.GetType().Name
        };

        tab.ProgressRing.IsActive = false;
        tab.ProgressRing.Visibility = Visibility.Collapsed;
        tab.AudioButton.Visibility = Visibility.Collapsed;
        tab.Favicon.Source = null;
        tab.Favicon.Visibility = Visibility.Collapsed;

        if (tab.TabItem.Header is Grid header &&
            header.Children.Count > 0 &&
            header.Children[0] is Grid iconGrid)
        {
            FontIcon? pageIcon = iconGrid.Children.OfType<FontIcon>().FirstOrDefault();
            if (pageIcon is null)
            {
                pageIcon = new FontIcon { FontSize = 16 };
                iconGrid.Children.Add(pageIcon);
            }

            pageIcon.Glyph = page switch
            {
                StartPage => "\uE734",
                SettingsPage => "\uE713",
                _ => "\uE713"
            };
            pageIcon.Visibility = Visibility.Visible;
        }
    }

    private void OpenNewTab(string url, bool autoSelect = true)
    {
        var tab = CreateNewTab(url);

        AddNewTab(tab, autoSelect);
    }

    private void UpdateNavigationButtons()
    {
        if (SelectedTab is BrowserTab tab &&
            tab.HistoryPage is not null &&
            ReferenceEquals(tab.Content, tab.HistoryPage))
        {
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled =
                tab.WebHistoryAfterInternalPage.Count > 0 &&
                tab.WebHistoryIndex >= 0;

            return;
        }

        BackButton.IsEnabled =
            (SelectedTab is BrowserTab
            {
                HistoryPage: not null,
                WebHistoryIndex: 0
            }) ||
            SelectedWebView?.CanGoBack == true;

        ForwardButton.IsEnabled =
            SelectedWebView?.CanGoForward == true;
    }

    private void UpdateLoadingUI(WebView2 webView)
    {
        if (!IsDisplayedWebView(webView))
        {
            HideLoadingProgress();
            VisualStateManager.GoToState(
                RefreshButton,
                "NotLoading",
                false);
            return;
        }

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
        if (!IsDisplayedWebView(webView))
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

            tab.LastKnownUrl = args.Uri;
            tab.IsInitializing = false;

            TrackInternalPageHistory(tab, args);

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

            if (IsDisplayedWebView(webView))
                CurrentTabContent.Content = tab.Content;

            _loadingWebViews.Add(webView);
            BeginLoadingProgress(webView);

            if (IsDisplayedWebView(webView) &&
                Uri.TryCreate(
                    args.Uri,
                    UriKind.Absolute,
                    out Uri? uri))
            {
                UpdateAddressBar(uri);
            }

            if (IsDisplayedWebView(webView))
            {
                ApplyDefaultThemeVisuals();

                webView.DefaultBackgroundColor =
                    GetDefaultWebViewBackgroundColor();
            }

            if (!ReferenceEquals(tab.Content, tab.HistoryPage))
            {
                favicon.Source = null;
                favicon.Visibility = Visibility.Collapsed;

                UpdateDefaultFavicon(tab, false);

                progressRing.IsActive = true;
                progressRing.Visibility = Visibility.Visible;
            }

            UpdateLoadingState(webView, true);
        };

        webView.NavigationCompleted += async (_, args) =>
        {
            try
            {
                if (tab.IsClosed ||
                    !ReferenceEquals(tab.WebView, webView))
                {
                    return;
                }

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

                        if (tab.IsClosed ||
                            !ReferenceEquals(tab.WebView, webView))
                        {
                            return;
                        }

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

                    bool isDisplayed = IsDisplayedWebView(webView);

                    webView.Visibility = isDisplayed
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                    if (isDisplayed)
                        CurrentTabContent.Content = tab.Content;

                    if (!string.IsNullOrWhiteSpace(core.Source))
                        tab.LastKnownUrl = core.Source;

                    if (!ReferenceEquals(tab.Content, tab.HistoryPage))
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

                    if (isDisplayed)
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

                if (tab.IsClosed ||
                    !ReferenceEquals(tab.WebView, webView))
                {
                    return;
                }

                if (!IsDisplayedWebView(webView))
                {
                    await SuspendBackgroundTabAsync(tab);
                    return;
                }

                VisualStateManager.GoToState(
                    RefreshButton,
                    "NotLoading",
                    true);

                UpdateNavigationButtons();
                UpdateAddressBar(webView, force: true);
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

    private static void TrackInternalPageHistory(
        BrowserTab tab,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (tab.HistoryPage is null)
            return;

        if (args.NavigationKind == CoreWebView2NavigationKind.BackOrForward)
        {
            if (tab.PendingWebHistoryMove != 0)
            {
                tab.WebHistoryIndex = Math.Clamp(
                    tab.WebHistoryIndex + tab.PendingWebHistoryMove,
                    0,
                    tab.WebHistoryAfterInternalPage.Count - 1);

                tab.PendingWebHistoryMove = 0;
            }

            return;
        }

        if (args.NavigationKind != CoreWebView2NavigationKind.NewDocument)
            return;

        if (args.IsRedirected && tab.WebHistoryIndex >= 0)
        {
            tab.WebHistoryAfterInternalPage[tab.WebHistoryIndex] = args.Uri;
            return;
        }

        if (tab.WebHistoryIndex < tab.WebHistoryAfterInternalPage.Count - 1)
        {
            tab.WebHistoryAfterInternalPage.RemoveRange(
                tab.WebHistoryIndex + 1,
                tab.WebHistoryAfterInternalPage.Count - tab.WebHistoryIndex - 1);
        }

        tab.WebHistoryAfterInternalPage.Add(args.Uri);
        tab.WebHistoryIndex = tab.WebHistoryAfterInternalPage.Count - 1;
    }

    private bool TryNavigateBackToInternalPage()
    {
        if (SelectedTab is not BrowserTab
            {
                HistoryPage: not null,
                WebView: WebView2 webView
            } tab)
        {
            return false;
        }

        if (ReferenceEquals(tab.Content, tab.HistoryPage))
            return true;

        if (tab.WebHistoryIndex > 0 && webView.CanGoBack)
        {
            tab.PendingWebHistoryMove = -1;
            webView.GoBack();
            webView.Focus(FocusState.Programmatic);
            return true;
        }

        if (tab.WebHistoryIndex == 0)
        {
            RestoreInternalPage(tab, webView);
            return true;
        }

        return false;
    }

    private void RestoreInternalPage(BrowserTab tab, WebView2 webView)
    {
        if (tab.HistoryPage is not FrameworkElement)
            return;

        _pendingAddressBarFocusWebView = null;

        CancelSuggestionRequest();
        ClearSuggestions(AddressBar);
        _suggestionTargets.Clear();
        _suggestionTrainingIds.Clear();
        _selectedSuggestionTarget = null;
        _suggestionWasChosen = false;

        webView.Visibility = Visibility.Collapsed;
        PresentInternalPage(tab);
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
        if (ReferenceEquals(tab.Content, tab.HistoryPage))
            return;

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

        if (IsDisplayedWebView(webView))
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