using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Windows.Storage;
using Windows.System;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly System.Collections.Generic.HashSet<WebView2>
        _suspendingWebViews = [];

    private const int DefaultIdleDiscardMinutes = 30;

    private const int PressureKeepRecentTabs = 2;

    private DispatcherTimer? _idleTabTimer;
    private bool _idleDiscardRunning;

    private int _memoryReliefQueued;
    private int _memoryReliefRequested;
    private bool _isWindowClosing;

    private void InitializeTabMemoryManagement()
    {
        MemoryManager.AppMemoryUsageIncreased +=
            MemoryManager_AppMemoryUsageIncreased;

        MemoryManager.AppMemoryUsageLimitChanging +=
            MemoryManager_AppMemoryUsageLimitChanging;

        _idleTabTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };

        _idleTabTimer.Tick += IdleTabTimer_Tick;
        _idleTabTimer.Start();

        Debug.WriteLine(
            $"tab memory management initialized; " +
            $"usage level: {MemoryManager.AppMemoryUsageLevel}, " +
            $"usage: {MemoryManager.AppMemoryUsage:N0} bytes.");
    }

    private void ShutdownTabMemoryManagement()
    {
        _isWindowClosing = true;

        if (_idleTabTimer is not null)
        {
            _idleTabTimer.Stop();
            _idleTabTimer.Tick -= IdleTabTimer_Tick;
            _idleTabTimer = null;
        }

        MemoryManager.AppMemoryUsageIncreased -=
            MemoryManager_AppMemoryUsageIncreased;

        MemoryManager.AppMemoryUsageLimitChanging -=
            MemoryManager_AppMemoryUsageLimitChanging;
    }

    private void MemoryManager_AppMemoryUsageIncreased(
        object? sender,
        object e)
    {
        AppMemoryUsageLevel level = MemoryManager.AppMemoryUsageLevel;

        if (level is AppMemoryUsageLevel.High or
            AppMemoryUsageLevel.OverLimit)
        {
            QueueMemoryRelief($"usage level increased to {level}");
        }
    }

    private void MemoryManager_AppMemoryUsageLimitChanging(
        object? sender,
        AppMemoryUsageLimitChangingEventArgs e)
    {
        ulong usage = MemoryManager.AppMemoryUsage;

        if (usage > e.NewLimit)
        {
            QueueMemoryRelief(
                $"memory limit changing to {e.NewLimit:N0} bytes");
        }
    }

    private void QueueMemoryRelief(string reason)
    {
        Interlocked.Exchange(ref _memoryReliefRequested, 1);

        if (Interlocked.CompareExchange(
                ref _memoryReliefQueued,
                1,
                0) != 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _ = RunMemoryReliefAsync(reason);
        }))
        {
            Interlocked.Exchange(ref _memoryReliefQueued, 0);
            Interlocked.Exchange(ref _memoryReliefRequested, 0);

            Debug.WriteLine(
                "unable to queue WebView2 memory relief.");
        }
    }

    private async Task RunMemoryReliefAsync(string reason)
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _memoryReliefRequested, 0);

                if (_isWindowClosing)
                    return;

                Debug.WriteLine(
                    $"starting WebView2 memory relief: {reason}; " +
                    $"usage level: {MemoryManager.AppMemoryUsageLevel}; " +
                    $"usage: {MemoryManager.AppMemoryUsage:N0} bytes.");

                await DiscardBackgroundWebViewsAsync(
                    reason,
                    minIdle: null,
                    keepMostRecent: PressureKeepRecentTabs);
            }
            while (Interlocked.Exchange(
                       ref _memoryReliefRequested,
                       0) != 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"WebView2 memory relief failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _memoryReliefQueued, 0);

            if (Interlocked.Exchange(
                    ref _memoryReliefRequested,
                    0) != 0 &&
                !_isWindowClosing)
            {
                QueueMemoryRelief("memory pressure recheck");
            }
        }
    }

    private async Task SuspendBackgroundTabAsync(BrowserTab tab)
    {
        WebView2? webView = tab.WebView;

        if (webView is null ||
            tab.IsClosed ||
            tab.IsInitializing ||
            tab.WasDiscarded ||
            ReferenceEquals(SelectedTab, tab) ||
            !ReferenceEquals(tab.WebView, webView) ||
            _loadingWebViews.Contains(webView) ||
            !_suspendingWebViews.Add(webView))
        {
            return;
        }

        try
        {
            webView.Visibility = Visibility.Collapsed;

            await Task.Yield();

            if (tab.IsClosed ||
                tab.IsInitializing ||
                !ReferenceEquals(tab.WebView, webView) ||
                ReferenceEquals(SelectedTab, tab) ||
                webView.Visibility != Visibility.Collapsed ||
                _loadingWebViews.Contains(webView))
            {
                return;
            }

            CoreWebView2? core = webView.CoreWebView2;

            if (core is null || core.IsSuspended)
                return;

            if (IsTabPlayingAudio(core) ||
                core.ContainsFullScreenElement)
            {
                return;
            }

            SetTabMemoryTarget(core, low: true);

            bool suspended = await core.TrySuspendAsync();

            Debug.WriteLine(
                $"tab {tab.ExtensionTabId} suspension result: {suspended}; " +
                $"IsSuspended={core.IsSuspended}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"failed to suspend tab {tab.ExtensionTabId}: {ex}");
        }
        finally
        {
            _suspendingWebViews.Remove(webView);
        }
    }

    private bool TryGetDiscardableWebView(
        BrowserTab tab,
        out WebView2 webView)
    {
        webView = null!;

        if (tab.IsClosed ||
            tab.IsInitializing ||
            tab.WasDiscarded ||
            IsInternalPageDisplayed(tab) ||
            ReferenceEquals(SelectedTab, tab) ||
            string.IsNullOrWhiteSpace(tab.LastKnownUrl) ||
            tab.WebView is not WebView2 candidate ||
            _loadingWebViews.Contains(candidate))
        {
            return false;
        }

        CoreWebView2? core = candidate.CoreWebView2;

        if (core is null ||
            IsTabPlayingAudio(core) ||
            core.ContainsFullScreenElement)
        {
            return false;
        }

        webView = candidate;
        return true;
    }

    private static int GetIdleDiscardMinutes() =>
        ApplicationData.Current.LocalSettings.Values["TabDiscardMinutes"]
            is int minutes
            ? minutes
            : DefaultIdleDiscardMinutes;

    private async void IdleTabTimer_Tick(object? sender, object e)
    {
        if (_isWindowClosing || _idleDiscardRunning)
            return;

        int minutes = GetIdleDiscardMinutes();

        if (minutes <= 0)
            return;

        _idleDiscardRunning = true;

        try
        {
            await DiscardBackgroundWebViewsAsync(
                "idle timeout",
                minIdle: TimeSpan.FromMinutes(minutes),
                keepMostRecent: 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"idle tab discard failed: {ex}");
        }
        finally
        {
            _idleDiscardRunning = false;
        }
    }

    private static void SetTabMemoryTarget(CoreWebView2 core, bool low)
    {
        try
        {
            core.MemoryUsageTargetLevel = low
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"failed to set memory target level: {ex.Message}");
        }
    }

    private async Task DiscardBackgroundWebViewsAsync(
        string reason,
        TimeSpan? minIdle = null,
        int keepMostRecent = 0)
    {
        int discarded = 0;
        DateTime now = DateTime.UtcNow;

        List<BrowserTab> candidates = [.. _browserTabs.Values
            .Where(tab => TryGetDiscardableWebView(tab, out _))
            .OrderBy(tab => tab.LastDeactivatedUtc)];

        if (keepMostRecent > 0)
        {
            candidates = [.. candidates.Take(
                Math.Max(0, candidates.Count - keepMostRecent))];
        }

        foreach (BrowserTab tab in candidates)
        {
            if (_isWindowClosing)
                break;

            if (minIdle is TimeSpan idle &&
                now - tab.LastDeactivatedUtc < idle)
            {
                continue;
            }

            if (!DiscardTab(tab, reason))
                continue;

            discarded++;

            await Task.Yield();
        }

        Debug.WriteLine(
            $"memory relief finished; discarded {discarded} tab(s).");
    }

    private bool DiscardTab(BrowserTab tab, string reason)
    {
        if (!TryGetDiscardableWebView(tab, out WebView2 webView))
            return false;

        DiscardTabCore(tab, webView, reason);
        return true;
    }

    private int DiscardAllExcept(BrowserTab keep, string reason)
    {
        int discarded = 0;

        foreach (BrowserTab tab in _browserTabs.Values.ToArray())
        {
            if (!ReferenceEquals(tab, keep) && DiscardTab(tab, reason))
                discarded++;
        }

        return discarded;
    }

    private void DiscardTabCore(
        BrowserTab tab,
        WebView2 webView,
        string reason)
    {
        string url = tab.LastKnownUrl;

        FrameworkElement oldContent = tab.Content;
        Page? oldErrorPage = tab.ErrorPage;

        if (ReferenceEquals(CurrentTabContent.Content, oldContent) ||
            ReferenceEquals(CurrentTabContent.Content, oldErrorPage))
        {
            CurrentTabContent.Content = null;
        }

        webView.Visibility = Visibility.Collapsed;

        tab.Content = new Grid();
        tab.ErrorPage = null;
        tab.WebView = null;
        tab.WasDiscarded = true;

        CleanupWebViewTracking(webView);

        webView.Tag = null;
        webView.Close();

        Debug.WriteLine(
            $"discarded background tab {tab.ExtensionTabId}: {url}; " +
            $"reason: {reason}.");
    }

    private void CleanupWebViewTracking(WebView2 webView)
    {
        CancelTabAudio(webView);

        if (webView.CoreWebView2 is { } core)
            _tabMediaPlayback.Remove(core);

        _loadingWebViews.Remove(webView);
        RemoveLoadProgressTracking(webView);
        _zoomFactors.Remove(webView);
        _webViewShortcutTokens.Remove(webView);

        _navigationUris.Remove(webView);
        _downloadNavigations.Remove(webView);

        _suspendingWebViews.Remove(webView);

        if (_certificateErrorDecisions.Remove(
                webView,
                out TaskCompletionSource<bool>? decision))
        {
            decision.TrySetResult(false);
        }
    }
}