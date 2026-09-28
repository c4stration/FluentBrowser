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
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, BitmapImage> _faviconCache =
        new(StringComparer.OrdinalIgnoreCase);

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
            // Disk caching is optional.
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

    public void ClearFaviconCache()
    {
        _faviconCache.Clear();

        try
        {
            if (Directory.Exists(_faviconCacheDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(
                    _faviconCacheDirectory,
                    "*.png"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore files that cannot be deleted.
                    }
                }
            }
        }
        catch
        {
            // Disk cache clearing is optional.
        }

        FaviconCacheChanged?.Invoke(this, EventArgs.Empty);
    }
}
