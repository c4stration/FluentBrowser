using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using Windows.Storage;
using WinRT.Interop;
using WinUI3Localizer;

namespace FluentBrowser;

public sealed partial class MainWindow : Window
{
    private readonly ApplicationDataContainer _settings;
    private readonly AppWindow _appWindow;

    private readonly HashSet<WebView2> _loadingWebViews = new();
    private readonly Dictionary<TabViewItem, BrowserTab> _browserTabs = new();
    private readonly Dictionary<WebView2, CoreWebView2Certificate>
        _serverCertificates = [];

    private readonly string _faviconCacheDirectory = Path.Combine(
        ApplicationData.Current.LocalFolder.Path,
        "Favicons");

    private BrowserTab? SelectedTab =>
        MainTabView.SelectedItem is TabViewItem tab &&
        _browserTabs.TryGetValue(tab, out var browserTab)
            ? browserTab
            : null;

    private WebView2? SelectedWebView =>
        SelectedTab?.WebView;

    public MainWindow()
    {
        InitializeComponent();

        _settings = ApplicationData.Current.LocalSettings;
        _searchSuggestionProvider = new SearchSuggestionProvider(_httpClient);

        _ = LoadDownloadHistoryAsync();

        ApplyBackdropMaterial(
            _settings.Values["BackdropMaterial"] as string ?? "Mica");

        ApplyTabWidth(
            _settings.Values["TabWidth"] as string ?? "Equal");

        RegisterKeyboardAccelerators();
        InitializeKeyboardHandling();

        var themeRoot =
            Content as FrameworkElement
            ?? throw new InvalidOperationException(
                "MainWindow.Content must be a FrameworkElement.");

        App.RegisterThemeRoot(themeRoot, this);

        themeRoot.ActualThemeChanged += (_, _) =>
        {
            _ = ApplyThemeColorTintAsync(
                _settings.Values["ThemeColorTint"] as bool? ?? true);
        };

        Localizer.Get().LanguageChanged +=
            OnLanguageChanged;

        BrowserChrome.SizeChanged += (_, _) =>
            UpdateFullscreenContentLayout();

        UpdateFullscreenContentLayout();

        MainTabView.Resources["TabViewItemHeaderBackgroundSelected"] =
            _tabViewSelectedBackgroundBrush;

        AudioToggleButton.PointerPressed += AudioToggleButton_PointerPressed;
        AudioToggleButton.PointerReleased += AudioToggleButton_PointerReleased;
        AudioToggleButton.PointerCanceled += AudioToggleButton_PointerCanceled;
        AudioToggleButton.PointerCaptureLost += AudioToggleButton_PointerCaptureLost;

        FindOnPageBox.RegisterPropertyChangedCallback(
            AutoSuggestBox.TextProperty,
            FindOnPageBox_TextPropertyChanged);

        FindOnPageBox.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(FindOnPageBox_KeyDownHandled),
            true);

        Directory.CreateDirectory(_faviconCacheDirectory);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);

        _appWindow = AppWindow.GetFromWindowId(windowId);
        InitializeFullscreenPointerTracking();

        Closed += MainWindow_Closed;

        InitializeStartup();
    }

    public nint GetWindowHandle()
    {
        return WindowNative.GetWindowHandle(this);
    }

    private void OnLanguageChanged(
        object? sender,
        LanguageChangedEventArgs e)
    {
        foreach (DownloadItem item in Downloads)
            item.UpdateLocalizedStatus();
    }

    private void trainML(object sender, RoutedEventArgs e)
    {
        _ = SuggestionModelTrainer.TrainInBackgroundAsync();
    }

    private void ToolbarButtons_ItemClick(
        object sender,
        ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ListViewItem item ||
            item.Content is not Button button ||
            button.Flyout is null)
        {
            return;
        }

        button.Flyout.ShowAt(button);
    }

    private void test(
        object sender,
        RoutedEventArgs e)
    {
        CustomizeToolbarTeachingTip.IsOpen = !CustomizeToolbarTeachingTip.IsOpen;
    }
}
