using FluentBrowser.Shared;
using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly FingerprintingProtectionService
        _fingerprintingProtection = new();

    /// <summary>
    /// Applies the persisted FingerprintingProtection setting to every
    /// existing website WebView. Future WebViews pick the setting up when
    /// they are attached, before their first navigation.
    ///
    /// Pages that are already loaded are not reloaded here: that would
    /// discard form input and playback in tabs the user is not looking at.
    /// They are reloaded lazily, once, when the tab is next selected.
    /// </summary>
    public async Task ApplyFingerprintingProtectionAsync()
    {
        try
        {
            _fingerprintingProtection.SetEnabled(
                FingerprintingProtectionService.GetEnabled());

            foreach (BrowserTab tab in _browserTabs.Values.ToArray())
            {
                if (tab.IsClosed ||
                    tab.WebView?.CoreWebView2 is not { } core)
                {
                    continue;
                }

                await _fingerprintingProtection.AttachAsync(core);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to update fingerprinting protection: {ex}");
        }
    }

    /// <summary>
    /// Called when a tab is selected. Re-synchronizes its WebView (it may
    /// have been suspended while the setting changed) and reloads the page
    /// once if it was loaded under a different setting.
    /// </summary>
    private async Task RefreshFingerprintingProtectionAsync(BrowserTab tab)
    {
        try
        {
            if (tab.IsClosed ||
                tab.WasDiscarded ||
                tab.WebView?.CoreWebView2 is not { } core)
            {
                return;
            }

            await _fingerprintingProtection.AttachAsync(core);

            if (tab.IsClosed ||
                tab.ErrorPage is not null ||
                tab.WebView is not { } webView ||
                !ReferenceEquals(webView.CoreWebView2, core) ||
                !IsDisplayedWebView(webView) ||
                !_fingerprintingProtection.NeedsReload(core))
            {
                return;
            }

            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp &&
                 uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            // Do not interrupt playback; the page keeps its old protection
            // state until the next navigation or a manual reload.
            if (IsTabPlayingAudio(core))
                return;

            webView.Reload();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to refresh fingerprinting protection: {ex}");
        }
    }
}
