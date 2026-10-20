using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Windows.System;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly System.Collections.Generic.HashSet<WebView2>
        _suspendingWebViews = [];

    private int _memoryReliefQueued;
    private int _memoryReliefRequested;
    private bool _isWindowClosing;

    private void InitializeTabMemoryManagement()
    {
        MemoryManager.AppMemoryUsageIncreased +=
            MemoryManager_AppMemoryUsageIncreased;

        MemoryManager.AppMemoryUsageLimitChanging +=
            MemoryManager_AppMemoryUsageLimitChanging;

        Debug.WriteLine(
            $"tab memory management initialized; " +
            $"usage level: {MemoryManager.AppMemoryUsageLevel}, " +
            $"usage: {MemoryManager.AppMemoryUsage:N0} bytes.");
    }

    private void ShutdownTabMemoryManagement()
    {
        _isWindowClosing = true;

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

                await DiscardBackgroundWebViewsAsync(reason);
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

            if (core.IsDocumentPlayingAudio ||
                core.ContainsFullScreenElement)
            {
                return;
            }

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
            ReferenceEquals(SelectedTab, tab) ||
            string.IsNullOrWhiteSpace(tab.LastKnownUrl) ||
            tab.WebView is not WebView2 candidate ||
            _loadingWebViews.Contains(candidate))
        {
            return false;
        }

        CoreWebView2? core = candidate.CoreWebView2;

        if (core is null ||
            core.IsDocumentPlayingAudio ||
            core.ContainsFullScreenElement)
        {
            return false;
        }

        webView = candidate;
        return true;
    }

    private async Task DiscardBackgroundWebViewsAsync(string reason)
    {
        int discarded = 0;

        foreach (BrowserTab tab in _browserTabs.Values.ToArray())
        {
            if (!TryGetDiscardableWebView(
                    tab,
                    out WebView2 webView))
            {
                continue;
            }

            string url = tab.LastKnownUrl;

            if (string.IsNullOrWhiteSpace(url))
                continue;

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

            discarded++;

            Debug.WriteLine(
                $"discarded background tab {tab.ExtensionTabId}: {url}; " +
                $"reason: {reason}.");

            await Task.Yield();
        }

        Debug.WriteLine(
            $"memory relief finished; discarded {discarded} tab(s).");
    }

    private void CleanupWebViewTracking(WebView2 webView)
    {
        CancelTabAudio(webView);

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