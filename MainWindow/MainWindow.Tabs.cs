using FluentBrowser.Pages;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using WinUI3Localizer;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private sealed class BrowserTab
    {
        public TabViewItem TabItem { get; }
        public FrameworkElement Content { get; set; }
        public WebView2? WebView { get; set; }
        public Page? ErrorPage { get; set; }

        public TextBlock Title { get; }
        public ProgressRing ProgressRing { get; }
        public Button AudioButton { get; }
        public Image Favicon { get; }
        public bool IsClosed { get; set; }
        public bool IsInitializing { get; set; } = true;
        public bool WasDiscarded { get; set; }
        public DateTime LastDeactivatedUtc { get; set; } = DateTime.UtcNow;
        public string LastKnownUrl { get; set; } = string.Empty;
        public string? AddressBarDraft { get; set; }
        public bool AddressBarFocused { get; set; }
        public int AddressBarSelectionStart { get; set; }
        public int AddressBarSelectionLength { get; set; }
        public bool AddressBarSelectionSaved { get; set; }
        public FrameworkElement? HistoryPage { get; set; }
        public string? HistoryPageUri { get; set; }

        public Dictionary<string, FrameworkElement> InternalPages { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> WebHistoryAfterInternalPage { get; } = [];
        public int WebHistoryIndex { get; set; } = -1;
        public int PendingWebHistoryMove { get; set; }
        public string? PendingReloadHistoryEntryUri { get; set; }
        public string? PendingHistoryRestoreUri { get; set; }
        public int ExtensionTabId { get; }

        public BrowserTab(
            int extensionTabId,
            TabViewItem tabItem,
            FrameworkElement content,
            TextBlock title,
            ProgressRing progressRing,
            Button audioButton,
            Image favicon,
            WebView2? webView = null)
        {
            ExtensionTabId = extensionTabId;
            TabItem = tabItem;
            Content = content;
            AddressBarFocused = content is StartPage;
            Title = title;
            ProgressRing = progressRing;
            AudioButton = audioButton;
            Favicon = favicon;
            WebView = webView;
        }
    }

    private abstract record TabNavigationSnapshot;

    private sealed record WebTabSnapshot(string Url) : TabNavigationSnapshot;

    private sealed record InternalHistoryTabSnapshot(
        string[] Entries,
        int CurrentIndex,
        bool ShowsInternalPage) : TabNavigationSnapshot;

    private sealed record ClosedTab(
        TabNavigationSnapshot Navigation,
        int Index);

    private bool IsDisplayedWebView(WebView2 webView) =>
        SelectedTab is BrowserTab tab &&
        ReferenceEquals(tab.WebView, webView) &&
        ReferenceEquals(tab.Content, webView);

    private static bool IsInternalPageDisplayed(BrowserTab tab) =>
        tab.HistoryPage is not null &&
        ReferenceEquals(tab.Content, tab.HistoryPage);

    private const int MaxClosedTabs = 25;

    private readonly Stack<ClosedTab> _closedTabs = [];

    private void PushClosedTab(ClosedTab closedTab)
    {
        _closedTabs.Push(closedTab);

        if (_closedTabs.Count <= MaxClosedTabs)
            return;

        // Stack enumerates newest-first; keep the newest entries only.
        ClosedTab[] keep = [.. _closedTabs.Take(MaxClosedTabs).Reverse()];

        _closedTabs.Clear();

        foreach (ClosedTab entry in keep)
            _closedTabs.Push(entry);
    }

    private void TabView_AddButtonClick(TabView sender, object args)
    {
        var tab = CreatePageTab(typeof(StartPage));

        AddNewTab(tab, true);
    }

    private void TabView_TabCloseRequested(
        TabView sender,
        TabViewTabCloseRequestedEventArgs args) =>
        CloseTab(args.Tab);

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is not BrowserTab browserTab)
        {
            Debug.WriteLine("Closing an untracked tab.");
            MainTabView.TabItems.Remove(tab);
            return;
        }

        Debug.WriteLine(
            $"closing host tab {browserTab.ExtensionTabId}; " +
            $"has web view: {browserTab.WebView is not null}.");

        browserTab.IsClosed = true;

        if (ReferenceEquals(_addressBarFocusTab, browserTab))
            _addressBarFocusTab = null;

        if (CaptureNavigationSnapshot(browserTab) is { } snapshot)
        {
            PushClosedTab(new ClosedTab(
                snapshot,
                MainTabView.TabItems.IndexOf(tab)));
        }

        if (ReferenceEquals(CurrentTabContent.Content, browserTab.Content) ||
            ReferenceEquals(CurrentTabContent.Content, browserTab.ErrorPage))
        {
            CurrentTabContent.Content = null;
        }

        bool hadWebView = browserTab.WebView is WebView2;

        if (browserTab.WebView is WebView2 webView)
        {
            CleanupWebViewTracking(webView);

            browserTab.WebView = null;
            webView.Tag = null;
            webView.Close();
        }

        foreach (IDisposable page in browserTab.InternalPages.Values
             .OfType<IDisposable>()
             .Distinct())
        {
            page.Dispose();
        }

        if (browserTab.InternalPages.Count == 0 &&
            !hadWebView &&
            browserTab.Content is IDisposable disposable &&
            !ReferenceEquals(browserTab.Content, browserTab.HistoryPage))
        {
            disposable.Dispose();
        }

        _extensionTabMap.Remove(browserTab.ExtensionTabId);
        _browserTabs.Remove(tab);
        MainTabView.TabItems.Remove(tab);

        browserTab.Content = new Grid();
        browserTab.HistoryPage = null;
        browserTab.HistoryPageUri = null;
        browserTab.ErrorPage = null;
        browserTab.InternalPages.Clear();
        browserTab.WebHistoryAfterInternalPage.Clear();
        tab.Tag = null;
        tab.Header = null;

        if (MainTabView.Resources["TabContextMenu"] is MenuFlyout menu &&
            menu.Items
                .OfType<MenuFlyoutItem>()
                .FirstOrDefault(item => item.Tag as string == "reopen")
                is MenuFlyoutItem reopenItem)
        {
            reopenItem.IsEnabled = _closedTabs.Count > 0;
        }

        if (MainTabView.TabItems.Count == 0)
        {
            Close();
        }
    }

    private void MainTabView_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_addressBarFocusTab is BrowserTab previousFocusTab &&
            !ReferenceEquals(previousFocusTab, SelectedTab))
        {
            SaveAddressBarSelection(previousFocusTab);
        }

        int selectionVersion = ++_addressBarTabSelectionVersion;

        ++_addressBarFocusVersion;

        _addressBarBlurCancellation?.Cancel();
        _addressBarBlurCancellation = null;

        _addressBarSuppressLostFocus = true;

        _addressBarUserEditing = false;

        foreach (TabViewItem removedTab in e.RemovedItems.OfType<TabViewItem>())
        {
            if (removedTab.Tag is not BrowserTab previousTab ||
                ReferenceEquals(SelectedTab, previousTab))
            {
                continue;
            }

            previousTab.LastDeactivatedUtc = DateTime.UtcNow;

            if (previousTab.WebView is not WebView2 previousWebView)
                continue;

            previousWebView.Visibility = Visibility.Collapsed;
            _ = SuspendBackgroundTabAsync(previousTab);
        }

        if (SelectedTab is not BrowserTab tab)
        {
            QueueAddressBarTabSwitchCompletion(selectionVersion);
            return;
        }

        if (tab.WasDiscarded)
            RestoreDiscardedTab(tab);

        bool restoreAddressBarFocus = tab.AddressBarFocused;
        bool restoreAddressBarDraft =
            restoreAddressBarFocus && tab.AddressBarDraft is not null;

        _addressBarUserEditing = restoreAddressBarDraft;

        if (restoreAddressBarDraft)
            SetAddressBarTextWithoutSavingSelection(tab.AddressBarDraft!);

        if (tab.WebView is WebView2 webView)
        {
            if (webView.CoreWebView2 is { } resumeCore)
            {
                if (resumeCore.IsSuspended)
                    resumeCore.Resume();

                SetTabMemoryTarget(resumeCore, low: false);
            }

            webView.Visibility = ReferenceEquals(tab.Content, tab.HistoryPage)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        CurrentTabContent.Content = tab.ErrorPage ?? tab.Content;

        MoveFocusAwayFromAddressBar(tab);

        if (ZoomTeachingTip.IsOpen)
            ZoomTeachingTip.IsOpen = false;

        if (tab.WebView is WebView2 selectedWebView)
        {
            if (ReferenceEquals(tab.Content, tab.HistoryPage) &&
                tab.HistoryPage is not null)
            {
                if (!restoreAddressBarDraft)
                {
                    if (tab.Content is StartPage)
                        ClearAddressBarForStartPage();
                    else if (tab.HistoryPageUri is string internalUri &&
                             Uri.TryCreate(internalUri, UriKind.Absolute, out Uri? uri))
                        UpdateAddressBar(uri, force: true);
                }

                LoadingProgressBar.Visibility = Visibility.Collapsed;
                SetAddressBarTrailingButtonVisible(AudioToggleButton, false);

                UpdateNavigationButtons();
                UpdateZoomControls(100);
                RestoreDefaultToolbarBackground();
            }
            else
            {
                if (!restoreAddressBarDraft)
                    UpdateAddressBar(selectedWebView, force: true);

                UpdateLoadingUI(selectedWebView);
                UpdateAudioButton();
                UpdateNavigationButtons();
                UpdateZoomControls(GetZoomPercentage(selectedWebView));

                if (tab.ErrorPage is null &&
                    selectedWebView.CoreWebView2 is { } core)
                {
                    _ = UpdateThemeColorFromPageAsync(
                        selectedWebView,
                        core);
                }
                else
                {
                    RestoreDefaultToolbarBackground();
                }

                if (tab.ErrorPage is null && !restoreAddressBarFocus)
                {
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ReferenceEquals(SelectedTab, tab) &&
                            ReferenceEquals(tab.WebView, selectedWebView) &&
                            tab.ErrorPage is null &&
                            !ReferenceEquals(tab.Content, tab.HistoryPage))
                        {
                            selectedWebView.Focus(FocusState.Programmatic);
                        }
                    });
                }
            }
        }
        else
        {
            UpdateZoomControls(100);

            if (restoreAddressBarDraft)
            {
                UpdateNavigationButtons();
            }
            else if (ReferenceEquals(tab.Content, tab.HistoryPage) &&
                     tab.HistoryPage is not null &&
                     tab.HistoryPage is not StartPage &&
                     tab.HistoryPageUri is string internalUri &&
                     Uri.TryCreate(internalUri, UriKind.Absolute, out Uri? uri))
            {
                UpdateAddressBar(uri, force: true);
                UpdateNavigationButtons();
            }
            else
            {
                ClearAddressBarForStartPage();
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
            }

            LoadingProgressBar.Visibility = Visibility.Collapsed;
            SetAddressBarTrailingButtonVisible(AudioToggleButton, false);
            RestoreDefaultToolbarBackground();
        }

        if (restoreAddressBarFocus)
        {
            ++_addressBarAnimationVersion;

            _addressBarUserEditing = restoreAddressBarDraft;
            _addressBarFocused = true;
            _addressBarAnimating = false;

            QueueAddressBarFocusRestore(tab, selectionVersion);
        }
        else
        {
            _addressBarFocusTab = null;
            _addressBarUserEditing = false;
            AnimateAddressBar(false);
            QueueAddressBarTabSwitchCompletion(selectionVersion);
        }
    }

    private void MoveFocusAwayFromAddressBar(BrowserTab tab)
    {
        if (!IsAddressBarTextBoxFocused())
            return;

        bool movedFocus =
            tab.ErrorPage is null &&
            tab.WebView is WebView2 webView &&
            ReferenceEquals(tab.Content, webView) &&
            webView.Focus(FocusState.Programmatic);

        if (!movedFocus &&
            !MainTabView.Focus(FocusState.Programmatic))
        {
            tab.TabItem.Focus(FocusState.Programmatic);
        }
    }

    private void QueueAddressBarFocusRestore(
        BrowserTab tab,
        int selectionVersion)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (selectionVersion != _addressBarTabSelectionVersion)
                return;

            if (!ReferenceEquals(SelectedTab, tab) ||
                !tab.AddressBarFocused)
            {
                _addressBarSuppressLostFocus = false;
                return;
            }

            bool hadSavedSelection = tab.AddressBarSelectionSaved;
            int savedStart = tab.AddressBarSelectionStart;
            int savedLength = tab.AddressBarSelectionLength;
            string? draftAtRestore = tab.AddressBarDraft;

            void ApplySavedSelection()
            {
                tab.AddressBarSelectionStart = savedStart;
                tab.AddressBarSelectionLength = savedLength;
                tab.AddressBarSelectionSaved = hadSavedSelection;

                RestoreAddressBarSelection(
                    tab,
                    tab.AddressBarDraft?.Length ?? 0,
                    tab.Content is StartPage ? 0 : AddressBar.Text.Length);
            }

            BeginAddressBarProgrammaticUpdate();

            SetAddressBarTextWithoutSavingSelection(
                GetAddressBarFocusedText(tab));

            AddressBar.Focus(FocusState.Programmatic);
            _addressBarFocusTab = tab;
            tab.AddressBarFocused = true;
            _addressBarUserEditing = tab.AddressBarDraft is not null;

            FindAddressBarTextHost();
            ResetAddressBarTextTranslation(true);

            ApplySavedSelection();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (selectionVersion != _addressBarTabSelectionVersion)
                    return;

                if (ReferenceEquals(SelectedTab, tab) &&
                    tab.AddressBarFocused &&
                    IsAddressBarTextBoxFocused() &&
                    ReferenceEquals(tab.AddressBarDraft, draftAtRestore))
                {
                    BeginAddressBarProgrammaticUpdate();
                    ApplySavedSelection();
                }

                _addressBarSuppressLostFocus = false;
            });
        });
    }

    private void QueueAddressBarTabSwitchCompletion(int selectionVersion)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (selectionVersion == _addressBarTabSelectionVersion)
                _addressBarSuppressLostFocus = false;
        });
    }

    private TabViewItem CreateNewTab(
        string url,
        bool initializeNavigation = true)
    {
        var tab = new TabViewItem
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var header = CreateTabHeader(
            out var title,
            out var progressRing,
            out var audioButton,
            out var favicon);

        tab.Header = header;

        var webView = new WebView2();

        int extensionTabId = _nextExtensionTabId++;

        var browserTab = new BrowserTab(
            extensionTabId,
            tab,
            webView,
            title,
            progressRing,
            audioButton,
            favicon,
            webView);

        tab.Tag = browserTab;
        browserTab.LastKnownUrl = url;

        _browserTabs[tab] = browserTab;
        _extensionTabMap[extensionTabId] = browserTab;

        webView.Tag = browserTab;

        ConfigureTabAudioButton(webView, audioButton);

        ConfigureWebView(
            browserTab,
            webView,
            title,
            progressRing,
            audioButton,
            favicon,
            url,
            initializeNavigation);

        return tab;
    }

    private TabViewItem CreateDiscardedTab(string url, string? title = null)
    {
        var tab = new TabViewItem
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var header = CreateTabHeader(
            out var titleBlock,
            out var progressRing,
            out var audioButton,
            out var favicon);

        titleBlock.Text = !string.IsNullOrWhiteSpace(title)
            ? title
            : Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                ? uri.Host
                : url;

        progressRing.IsActive = false;
        progressRing.Visibility = Visibility.Collapsed;
        audioButton.Visibility = Visibility.Collapsed;

        tab.Header = header;

        var browserTab = new BrowserTab(
            _nextExtensionTabId++,
            tab,
            new Grid(),
            titleBlock,
            progressRing,
            audioButton,
            favicon)
        {
            LastKnownUrl = url,
            WasDiscarded = true,
            IsInitializing = false
        };

        tab.Tag = browserTab;

        _browserTabs[tab] = browserTab;
        _extensionTabMap[browserTab.ExtensionTabId] = browserTab;

        return tab;
    }

    private void RestoreDiscardedTab(BrowserTab tab)
    {
        if (!tab.WasDiscarded ||
            string.IsNullOrWhiteSpace(tab.LastKnownUrl))
        {
            return;
        }

        string url = tab.LastKnownUrl;
        var webView = new WebView2();

        if (tab.HistoryPage is not null)
        {
            tab.PendingHistoryRestoreUri = url;
            tab.PendingWebHistoryMove = 0;
            tab.PendingReloadHistoryEntryUri = null;
        }

        tab.Content = webView;
        tab.WebView = webView;
        tab.ErrorPage = null;
        tab.WasDiscarded = false;
        tab.IsInitializing = true;

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
            url);

        Debug.WriteLine(
            $"restoring discarded tab {tab.ExtensionTabId}: {url}");
    }

    private void AddNewTab(
        TabViewItem tab,
        bool autoSelect,
        bool forceAfterCurrent = false)
    {
        string position =
            ApplicationData.Current.LocalSettings.Values["NewTabPosition"] as string
            ?? "AfterCurrent";

        if ((forceAfterCurrent || position == "AfterCurrent") &&
            MainTabView.SelectedItem is TabViewItem currentTab)
        {
            int index = MainTabView.TabItems.IndexOf(currentTab);

            if (index >= 0)
            {
                MainTabView.TabItems.Insert(index + 1, tab);
            }
            else
            {
                MainTabView.TabItems.Add(tab);
            }
        }
        else
        {
            MainTabView.TabItems.Add(tab);
        }

        if (autoSelect)
            MainTabView.SelectedItem = tab;
    }

    private void AddNewTabAtIndex(
        TabViewItem tab,
        bool autoSelect,
        int index)
    {
        index = Math.Clamp(index, 0, MainTabView.TabItems.Count);

        MainTabView.TabItems.Insert(index, tab);

        if (autoSelect)
            MainTabView.SelectedItem = tab;
    }

    private static Grid CreateTabHeader(
        out TextBlock title,
        out ProgressRing progressRing,
        out Button audioButton,
        out Image favicon)
    {
        var grid = new Grid();

        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(16)
        });

        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(8)
        });

        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var iconGrid = new Grid
        {
            Width = 16,
            Height = 16
        };

        favicon = new Image
        {
            Width = 16,
            Height = 16,
            Visibility = Visibility.Collapsed
        };

        progressRing = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = true
        };

        iconGrid.Children.Add(favicon);
        iconGrid.Children.Add(progressRing);

        title = new TextBlock
        {
            Text = "New Tab",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        audioButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.Resources["TabButtonStyle"],
            Content = new FontIcon
            {
                Glyph = "\uE767",
                FontSize = 12
            },
            IsHitTestVisible = true
        };

        Grid.SetColumn(iconGrid, 0);
        Grid.SetColumn(title, 2);
        Grid.SetColumn(audioButton, 3);

        grid.Children.Add(iconGrid);
        grid.Children.Add(title);
        grid.Children.Add(audioButton);

        return grid;
    }

    private TabViewItem CreatePageTab(Type pageType)
    {
        if (!typeof(Page).IsAssignableFrom(pageType) ||
            pageType.IsAbstract)
        {
            throw new ArgumentException(
                "The specified type must be a non-abstract Page.",
                nameof(pageType));
        }

        var tab = new TabViewItem
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        var header = CreateTabHeader(
            out var title,
            out var progressRing,
            out var audioButton,
            out var favicon);

        title.Text = pageType switch
        {
            var t when t == typeof(SettingsPage) => LocalizerExtensions.GetLocalizedString("SettingsTitle"),
            var t when t == typeof(StartPage) => LocalizerExtensions.GetLocalizedString("StartTitle"),
            _ => pageType.Name.EndsWith("Page", StringComparison.Ordinal)
                ? pageType.Name[..^4]
                : pageType.Name
        };

        progressRing.Visibility = Visibility.Collapsed;
        audioButton.Visibility = Visibility.Collapsed;
        favicon.Visibility = Visibility.Collapsed;

        var icon = new FontIcon
        {
            Glyph = pageType switch
            {
                var t when t == typeof(StartPage) => "\uE734",
                var t when t == typeof(PageSelector) => "\uF196",
                _ => "\uE713" // settings
            },
            FontSize = 16
        };

        if (header.Children[0] is Grid iconGrid)
            iconGrid.Children.Add(icon);

        tab.Header = header;

        Page page = CreatePageInstance(pageType);

        var browserTab = new BrowserTab(
            _nextExtensionTabId++,
            tab,
            page,
            title,
            progressRing,
            audioButton,
            favicon);

        string? internalUri = GetInternalPageUri(page);

        if ((page is StartPage or SettingsPage) &&
            internalUri is not null)
        {
            browserTab.HistoryPage = page;
            browserTab.HistoryPageUri = internalUri;
            browserTab.InternalPages[internalUri] = page;
            browserTab.WebHistoryAfterInternalPage.Add(internalUri);
            browserTab.WebHistoryIndex = 0;
        }

        tab.Tag = browserTab;
        _browserTabs[tab] = browserTab;

        return tab;
    }

    private static TabNavigationSnapshot? CaptureNavigationSnapshot(
        BrowserTab tab)
    {
        if (tab.HistoryPage is not null &&
            tab.WebHistoryAfterInternalPage.Count > 0 &&
            tab.WebHistoryAfterInternalPage.Any(IsInternalHistoryEntry))
        {
            return new InternalHistoryTabSnapshot(
                [.. tab.WebHistoryAfterInternalPage],
                Math.Clamp(
                    tab.WebHistoryIndex,
                    0,
                    tab.WebHistoryAfterInternalPage.Count - 1),
                IsInternalPageDisplayed(tab));
        }

        string url = tab.LastKnownUrl;

        if (string.IsNullOrWhiteSpace(url) &&
            tab.WebView?.Source is Uri source)
        {
            url = source.AbsoluteUri;
        }

        return string.IsNullOrWhiteSpace(url)
            ? null
            : new WebTabSnapshot(url);
    }

    private static bool IsInternalHistoryEntry(string entry) =>
        Uri.TryCreate(entry, UriKind.Absolute, out Uri? uri) &&
        TryGetInternalPageType(uri, out _);

    private TabViewItem CreateTabFromSnapshot(
        TabNavigationSnapshot snapshot)
    {
        if (snapshot is WebTabSnapshot webTab)
            return CreateNewTab(webTab.Url);

        var historyTab = (InternalHistoryTabSnapshot)snapshot;

        if (!TryGetRetainedInternalPageType(historyTab, out Type? pageType) ||
            pageType is null)
        {
            string? currentUrl = historyTab.Entries
                .ElementAtOrDefault(historyTab.CurrentIndex);

            return !string.IsNullOrWhiteSpace(currentUrl)
                ? CreateNewTab(currentUrl)
                : CreatePageTab(typeof(StartPage));
        }

        TabViewItem tab = CreatePageTab(pageType);
        BrowserTab browserTab = (BrowserTab)tab.Tag;

        browserTab.WebHistoryAfterInternalPage.Clear();
        browserTab.WebHistoryAfterInternalPage.AddRange(historyTab.Entries);
        browserTab.WebHistoryIndex = Math.Clamp(
            historyTab.CurrentIndex,
            0,
            historyTab.Entries.Length - 1);

        string currentEntry =
            browserTab.WebHistoryAfterInternalPage[browserTab.WebHistoryIndex];

        if (!historyTab.ShowsInternalPage &&
            !IsInternalHistoryEntry(currentEntry))
        {
            CreateWebViewForHistoryEntry(
                browserTab,
                browserTab.WebHistoryIndex,
                currentEntry);
        }
        else if (IsInternalHistoryEntry(currentEntry))
        {
            ShowInternalHistoryEntry(browserTab, currentEntry);
        }

        if (browserTab.HistoryPage is SettingsPage settingsPage)
            _ = LoadSettingsPageExtensionsAsync(settingsPage);

        return tab;
    }

    private static bool TryGetRetainedInternalPageType(
        InternalHistoryTabSnapshot snapshot,
        out Type? pageType)
    {
        pageType = null;

        IEnumerable<string> candidates = snapshot.Entries
            .Take(snapshot.CurrentIndex + 1)
            .Reverse()
            .Concat(snapshot.Entries);

        foreach (string entry in candidates)
        {
            if (Uri.TryCreate(entry, UriKind.Absolute, out Uri? uri) &&
                TryGetInternalPageType(uri, out pageType))
            {
                return true;
            }
        }

        return false;
    }

    private Page CreatePageInstance(Type pageType)
    {
        Page? page;

        if (pageType == typeof(StartPage))
        {
            var startPage = new StartPage(Favorites);
            startPage.FavoriteSelected += StartPage_FavoriteSelected;
            page = startPage;
        }
        else
        {
            page = Activator.CreateInstance(pageType) as Page;
        }

        if (page is null)
        {
            throw new InvalidOperationException(
                $"Could not create an instance of {pageType.FullName}.");
        }

        if (page is SettingsPage settingsPage)
            ConfigureSettingsPage(settingsPage);

        return page;
    }

    private void StartPage_FavoriteSelected(FavoriteWebsite favorite)
    {
        if (SelectedTab is not BrowserTab tab ||
            !Uri.TryCreate(favorite.Url, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        Navigate(tab, uri);
    }

    private void ConfigureSettingsPage(SettingsPage settingsPage)
    {
        settingsPage.FullWebAddressChanged += (_, _) =>
        {
            if (SelectedWebView is WebView2 webView)
                UpdateAddressBar(webView);
        };

        settingsPage.SearchSuggestionsChanged += (_, _) =>
        {
            if (!AreSearchSuggestionsEnabled())
            {
                CancelSuggestionRequest();
                ClearSuggestions(AddressBar);
                _suggestionTargets.Clear();
                _suggestionTrainingIds.Clear();
                _selectedSuggestionTarget = null;
                _suggestionWasChosen = false;
            }
        };

        settingsPage.SearchEngineChanged += (_, _) =>
        {
            CancelSuggestionRequest();
            ClearSuggestions(AddressBar);
            _suggestionTargets.Clear();
            _suggestionTrainingIds.Clear();
            _selectedSuggestionTarget = null;
            _suggestionWasChosen = false;
        };
    }


    private async Task LoadSettingsPageExtensionsAsync(SettingsPage settingsPage)
    {
        if (settingsPage.IsDisposed)
            return;

        try
        {
            CoreWebView2Profile profile = await GetBrowserProfileAsync();

            if (settingsPage.IsDisposed)
                return;

            await settingsPage.LoadExtensionsAsync(profile);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load Settings page extensions: {ex}");
        }
    }


    private async Task ShowPageAsync(Type pageType)
    {
        var tab = CreatePageTab(pageType);

        AddNewTab(tab, true);

        if (tab.Tag is BrowserTab browserTab &&
            browserTab.Content is SettingsPage settingsPage)
        {
            await LoadSettingsPageExtensionsAsync(settingsPage);
        }
    }

    private async void SettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ShowPageAsync(typeof(SettingsPage));
    }

    private async void ShowPageSelector_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ShowPageAsync(typeof(PageSelector));
    }

    private void TabContextMenu_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item ||
            item.Tag is not string action ||
            MainTabView.Resources["TabContextMenu"] is not MenuFlyout menu)
        {
            return;
        }

        if (action == "reopen")
        {
            ReopenClosedTab();
            return;
        }

        if (menu.Target is not TabViewItem tab)
            return;

        switch (action)
        {
            case "refresh":
                if (tab.Tag is BrowserTab browserTab &&
                    browserTab.WebView is WebView2 webView)
                {
                    webView.Reload();
                }
                else if (tab.Tag is BrowserTab { WasDiscarded: true })
                {
                    MainTabView.SelectedItem = tab;
                }
                break;

            case "hibernate":
                if (tab.Tag is BrowserTab hibernateTab)
                    DiscardTab(hibernateTab, "tab menu");
                break;

            case "hibernateOthers":
                if (tab.Tag is BrowserTab keepTab)
                    DiscardAllExcept(keepTab, "tab menu");
                break;

            case "duplicate":
                if (tab.Tag is BrowserTab duplicateTab)
                    DuplicateTab(duplicateTab);
                break;

            case "close":
                CloseTab(tab);
                break;

            case "closeOther":
                CloseOtherTabs(tab);
                break;

            case "closeRight":
                CloseTabsToRight(tab);
                break;
        }
    }

    private void TabContextMenu_Opening(
        object sender,
        object e)
    {
        if (sender is not MenuFlyout menu)
            return;

        MenuFlyoutItem? reopenItem = menu.Items
            .OfType<MenuFlyoutItem>()
            .FirstOrDefault(item => item.Tag as string == "reopen");

        if (reopenItem is not null)
            reopenItem.IsEnabled = _closedTabs.Count > 0;
    }

    private void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0)
            return;

        ClosedTab closedTab = _closedTabs.Pop();

        var tab = CreateTabFromSnapshot(closedTab.Navigation);

        int index = Math.Clamp(
            closedTab.Index,
            0,
            MainTabView.TabItems.Count);

        MainTabView.TabItems.Insert(index, tab);
        MainTabView.SelectedItem = tab;

        if (MainTabView.Resources["TabContextMenu"] is MenuFlyout menu &&
            menu.Items
                .OfType<MenuFlyoutItem>()
                .FirstOrDefault(item => item.Tag as string == "reopen")
                is MenuFlyoutItem reopenItem)
        {
            reopenItem.IsEnabled = _closedTabs.Count > 0;
        }
    }

    private void CloseOtherTabs(TabViewItem selectedTab)
    {
        MainTabView.SelectedItem = selectedTab;

        foreach (var tab in MainTabView.TabItems
                     .OfType<TabViewItem>()
                     .Where(tab => !ReferenceEquals(tab, selectedTab))
                     .ToList())
        {
            CloseTab(tab);
        }

        MainTabView.SelectedItem = selectedTab;
    }

    private void CloseTabsToRight(TabViewItem selectedTab)
    {
        int index = MainTabView.TabItems.IndexOf(selectedTab);

        if (index < 0)
            return;

        MainTabView.SelectedItem = selectedTab;

        foreach (var tab in MainTabView.TabItems
                     .OfType<TabViewItem>()
                     .Skip(index + 1)
                     .ToList())
        {
            CloseTab(tab);
        }

        MainTabView.SelectedItem = selectedTab;
    }

    private void NewTabMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        AddNewTab(CreatePageTab(typeof(StartPage)), true);

    private void DuplicateTabMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedTab is BrowserTab tab)
            DuplicateTab(tab);
    }

    private void DuplicateTab(BrowserTab source)
    {
        if (CaptureNavigationSnapshot(source) is not { } snapshot)
            return;

        AddNewTab(CreateTabFromSnapshot(snapshot), true);
    }
}