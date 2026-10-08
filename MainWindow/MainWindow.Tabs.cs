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
            Title = title;
            ProgressRing = progressRing;
            AudioButton = audioButton;
            Favicon = favicon;
            WebView = webView;
        }
    }

    private sealed record ClosedTab(
        string Url,
        int Index);

    private readonly Stack<ClosedTab> _closedTabs = [];

    private void TabView_AddButtonClick(TabView sender, object args)
    {
        var tab = CreateNewTab(
            _searchSuggestionProvider.CreateHomeUri(GetSearchEngine()).AbsoluteUri);

        AddNewTab(tab, true);

        if (tab.Tag is BrowserTab browserTab &&
            browserTab.WebView is WebView2 webView)
        {
            webView.Focus(FocusState.Programmatic);
        }
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

        if (browserTab.WebView is WebView2 webView)
        {
            if (webView.Source is Uri source)
            {
                _closedTabs.Push(new ClosedTab(
                    source.AbsoluteUri,
                    MainTabView.TabItems.IndexOf(tab)));
            }

            CancelTabAudio(webView);
            _loadingWebViews.Remove(webView);
            RemoveLoadProgressTracking(webView);
            _zoomFactors.Remove(webView);
            _webViewShortcutTokens.Remove(webView);

            if (ReferenceEquals(CurrentTabContent.Content, browserTab.Content) ||
                ReferenceEquals(CurrentTabContent.Content, browserTab.ErrorPage))
            {
                CurrentTabContent.Content = null;
            }

            browserTab.WebView = null;

            _extensionTabMap.Remove(browserTab.ExtensionTabId);
            _browserTabs.Remove(tab);
            MainTabView.TabItems.Remove(tab);

            webView.Close();
            return;
        }

        if (ReferenceEquals(CurrentTabContent.Content, browserTab.Content) ||
            ReferenceEquals(CurrentTabContent.Content, browserTab.ErrorPage))
        {
            CurrentTabContent.Content = null;
        }

        if (browserTab.Content is IDisposable disposable)
            disposable.Dispose();

        _extensionTabMap.Remove(browserTab.ExtensionTabId);
        _browserTabs.Remove(tab);
        MainTabView.TabItems.Remove(tab);

        if (MainTabView.Resources["TabContextMenu"] is MenuFlyout menu &&
            menu.Items
                .OfType<MenuFlyoutItem>()
                .FirstOrDefault(item => item.Tag as string == "reopen")
                is MenuFlyoutItem reopenItem)
        {
            reopenItem.IsEnabled = _closedTabs.Count > 0;
        }
    }

    private void MainTabView_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (SelectedTab is not BrowserTab tab)
            return;

        CurrentTabContent.Content =
            tab.ErrorPage ?? tab.Content;

        if (tab.WebView is WebView2 webView)
        {
            UpdateAddressBar(webView);
            UpdateLoadingUI(webView);
            UpdateAudioButton();
            UpdateNavigationButtons();

            if (tab.ErrorPage is null &&
                webView.CoreWebView2 is { } core)
            {
                _ = UpdateThemeColorFromPageAsync(
                    webView,
                    core);
            }
            else
            {
                RestoreDefaultToolbarBackground();
            }
        }
        else
        {
            AddressBar.Text = string.Empty;
            LoadingProgressBar.Visibility = Visibility.Collapsed;
            AudioToggleButton.Opacity = 0;
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;

            RestoreDefaultToolbarBackground();
        }
    }

    private static Grid CreateWebViewContent(
        WebView2 webView,
        out Frame errorFrame)
    {
        var grid = new Grid();

        errorFrame = new Frame
        {
            Visibility = Visibility.Collapsed
        };

        grid.Children.Add(webView);
        grid.Children.Add(errorFrame);

        return grid;
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
            Opacity = 0,
            IsHitTestVisible = false
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

        title.Text = pageType.Name.EndsWith("Page", StringComparison.Ordinal)
            ? pageType.Name[..^4]
            : pageType.Name;

        progressRing.Visibility = Visibility.Collapsed;
        audioButton.Visibility = Visibility.Collapsed;
        favicon.Visibility = Visibility.Collapsed;

        var icon = new FontIcon
        {
            Glyph = "\uE713",
            FontSize = 16
        };

        if (header.Children[0] is Grid iconGrid)
            iconGrid.Children.Add(icon);

        tab.Header = header;

        if (Activator.CreateInstance(pageType) is not Page page)
            throw new InvalidOperationException(
                $"Could not create an instance of {pageType.FullName}.");

        if (page is SettingsPage settingsPage)
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

        var browserTab = new BrowserTab(
            _nextExtensionTabId++,
            tab,
            page,
            title,
            progressRing,
            audioButton,
            favicon);

        tab.Tag = browserTab;
        _browserTabs[tab] = browserTab;

        return tab;
    }

    private async Task ShowPageAsync(Type pageType)
    {
        var tab = CreatePageTab(pageType);

        AddNewTab(tab, true);

        if (tab.Tag is BrowserTab browserTab &&
            browserTab.Content is SettingsPage settingsPage)
        {
            CoreWebView2Profile profile =
                await GetBrowserProfileAsync();

            await settingsPage.LoadExtensionsAsync(profile);
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
                break;

            case "duplicate":
                if (tab.Tag is BrowserTab duplicateTab &&
                    duplicateTab.WebView?.Source is Uri uri)
                {
                    OpenNewTab(uri.AbsoluteUri);
                }
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

        var tab = CreateNewTab(closedTab.Url);

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
        OpenNewTab("https://www.google.com");

    private void DuplicateTabMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedWebView?.Source is Uri uri)
            OpenNewTab(uri.AbsoluteUri);
    }
}

