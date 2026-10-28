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
        if (TryNavigateBackToStartPage())
            return;

        if (SelectedWebView is not { CanGoBack: true } webView)
            return;

        webView.GoBack();
        webView.Focus(FocusState.Programmatic);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTab is BrowserTab tab &&
            tab.Content is StartPage &&
            tab.StartPage is not null &&
            tab.WebView is WebView2 startPageWebView &&
            tab.WebHistoryIndex >= 0 &&
            tab.WebHistoryAfterStartPage.Count > 0)
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
            historyTab.StartPage is not null)
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
        if (tab.Content is StartPage)
        {
            if (tab.WebView is WebView2)
            {
                ReplaceRetainedStartPageWebView(tab, uri);
            }
            else
            {
                ConvertInternalTabToWeb(tab, uri);
            }

            return;
        }

        if (tab.WebView is not WebView2 webView)
            return;

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
        if (SelectedTab is BrowserTab tab &&
            tab.Content is StartPage)
        {
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled =
                tab.StartPage is not null &&
                tab.WebHistoryAfterStartPage.Count > 0 &&
                tab.WebHistoryIndex >= 0;

            return;
        }

        BackButton.IsEnabled =
            (SelectedTab is BrowserTab
            {
                StartPage: not null,
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

            TrackStartPageHistory(tab, args);

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

            if (!IsStartPageDisplayed(tab))
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

                    if (tab.Content is not StartPage)
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

    private static void TrackStartPageHistory(
        BrowserTab tab,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (tab.StartPage is null)
            return;

        if (args.NavigationKind == CoreWebView2NavigationKind.BackOrForward)
        {
            if (tab.PendingWebHistoryMove != 0)
            {
                tab.WebHistoryIndex = Math.Clamp(
                    tab.WebHistoryIndex + tab.PendingWebHistoryMove,
                    0,
                    tab.WebHistoryAfterStartPage.Count - 1);

                tab.PendingWebHistoryMove = 0;
            }

            return;
        }

        if (args.NavigationKind != CoreWebView2NavigationKind.NewDocument)
            return;

        if (args.IsRedirected && tab.WebHistoryIndex >= 0)
        {
            tab.WebHistoryAfterStartPage[tab.WebHistoryIndex] = args.Uri;
            return;
        }

        if (tab.WebHistoryIndex < tab.WebHistoryAfterStartPage.Count - 1)
        {
            tab.WebHistoryAfterStartPage.RemoveRange(
                tab.WebHistoryIndex + 1,
                tab.WebHistoryAfterStartPage.Count - tab.WebHistoryIndex - 1);
        }

        tab.WebHistoryAfterStartPage.Add(args.Uri);
        tab.WebHistoryIndex = tab.WebHistoryAfterStartPage.Count - 1;
    }

    private bool TryNavigateBackToStartPage()
    {
        if (SelectedTab is not BrowserTab
            {
                StartPage: not null,
                WebView: WebView2 webView
            } tab)
        {
            return false;
        }

        if (SelectedTab is BrowserTab currentTab &&
            currentTab.Content is StartPage)
        {
            return true;
        }

        if (tab.WebHistoryIndex > 0 && webView.CanGoBack)
        {
            tab.PendingWebHistoryMove = -1;
            webView.GoBack();
            webView.Focus(FocusState.Programmatic);
            return true;
        }

        if (tab.WebHistoryIndex == 0)
        {
            RestoreStartPage(tab, webView);
            return true;
        }

        return false;
    }

    private void RestoreStartPage(BrowserTab tab, WebView2 webView)
    {
        FrameworkElement startPage = tab.StartPage
            ?? throw new InvalidOperationException(
                "The tab has no start page to restore.");

        _pendingAddressBarFocusWebView = null;

        CancelSuggestionRequest();
        ClearSuggestions(AddressBar);

        _suggestionTargets.Clear();
        _suggestionTrainingIds.Clear();
        _selectedSuggestionTarget = null;
        _suggestionWasChosen = false;

        webView.Visibility = Visibility.Collapsed;

        tab.Content = startPage;
        tab.ErrorPage = null;
        tab.PendingWebHistoryMove = 0;

        tab.Title.Text =
            LocalizerExtensions.GetLocalizedString("StartTitle");

        tab.ProgressRing.IsActive = false;
        tab.ProgressRing.Visibility = Visibility.Collapsed;
        tab.AudioButton.Visibility = Visibility.Collapsed;
        tab.Favicon.Source = null;
        tab.Favicon.Visibility = Visibility.Collapsed;

        if (tab.TabItem.Header is Grid header &&
            header.Children.Count > 0 &&
            header.Children[0] is Grid iconGrid)
        {
            FontIcon? pageIcon =
                iconGrid.Children.OfType<FontIcon>().FirstOrDefault();

            if (pageIcon is null)
            {
                pageIcon = new FontIcon
                {
                    FontSize = 16
                };

                iconGrid.Children.Add(pageIcon);
            }

            pageIcon.Glyph = "\uE734";
            pageIcon.Visibility = Visibility.Visible;
        }

        CurrentTabContent.Content = startPage;
        ClearAddressBarForStartPage();
        LoadingProgressBar.Visibility = Visibility.Collapsed;

        SetAddressBarTrailingButtonVisible(AudioToggleButton, false);

        UpdateNavigationButtons();
        UpdateZoomControls(100);
        RestoreDefaultToolbarBackground();
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
        if (IsStartPageDisplayed(tab))
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
