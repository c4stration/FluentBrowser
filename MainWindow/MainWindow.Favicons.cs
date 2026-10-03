using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, BitmapImage> _faviconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _faviconUrisByHost =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _faviconIndexLock = new(1, 1);
    private bool _faviconIndexLoaded;

    public event EventHandler? FaviconCacheChanged;

    private static FontIcon CreateDefaultFavicon()
    {
        return new FontIcon
        {
            Glyph = "\uE774",
            FontSize = 16
        };
    }

    private void UpdateDefaultFavicon(BrowserTab tab, bool visible)
    {
        if (tab.Favicon.Parent is not Grid grid)
            return;

        var defaultIcon = grid.Children
            .OfType<FontIcon>()
            .FirstOrDefault();

        if (defaultIcon is null)
        {
            defaultIcon = CreateDefaultFavicon();
            grid.Children.Add(defaultIcon);
        }

        defaultIcon.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task UpdateFaviconAsync(BrowserTab tab)
    {
        try
        {
            if (tab.IsClosed ||
                tab.WebView is not { } webView ||
                webView.CoreWebView2 is not { } core)
            {
                return;
            }

            string? faviconUri = core.FaviconUri;

            if (string.IsNullOrWhiteSpace(faviconUri))
            {
                tab.Favicon.Source = null;
                tab.Favicon.Visibility = Visibility.Collapsed;

                UpdateDefaultFavicon(
                    tab,
                    !_loadingWebViews.Contains(webView));

                return;
            }

            await RememberFaviconForHostAsync(webView.Source, faviconUri);

            if (_faviconCache.TryGetValue(faviconUri, out var cached))
            {
                if (tab.IsClosed ||
                    tab.WebView is not { } cachedWebView ||
                    !ReferenceEquals(cachedWebView, webView))
                {
                    return;
                }

                tab.Favicon.Source = cached;
                UpdateDefaultFavicon(tab, false);

                if (!_loadingWebViews.Contains(webView))
                    tab.Favicon.Visibility = Visibility.Visible;

                return;
            }

            var diskFavicon = await LoadFaviconFromDiskAsync(faviconUri);

            if (tab.IsClosed ||
                tab.WebView is not { } diskWebView ||
                !ReferenceEquals(diskWebView, webView))
            {
                return;
            }

            if (diskFavicon is not null)
            {
                _faviconCache[faviconUri] = diskFavicon;
                tab.Favicon.Source = diskFavicon;
                UpdateDefaultFavicon(tab, false);

                if (!_loadingWebViews.Contains(webView))
                    tab.Favicon.Visibility = Visibility.Visible;

                return;
            }

            using var faviconStream = await core.GetFaviconAsync(
                CoreWebView2FaviconImageFormat.Png);

            if (tab.IsClosed ||
                tab.WebView is not { } fetchedWebView ||
                !ReferenceEquals(fetchedWebView, webView))
            {
                return;
            }

            if (faviconStream is null)
                return;

            using var memoryStream = new MemoryStream();

            await faviconStream
                .AsStreamForRead()
                .CopyToAsync(memoryStream);

            if (tab.IsClosed ||
                tab.WebView is not { } copiedWebView ||
                !ReferenceEquals(copiedWebView, webView))
            {
                return;
            }

            byte[] bytes = memoryStream.ToArray();

            if (bytes.Length == 0)
                return;

            var bitmap = new BitmapImage();

            using var bitmapStream = new MemoryStream(bytes);

            await bitmap.SetSourceAsync(
                bitmapStream.AsRandomAccessStream());

            if (tab.IsClosed ||
                tab.WebView is not { } bitmapWebView ||
                !ReferenceEquals(bitmapWebView, webView))
            {
                return;
            }

            _faviconCache[faviconUri] = bitmap;

            await SaveFaviconToDiskAsync(faviconUri, bytes);

            FaviconCacheChanged?.Invoke(this, EventArgs.Empty);

            if (tab.IsClosed ||
                tab.WebView is not { } savedWebView ||
                !ReferenceEquals(savedWebView, webView))
            {
                return;
            }

            tab.Favicon.Source = bitmap;
            UpdateDefaultFavicon(tab, false);

            if (!_loadingWebViews.Contains(webView))
                tab.Favicon.Visibility = Visibility.Visible;
        }
        catch
        {
            // "For example, nothing" - Mbappe
        }
    }

    private async Task<BitmapImage?> LoadFaviconFromDiskAsync(
        string faviconUri)
    {
        string path = GetFaviconPath(faviconUri);

        if (!File.Exists(path))
            return null;

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            var bitmap = new BitmapImage();

            using var stream = new MemoryStream(bytes);

            await bitmap.SetSourceAsync(
                stream.AsRandomAccessStream());

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveFaviconToDiskAsync(
        string faviconUri,
        byte[] bytes)
    {
        try
        {
            await File.WriteAllBytesAsync(
                GetFaviconPath(faviconUri),
                bytes);
        }
        catch
        {
            // All the things she said, all the things she said, running through my head, running through my head, running through my head
        }
    }

    private string GetFaviconPath(string faviconUri)
    {
        byte[] hash = XxHash3.Hash(
            Encoding.UTF8.GetBytes(faviconUri));

        return Path.Combine(
            _faviconCacheDirectory,
            $"{Convert.ToHexString(hash)}.png");
    }

    private async Task<BitmapImage?> GetSuggestionFaviconAsync(Uri target)
    {
        await EnsureFaviconIndexLoadedAsync();

        if (!_faviconUrisByHost.TryGetValue(
                GetFaviconHost(target),
                out string? faviconUri))
            return null;

        if (_faviconCache.TryGetValue(faviconUri, out BitmapImage? cached))
            return cached;

        BitmapImage? diskFavicon = await LoadFaviconFromDiskAsync(faviconUri);

        if (diskFavicon is not null)
            _faviconCache[faviconUri] = diskFavicon;

        return diskFavicon;
    }

    private async Task RememberFaviconForHostAsync(
        Uri? pageUri,
        string faviconUri)
    {
        if (pageUri is null || string.IsNullOrWhiteSpace(pageUri.Host))
            return;

        await EnsureFaviconIndexLoadedAsync();

        string host = GetFaviconHost(pageUri);

        if (_faviconUrisByHost.TryGetValue(host, out string? current) &&
            string.Equals(current, faviconUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _faviconUrisByHost[host] = faviconUri;
        await SaveFaviconIndexAsync();
    }

    private async Task EnsureFaviconIndexLoadedAsync()
    {
        if (_faviconIndexLoaded)
            return;

        await _faviconIndexLock.WaitAsync();

        try
        {
            if (_faviconIndexLoaded)
                return;

            string path = GetFaviconIndexPath();

            if (File.Exists(path))
            {
                string json = await File.ReadAllTextAsync(path);
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (entries is not null)
                {
                    foreach ((string host, string faviconUri) in entries)
                        _faviconUrisByHost[host] = faviconUri;
                }
            }

            _faviconIndexLoaded = true;
        }
        catch
        {
            _faviconIndexLoaded = true;
        }
        finally
        {
            _faviconIndexLock.Release();
        }
    }

    private async Task SaveFaviconIndexAsync()
    {
        await _faviconIndexLock.WaitAsync();

        try
        {
            await File.WriteAllTextAsync(
                GetFaviconIndexPath(),
                JsonSerializer.Serialize(_faviconUrisByHost));
        }
        catch
        {
        }
        finally
        {
            _faviconIndexLock.Release();
        }
    }

    private string GetFaviconIndexPath() =>
        Path.Combine(_faviconCacheDirectory, "hosts.json");

    private static string GetFaviconHost(Uri uri) =>
        uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;

    public void ClearFaviconCache()
    {
        _faviconCache.Clear();
        _faviconUrisByHost.Clear();
        _faviconIndexLoaded = true;

        try
        {
            if (Directory.Exists(_faviconCacheDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(
                    _faviconCacheDirectory,
                    "*"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Why would you ever want to delete a file? It's not like it takes up space or anything.
                    }
                }
            }
        }
        catch
        {
            // Nothing Ever Happens
        }

        FaviconCacheChanged?.Invoke(this, EventArgs.Empty);
    }
}
