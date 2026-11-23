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
