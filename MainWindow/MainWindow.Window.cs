using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

using System;
using System.Runtime.InteropServices;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private bool _browserFullscreen;
    private bool _webContentFullscreen;
    private bool _browserChromeIsShown = true;
    private bool _isRestoringNormalContentLayout;
    private DateTime? _fullscreenRevealStartedAt;
    private TranslateTransform? _browserChromeTransform;
    private DispatcherTimer? _fullscreenPointerTimer;
    private DispatcherTimer? _normalLayoutRestoreTimer;
    private int _browserChromeAnimationVersion;

    private bool IsChromeAutoHideFullscreen =>
        _browserFullscreen || _webContentFullscreen;

    public void ApplyBackdropMaterial(string material)
    {
        // The window backdrop is visible behind pages such as Settings, while
        // the chrome element needs its own backdrop instance for the toolbar
        SystemBackdrop = CreateSystemBackdrop(material);
        BrowserChromeMica.SystemBackdrop = CreateSystemBackdrop(material);
    }

    private static SystemBackdrop CreateSystemBackdrop(string material)
    {
        return material switch
        {
            "Mica" => new MicaBackdrop
            {
                Kind = MicaKind.Base
            },

            "MicaAlt" => new MicaBackdrop
            {
                Kind = MicaKind.BaseAlt
            },

            "Acrylic" => new DesktopAcrylicBackdrop(),

            _ => new MicaBackdrop
            {
                Kind = MicaKind.Base
            }
        };
    }

    private void UpdateFullscreenState(WebView2 webView)
    {
        if (webView.CoreWebView2 is not { } core)
            return;

        _webContentFullscreen = core.ContainsFullScreenElement;

        if (_webContentFullscreen)
        {
            CancelNormalContentLayoutRestore();
            _isRestoringNormalContentLayout = false;
            BrowserChrome.Visibility = Visibility.Visible;
            UpdateFullscreenContentLayout();
            _browserChromeIsShown = false;
            AnimateBrowserChrome(false);
            StartFullscreenPointerTracking();

            ExtendsContentIntoTitleBar = false;
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            return;
        }

        if (_browserFullscreen)
        {
            BrowserChrome.Visibility = Visibility.Visible;
            UpdateFullscreenContentLayout();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(DragRegion);

            _appWindow.SetPresenter(
                AppWindowPresenterKind.FullScreen);

            return;
        }

        BrowserChrome.Visibility =
            Visibility.Visible;

        _isRestoringNormalContentLayout = true;
        StopFullscreenPointerTracking();

        _appWindow.SetPresenter(
            AppWindowPresenterKind.Default);

        ExtendsContentIntoTitleBar = true;
        ShowBrowserChrome(RestoreNormalContentLayout);
        ScheduleNormalContentLayoutRestore();
    }

    public void ApplyTabWidth(string width)
    {
        MainTabView.TabWidthMode = width switch
        {
            "Equal" => TabViewWidthMode.Equal,
            "Title" => TabViewWidthMode.SizeToContent,
            "Compact" => TabViewWidthMode.Compact,
            _ => TabViewWidthMode.Equal
        };
    }

    private void EnsureBrowserChromeTransform()
    {
        if (_browserChromeTransform is not null)
            return;

        if (BrowserChrome.RenderTransform is TranslateTransform transform)
        {
            _browserChromeTransform = transform;
            return;
        }

        _browserChromeTransform = new TranslateTransform();
        BrowserChrome.RenderTransform = _browserChromeTransform;
    }

    private void AnimateBrowserChrome(
        bool visible,
        Action? completed = null)
    {
        EnsureBrowserChromeTransform();

        int animationVersion = ++_browserChromeAnimationVersion;

        double offset = visible
            ? 0
            : -BrowserChrome.ActualHeight;

        var animation = new DoubleAnimation
        {
            To = offset,
            Duration = new Duration(
                TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            },
            EnableDependentAnimation = true
        };

        var storyboard = new Storyboard();

        storyboard.Children.Add(animation);

        Storyboard.SetTarget(
            animation,
            _browserChromeTransform);

        Storyboard.SetTargetProperty(
            animation,
            nameof(TranslateTransform.Y));

        storyboard.Completed += (_, _) =>
        {
            if (animationVersion == _browserChromeAnimationVersion)
                completed?.Invoke();
        };

        storyboard.Begin();
    }

    private void InitializeFullscreenPointerTracking()
    {
        _fullscreenPointerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };

        _fullscreenPointerTimer.Tick += (_, _) =>
        {
            if (!IsChromeAutoHideFullscreen)
                return;

            nint windowHandle = GetWindowHandle();

            if (!GetCursorPos(out POINT cursor) ||
                !GetWindowRect(windowHandle, out RECT windowBounds))
            {
                return;
            }

            bool isWithinWindow =
                cursor.X >= windowBounds.Left &&
                cursor.X < windowBounds.Right &&
                cursor.Y >= windowBounds.Top &&
                cursor.Y < windowBounds.Bottom;

            if (!isWithinWindow)
            {
                _fullscreenRevealStartedAt = null;
                return;
            }

            bool isAtTopEdge = cursor.Y == windowBounds.Top;

            if (isAtTopEdge)
            {
                if (_fullscreenRevealStartedAt is null)
                {
                    _fullscreenRevealStartedAt = DateTime.UtcNow;
                }
                else if (
                    !_browserChromeIsShown &&
                    DateTime.UtcNow - _fullscreenRevealStartedAt.Value >=
                    TimeSpan.FromMilliseconds(250))
                {
                    _fullscreenRevealStartedAt = null;
                    ShowBrowserChrome();
                }

                return;
            }

            _fullscreenRevealStartedAt = null;

            const int revealZoneHeight = 16;

            double scale = GetDpiForWindow(windowHandle) / 96d;
            double chromeBottom = windowBounds.Top +
                (BrowserChrome.ActualHeight * scale) +
                revealZoneHeight;

            if (cursor.Y > chromeBottom)
                HideBrowserChrome();
        };

        _normalLayoutRestoreTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };

        _normalLayoutRestoreTimer.Tick += (_, _) =>
        {
            _normalLayoutRestoreTimer.Stop();

            if (!IsChromeAutoHideFullscreen &&
                _isRestoringNormalContentLayout)
            {
                RestoreNormalContentLayout();
            }
        };
    }

    private void StartFullscreenPointerTracking()
    {
        _fullscreenRevealStartedAt = null;
        _fullscreenPointerTimer?.Start();
    }

    private void StopFullscreenPointerTracking()
    {
        _fullscreenPointerTimer?.Stop();
        _fullscreenRevealStartedAt = null;
    }

    private void ScheduleNormalContentLayoutRestore()
    {
        _normalLayoutRestoreTimer?.Stop();
        _normalLayoutRestoreTimer?.Start();
    }

    private void CancelNormalContentLayoutRestore() =>
        _normalLayoutRestoreTimer?.Stop();

    private void ShowBrowserChrome(Action? completed = null)
    {
        if (_browserChromeIsShown)
        {
            completed?.Invoke();
            return;
        }

        _browserChromeIsShown = true;
        BrowserChrome.Visibility = Visibility.Visible;
        AnimateBrowserChrome(true, completed);
    }

    private void HideBrowserChrome()
    {
        if (!IsChromeAutoHideFullscreen)
            return;

        if (!_browserChromeIsShown)
            return;

        _browserChromeIsShown = false;
        BrowserChrome.Visibility = Visibility.Visible;
        AnimateBrowserChrome(false);
    }

    private void ToggleBrowserFullscreen()
    {
        _browserFullscreen = !_browserFullscreen;

        if (_browserFullscreen)
        {
            CancelNormalContentLayoutRestore();
            _isRestoringNormalContentLayout = false;
            ExtendsContentIntoTitleBar = false;
            BrowserChrome.Visibility = Visibility.Visible;
            UpdateFullscreenContentLayout();
            AnimateBrowserChrome(false);
            _browserChromeIsShown = false;
            StartFullscreenPointerTracking();

            _appWindow.SetPresenter(
                AppWindowPresenterKind.FullScreen);

            return;
        }

        if (_webContentFullscreen)
        {
            CancelNormalContentLayoutRestore();
            _isRestoringNormalContentLayout = false;
            BrowserChrome.Visibility = Visibility.Visible;
            UpdateFullscreenContentLayout();
            _browserChromeIsShown = false;
            AnimateBrowserChrome(false);
            StartFullscreenPointerTracking();

            _appWindow.SetPresenter(
                AppWindowPresenterKind.FullScreen);

            return;
        }

        BrowserChrome.Visibility = Visibility.Visible;
        _isRestoringNormalContentLayout = true;
        StopFullscreenPointerTracking();

        _appWindow.SetPresenter(
            AppWindowPresenterKind.Default);

        ExtendsContentIntoTitleBar = true;
        ShowBrowserChrome(RestoreNormalContentLayout);
        ScheduleNormalContentLayoutRestore();
    }

    private void UpdateFullscreenContentLayout()
    {
        bool overlaysContent = IsChromeAutoHideFullscreen ||
            _isRestoringNormalContentLayout;

        RootGrid.RowDefinitions[0].Height = overlaysContent
            ? new GridLength(0)
            : GridLength.Auto;

        Grid.SetRowSpan(BrowserChrome, overlaysContent ? 2 : 1);

        BrowserChrome.VerticalAlignment = overlaysContent
            ? VerticalAlignment.Top
            : VerticalAlignment.Stretch;

        CurrentTabContent.Margin = new Thickness(0);
    }

    private void RestoreNormalContentLayout()
    {
        CancelNormalContentLayoutRestore();
        _isRestoringNormalContentLayout = false;
        UpdateFullscreenContentLayout();

        // The fullscreen animation translates BrowserChrome (and DragRegion)
        // off-screen. Register the title bar only after that transform has
        // returned to zero, otherwise Windows retains an off-screen drag area
        SetTitleBar(DragRegion);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(
        nint windowHandle,
        out RECT rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
