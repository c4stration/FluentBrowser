using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

using MessagePack;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using MessagePack.Resolvers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, BitmapImage> _faviconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxInMemoryFavicons = 128;
    private const int MaxDiskFavicons = 512; // Or whatever.
    private const long MaxDiskFaviconBytes = 32L * 1024 * 1024;

    private readonly LinkedList<string> _faviconLru = new();
    private readonly Dictionary<string, LinkedListNode<string>>
        _faviconLruNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _faviconUrisByHost =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _faviconIndexLock = new(1, 1);
    private bool _faviconIndexLoaded;
    private static readonly MessagePackSerializerOptions _faviconIndexOptions =
        StandardResolver.Options.WithSecurity(
            MessagePackSecurity.UntrustedData);

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

    private bool TryGetCachedFavicon(
    string faviconUri,
    out BitmapImage? bitmap)
    {
        if (!_faviconCache.TryGetValue(faviconUri, out bitmap))
            return false;

        if (_faviconLruNodes.TryGetValue(faviconUri, out var node))
        {
            _faviconLru.Remove(node);
            _faviconLru.AddFirst(node);
        }

        return true;
    }

    private void CacheFavicon(
        string faviconUri,
        BitmapImage bitmap)
    {
        _faviconCache[faviconUri] = bitmap;

        if (_faviconLruNodes.TryGetValue(faviconUri, out var node))
        {
            _faviconLru.Remove(node);
            _faviconLru.AddFirst(node);
        }
        else
        {
            _faviconLruNodes[faviconUri] =
                _faviconLru.AddFirst(faviconUri);
        }

        while (_faviconCache.Count > MaxInMemoryFavicons)
        {
            var oldest = _faviconLru.Last;

            if (oldest is null)
                break;

            _faviconLru.RemoveLast();
            _faviconLruNodes.Remove(oldest.Value);
            _faviconCache.Remove(oldest.Value);
        }
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

            if (TryGetCachedFavicon(faviconUri, out var cached) &&
                cached is not null)
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
                CacheFavicon(faviconUri, diskFavicon);
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

            CacheFavicon(faviconUri, bitmap);

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

            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch { }

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
            string path = GetFaviconPath(faviconUri);

            await File.WriteAllBytesAsync(path, bytes);

            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
            }

            TrimFaviconDiskCache();
        }
        catch { }
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

        if (TryGetCachedFavicon(faviconUri, out var cached) &&
            cached is not null)
        {
            return cached;
        }

        BitmapImage? diskFavicon = await LoadFaviconFromDiskAsync(faviconUri);

        if (diskFavicon is not null)
            CacheFavicon(faviconUri, diskFavicon);

        return diskFavicon;
    }

    private void TrimFaviconDiskCache()
    {
        try
        {
            var files = new DirectoryInfo(_faviconCacheDirectory)
                .EnumerateFiles("*.png")
                .Select(file => new
                {
                    File = file,
                    Size = file.Length,
                    LastAccess = file.LastAccessTimeUtc
                })
                .OrderBy(file => file.LastAccess)
                .ToList();

            long totalBytes = files.Sum(file => file.Size);
            int count = files.Count;

            foreach (var item in files)
            {
                if (count <= MaxDiskFavicons &&
                    totalBytes <= MaxDiskFaviconBytes)
                {
                    break;
                }

                try
                {
                    item.File.Delete();
                    totalBytes -= item.Size;
                    count--;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"failed to trim favicon cache {ex}");
        }
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
            string legacyPath = GetLegacyFaviconIndexPath();

            if (File.Exists(path))
            {
                var entries = MessagePackSerializer.Deserialize<Dictionary<string, string>>(
                    await File.ReadAllBytesAsync(path),
                    _faviconIndexOptions);

                if (entries is not null)
                {
                    foreach ((string host, string faviconUri) in entries)
                        _faviconUrisByHost[host] = faviconUri;
                }

                if (File.Exists(legacyPath))
                    File.Delete(legacyPath);
            }
            else if (File.Exists(legacyPath))
            {
                string json = await File.ReadAllTextAsync(legacyPath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (entries is not null)
                {
                    foreach ((string host, string faviconUri) in entries)
                        _faviconUrisByHost[host] = faviconUri;

                    await WriteFaviconIndexAsync(path);
                    File.Delete(legacyPath);
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
            await WriteFaviconIndexAsync(GetFaviconIndexPath());
        }
        catch
        {
        }
        finally
        {
            _faviconIndexLock.Release();
        }
    }

    private async Task WriteFaviconIndexAsync(string path)
    {
        byte[] data = MessagePackSerializer.Serialize(
            _faviconUrisByHost,
            _faviconIndexOptions);

        string temporaryPath = path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, data);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private string GetFaviconIndexPath() =>
        Path.Combine(_faviconCacheDirectory, "hosts.msgpack");

    private string GetLegacyFaviconIndexPath() =>
        Path.Combine(_faviconCacheDirectory, "hosts.json");

    private static string GetFaviconHost(Uri uri) =>
        uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;

    public void ClearFaviconCache()
    {
        _faviconCache.Clear();
        _faviconLru.Clear();
        _faviconLruNodes.Clear();
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
