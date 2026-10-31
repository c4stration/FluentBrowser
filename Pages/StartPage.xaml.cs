using FluentBrowser.Shared;
using MessagePack;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUI3Localizer;

// TODO: fix the gear icon becoming white on a transparent background when the underlying pixels are white

namespace FluentBrowser.Pages;

public sealed partial class StartPage : Page
{
    private const string ImageMode = "Image";
    private const string RandomMode = "Random";
    private const string LegacySpotlightMode = "Spotlight";
    private const string NoneMode = "None";
    private const int MaximumRecentBackgrounds = 12;
    private const int RandomBackgroundBatchSize = 10;
    private const int RandomBackgroundRefillThreshold = 3;
    private const int MaximumRandomBackgroundCacheEntries = 40;
    private static readonly string CommonsApiUrl =
        "https://commons.wikimedia.org/w/api.php?action=query" +
        $"&generator=random&grnnamespace=6&grnlimit={RandomBackgroundBatchSize}" +
        "&prop=imageinfo&iiprop=url%7Cextmetadata&iiurlwidth=1920" +
        "&format=json&formatversion=2&origin=*&maxage=0&smaxage=0";

    private static readonly HttpClient RandomBackgroundHttpClient =
        CreateRandomBackgroundHttpClient();
    private static readonly object CommonsCooldownLock = new();
    private static readonly object RandomBackgroundCacheLock = new();
    private static DateTimeOffset _commonsRetryAfterUtc = DateTimeOffset.MinValue;

    private bool _isRefreshingRecentBackgrounds;
    private CancellationTokenSource? _randomBackgroundLoadCancellation;
    private int _randomBackgroundRequestId;
    private readonly SemaphoreSlim _randomBackgroundQueueLock = new(1, 1);
    private readonly SemaphoreSlim _randomBackgroundDownloadLimiter = new(2, 2);
    private readonly object _randomBackgroundDownloadsLock = new();
    private readonly Dictionary<string, Task<bool>> _randomBackgroundDownloads =
        new(StringComparer.Ordinal);
    private readonly List<RandomBackgroundEntry> _randomBackgroundQueue = [];
    private readonly Dictionary<string, RandomBackgroundEntry>
        _randomBackgroundEntries = new(StringComparer.Ordinal);

    private bool _isUpdatingMode;
    private BackgroundSettings _settings = new();

    public ObservableCollection<BackgroundImageItem> RecentBackgrounds { get; } = new();

    public StartPage()
    {
        InitializeComponent();

        BackgroundModeComboBox.SelectionChanged += BackgroundModeComboBox_SelectionChanged;

        Loaded += StartPage_Loaded;
        Unloaded += StartPage_Unloaded;

        LoadBackgroundSettings();
        LoadRandomBackgroundCache();
        RefreshRecentBackgrounds();
        SynchronizeModeControls();
        UpdateBackgroundModeCards();
        ApplyCurrentBackground();
        SetRandomBackgroundDescription();
    }

    private void StartPage_Loaded(object sender, RoutedEventArgs e)
    {
        var localizer = Localizer.Get();

        localizer.LanguageChanged -= OnLanguageChanged;
        localizer.LanguageChanged += OnLanguageChanged;

        SetRandomBackgroundDescription();
    }

    private void StartPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Localizer.Get().LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(
        object? sender,
        LanguageChangedEventArgs e)
    {
        SetRandomBackgroundDescription();
    }

    private string SettingsFilePath => Path.Combine(Paths.CustomBackgrounds, "settings.msgpack");
    private string LegacySettingsFilePath => Path.Combine(Paths.CustomBackgrounds, "settings.json");
    private string RandomBackgroundMetadataPath => Path.Combine(
        Paths.RandomBackgrounds,
        "commons-backgrounds.msgpack");

    private static string? GetBackgroundFileName(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFileName(path);
    }

    private static string? ResolveBackgroundPath(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            ? null
            : Path.Combine(Paths.CustomBackgrounds, Path.GetFileName(fileName));
    }

    private static bool HasDirectory(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               !string.Equals(path, Path.GetFileName(path), StringComparison.Ordinal);
    }

    private void RecentBackgroundsItemsView_SelectionChanged(
        ItemsView sender,
        ItemsViewSelectionChangedEventArgs e)
    {
        if (_isRefreshingRecentBackgrounds)
        {
            return;
        }

        if (sender.SelectedItem is BackgroundImageItem { IsAddButton: true })
        {
            sender.Deselect(0);
            return;
        }

        if (sender.SelectedItem is not BackgroundImageItem item ||
            !File.Exists(item.FilePath))
        {
            return;
        }

        _settings.SelectedBackgroundPath = item.FilePath;

        SetBackgroundMode(ImageMode);
        ApplyCurrentBackground();
        SaveBackgroundSettings();
    }

    private void RecentBackgroundsItemsView_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ItemsView itemsView)
        {
            return;
        }

        if (itemsView.ScrollView is { } scrollView)
        {
            scrollView.PointerWheelChanged -= RecentBackgroundsScrollView_PointerWheelChanged;
            scrollView.PointerWheelChanged += RecentBackgroundsScrollView_PointerWheelChanged;
        }

        var selectedIndex = RecentBackgrounds
            .Select((item, index) => new { item, index })
            .Where(entry =>
                !entry.item.IsAddButton &&
                string.Equals(
                    entry.item.FilePath,
                    _settings.SelectedBackgroundPath,
                    StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .First();

        if (selectedIndex < 0)
        {
            return;
        }

        var wasRefreshing = _isRefreshingRecentBackgrounds;
        _isRefreshingRecentBackgrounds = true;

        try
        {
            itemsView.DeselectAll();
            itemsView.Select(selectedIndex);
        }
        finally
        {
            _isRefreshingRecentBackgrounds = wasRefreshing;
        }
    }

    private void RecentBackgroundsScrollView_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not ScrollView scrollView ||
            scrollView.ComputedVerticalScrollMode != ScrollingScrollMode.Disabled ||
            scrollView.ComputedHorizontalScrollMode != ScrollingScrollMode.Enabled)
        {
            return;
        }

        var delta = e.GetCurrentPoint(scrollView).Properties.MouseWheelDelta;

        scrollView.ScrollBy(
            -delta / 2.5,
            0,
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));

        e.Handled = true;
    }

    private async void AddBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;

        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.CommitButtonText = "Choose";
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.ViewMode = PickerViewMode.Thumbnail;

            var pickedFile = await picker.PickSingleFileAsync();
            if (pickedFile is null || string.IsNullOrWhiteSpace(pickedFile.Path))
            {
                return;
            }

            var extension = Path.GetExtension(pickedFile.Path).ToLowerInvariant();
            if (!IsSupportedImage(pickedFile.Path))
            {
                await ShowErrorDialogAsync("Choose a JPG, JPEG, or PNG image.");
                return;
            }

            Directory.CreateDirectory(Paths.CustomBackgrounds);

            var destinationPath = FindExistingBackground(pickedFile.Path);

            if (destinationPath is null)
            {
                destinationPath = Path.Combine(
                    Paths.CustomBackgrounds,
                    $"{Guid.NewGuid():N}{extension}");

                File.Copy(pickedFile.Path, destinationPath);
            }
            else
            {
                DeleteDuplicateBackgrounds(destinationPath);
            }

            AddToRecentBackgrounds(destinationPath);
            _settings.SelectedBackgroundPath = destinationPath;
            SetBackgroundMode(ImageMode);
            RefreshRecentBackgrounds();
            ApplyCurrentBackground();
            SaveBackgroundSettings();
        }
        catch (Exception exception)
        {
            await ShowErrorDialogAsync($"The background image could not be added.\n\n{exception.Message}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void BackgroundItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path || !File.Exists(path))
        {
            return;
        }

        AddToRecentBackgrounds(path);
        _settings.SelectedBackgroundPath = path;
        SetBackgroundMode(ImageMode);
        RefreshRecentBackgrounds();
        ApplyCurrentBackground();
        SaveBackgroundSettings();
    }

    private async void DeleteBackgroundMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem menuItem ||
            menuItem.Tag is not string path ||
            string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var backgroundItem = RecentBackgrounds.FirstOrDefault(item =>
            !item.IsAddButton &&
            string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));

        if (backgroundItem is null)
        {
            return;
        }

        var wasSelected = string.Equals(
            _settings.SelectedBackgroundPath,
            path,
            StringComparison.OrdinalIgnoreCase);

        if (wasSelected)
        {
            RootGrid.Background = null;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            if (wasSelected)
            {
                ApplyCurrentBackground();
            }

            await ShowErrorDialogAsync(
                $"The background image could not be deleted.\n\n{exception.Message}");
            return;
        }

        _settings.RecentBackgroundPaths.RemoveAll(existingPath =>
            string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase));

        if (wasSelected)
        {
            _settings.SelectedBackgroundPath = _settings.RecentBackgroundPaths
                .FirstOrDefault(existingPath =>
                    File.Exists(existingPath) && IsSupportedImage(existingPath));
        }

        _isRefreshingRecentBackgrounds = true;

        try
        {
            RecentBackgroundsItemsView.DeselectAll();
            RecentBackgrounds.Remove(backgroundItem);

            var selectedIndex = RecentBackgrounds
                .Select((item, index) => new { item, index })
                .Where(entry => !entry.item.IsAddButton &&
                    string.Equals(
                        entry.item.FilePath,
                        _settings.SelectedBackgroundPath,
                        StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.index)
                .DefaultIfEmpty(-1)
                .First();

            if (selectedIndex >= 0)
            {
                RecentBackgroundsItemsView.Select(selectedIndex);
            }
        }
        finally
        {
            _isRefreshingRecentBackgrounds = false;
        }

        ApplyCurrentBackground();
        SaveBackgroundSettings();
    }

    private void BackgroundModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMode || sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var selectedMode = item.Tag?.ToString();
        var mode = selectedMode switch
        {
            RandomMode => RandomMode,
            NoneMode => NoneMode,
            _ => ImageMode
        };

        SetBackgroundMode(mode);
    }

    private void SetRandomBackgroundDescription()
    {
        if (_settings.Mode != RandomMode)
        {
            RandomBackgroundDescription.Visibility = Visibility.Collapsed;
            RandomBackgroundDescription.Inlines.Clear();
            return;
        }

        RandomBackgroundDescription.Visibility = Visibility.Visible;
        RandomBackgroundDescription.Inlines.Clear();

        string message = LocalizerExtensions.GetLocalizedString("RandomBackgroundDescription");

        int placeholderIndex =
            message.IndexOf("{0}", StringComparison.Ordinal);

        if (placeholderIndex < 0)
        {
            RandomBackgroundDescription.Inlines.Add(
                new Run { Text = message });

            return;
        }

        RandomBackgroundDescription.Inlines.Add(
            new Run { Text = message[..placeholderIndex] });

        var hyperlink = new Hyperlink
        {
            UnderlineStyle = UnderlineStyle.None,
            NavigateUri = new Uri("https://commons.wikimedia.org/")
        };

        hyperlink.Inlines.Add(new Run { Text = "Wikimedia Commons" });
        RandomBackgroundDescription.Inlines.Add(hyperlink);

        RandomBackgroundDescription.Inlines.Add(
            new Run { Text = message[(placeholderIndex + 3)..] });
    }

    private void SetBackgroundMode(string mode)
    {
        _settings.Mode = mode switch
        {
            RandomMode => RandomMode,
            NoneMode => NoneMode,
            _ => ImageMode
        };
        SynchronizeModeControls();
        UpdateBackgroundModeCards();
        ApplyCurrentBackground();
        SaveBackgroundSettings();
    }

    private void SynchronizeModeControls()
    {
        _isUpdatingMode = true;

        try
        {
            var selectedMode = _settings.Mode switch
            {
                RandomMode => RandomMode,
                NoneMode => NoneMode,
                _ => ImageMode
            };

            SelectMode(BackgroundModeComboBox, selectedMode);
        }
        finally
        {
            _isUpdatingMode = false;
        }
    }

    private static void SelectMode(ComboBox comboBox, string mode)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), mode, StringComparison.Ordinal));
    }

    private void UpdateBackgroundModeCards()
    {
        BackgroundModeCard.IsExpander = _settings.Mode == ImageMode;

        SetRandomBackgroundDescription();
    }

    private void ApplyCurrentBackground()
    {
        _randomBackgroundLoadCancellation?.Cancel();
        _randomBackgroundLoadCancellation = null;
        var requestId = ++_randomBackgroundRequestId;
        SetIconToThemeForeground();
        SetImageMetadata(null, null, null);
        InfoButton.Visibility = _settings.Mode == RandomMode
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_settings.Mode == NoneMode)
        {
            RootGrid.Background = null;
            return;
        }

        if (_settings.Mode == RandomMode)
        {
            var cancellation = new CancellationTokenSource();
            _randomBackgroundLoadCancellation = cancellation;
            _ = ApplyRandomBackgroundAsync(requestId, cancellation);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_settings.SelectedBackgroundPath) &&
            File.Exists(_settings.SelectedBackgroundPath))
        {
            SetBackgroundImage(_settings.SelectedBackgroundPath, requestId);
        }
        else
        {
            RootGrid.Background = null;
        }
    }

    private async Task ApplyRandomBackgroundAsync(
        int requestId,
        CancellationTokenSource cancellation)
    {
        try
        {
            Debug.WriteLine("Loading a local-first Wikimedia Commons random background.");

            RandomBackgroundEntry? entry =
                await GetNextRandomBackgroundAsync(cancellation.Token);

            if (entry is null)
            {
                Debug.WriteLine("No cached Wikimedia Commons background is available yet.");
                return;
            }

            (BitmapImage Image, byte[] Bytes)? preparedImage =
                await LoadCachedRandomBackgroundAsync(entry, cancellation.Token);

            if (preparedImage is null)
            {
                _randomBackgroundEntries.Remove(entry.Id);
                RemoveQueuedRandomBackground(entry.Id);
                SaveRandomBackgroundCache();

                if (requestId == _randomBackgroundRequestId &&
                    _settings.Mode == RandomMode &&
                    !cancellation.IsCancellationRequested)
                {
                    _ = ApplyRandomBackgroundAsync(requestId, cancellation);
                }

                return;
            }

            if (requestId != _randomBackgroundRequestId || _settings.Mode != RandomMode)
                return;

            entry.LastShownUtcTicks = DateTimeOffset.UtcNow.Ticks;

            RootGrid.Background = new ImageBrush
            {
                ImageSource = preparedImage.Value.Image,
                Stretch = Stretch.UniformToFill
            };

            SetImageMetadata(
                entry.Artist,
                entry.Description,
                entry.Name,
                entry.DateTaken,
                entry.LicenseShortName,
                entry.LicenseUrl);
            RemoveQueuedRandomBackground(entry.Id);
            SaveRandomBackgroundCache();

            Debug.WriteLine($"Displayed Wikimedia Commons background: {entry.Title}");

            _ = RefillRandomBackgroundQueueAsync();
            _ = UpdateIconForegroundAsync(preparedImage.Value.Bytes, requestId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (requestId == _randomBackgroundRequestId && _settings.Mode == RandomMode)
                Debug.WriteLine($"Failed to load a random background: {ex}");
        }
    }

    private static HttpClient CreateRandomBackgroundHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "FluentBrowser/1.0 (https://github.com/c4stration/FluentBrowser/issues)");

        return client;
    }

    private async Task<RandomBackgroundEntry?> GetNextRandomBackgroundAsync(
        CancellationToken cancellationToken)
    {
        await RefillRandomBackgroundQueueAsync(cancellationToken);
        await _randomBackgroundQueueLock.WaitAsync(cancellationToken);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RandomBackgroundEntry? unseenLocalEntry = GetNextUnshownLocalBackground();

                if (unseenLocalEntry is not null)
                {
                    StartNextRandomBackgroundPrefetch(unseenLocalEntry);
                    return unseenLocalEntry;
                }

                if (_randomBackgroundQueue.Count > 0)
                {
                    RandomBackgroundEntry candidate = _randomBackgroundQueue[0];

                    if (IsCommonsCooldownActive())
                        break;

                    bool downloaded = await EnsureRandomBackgroundDownloadedAsync(
                        candidate,
                        CancellationToken.None).WaitAsync(cancellationToken);

                    if (downloaded)
                    {
                        StartNextRandomBackgroundPrefetch(candidate);
                        return candidate;
                    }

                    // A rate limit or temporary server failure should preserve
                    // the candidate so it can be retried after the cooldown.
                    if (IsCommonsCooldownActive())
                        break;

                    // Permanent failures, such as a missing thumbnail, should
                    // not block the remaining candidates in this batch.
                    RemoveQueuedRandomBackground(candidate.Id);
                    _randomBackgroundEntries.Remove(candidate.Id);
                    SaveRandomBackgroundCache();
                    continue;
                }

                break;
            }

            // Wikimedia is unavailable or the current batch has been exhausted.
            // Reuse the least-recently-shown local image instead of making
            // repeated requests or leaving the background empty.
            return GetLeastRecentlyShownLocalBackground();
        }
        finally
        {
            _randomBackgroundQueueLock.Release();
        }
    }

    private async Task RefillRandomBackgroundQueueAsync(
        CancellationToken cancellationToken = default)
    {
        await _randomBackgroundQueueLock.WaitAsync(cancellationToken);

        try
        {
            if (_randomBackgroundQueue.Count > RandomBackgroundRefillThreshold ||
                IsCommonsCooldownActive())
            {
                return;
            }

            List<RandomBackgroundEntry> batch =
                await RequestRandomBackgroundBatchAsync(cancellationToken);

            if (batch.Count == 0)
                return;

            foreach (RandomBackgroundEntry entry in batch)
            {
                if (_randomBackgroundEntries.TryAdd(entry.Id, entry))
                    _randomBackgroundQueue.Add(entry);
            }

            SaveRandomBackgroundCache();
            StartRandomBackgroundPrefetches();
        }
        finally
        {
            _randomBackgroundQueueLock.Release();
        }
    }

    private RandomBackgroundEntry? GetNextUnshownLocalBackground()
    {
        return _randomBackgroundEntries.Values
            .Where(entry =>
                entry.LastShownUtcTicks == 0 &&
                File.Exists(GetRandomBackgroundFilePath(entry)))
            .OrderBy(entry => entry.DownloadedUtcTicks)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private RandomBackgroundEntry? GetLeastRecentlyShownLocalBackground()
    {
        return _randomBackgroundEntries.Values
            .Where(entry =>
                entry.LastShownUtcTicks > 0 &&
                File.Exists(GetRandomBackgroundFilePath(entry)))
            .OrderBy(entry => entry.LastShownUtcTicks)
            .ThenBy(entry => entry.DownloadedUtcTicks)
            .FirstOrDefault();
    }

    private void StartNextRandomBackgroundPrefetch(RandomBackgroundEntry currentEntry)
    {
        // If another downloaded, unseen image is already waiting, leave it alone.
        bool anotherUnshownImageIsReady = _randomBackgroundEntries.Values.Any(entry =>
            entry.Id != currentEntry.Id &&
            entry.LastShownUtcTicks == 0 &&
            File.Exists(GetRandomBackgroundFilePath(entry)));

        if (anotherUnshownImageIsReady || IsCommonsCooldownActive())
            return;

        RandomBackgroundEntry? nextEntry = _randomBackgroundQueue.FirstOrDefault(entry =>
            entry.Id != currentEntry.Id &&
            !File.Exists(GetRandomBackgroundFilePath(entry)));

        if (nextEntry is not null)
        {
            // Prefetch is intentionally independent of the current display
            // cancellation token. Switching backgrounds must not cancel a
            // useful download that is already in progress.
            _ = EnsureRandomBackgroundDownloadedAsync(
                nextEntry,
                CancellationToken.None);
        }
    }

    private void StartRandomBackgroundPrefetches()
    {
        // Keep the next one or two queued backgrounds ready without allowing
        // a burst of downloads when a new batch arrives.
        foreach (RandomBackgroundEntry entry in _randomBackgroundQueue
                     .Where(entry => !File.Exists(GetRandomBackgroundFilePath(entry)))
                     .Take(2))
        {
            _ = EnsureRandomBackgroundDownloadedAsync(entry, CancellationToken.None);
        }
    }

    private async Task<List<RandomBackgroundEntry>>
        RequestRandomBackgroundBatchAsync(CancellationToken cancellationToken)
    {
        if (IsCommonsCooldownActive())
            return [];

        // generator=random is cacheable at intermediary proxies. A unique
        // request id, plus explicit no-cache headers, makes every refill an
        // actual random-generator request instead of repeatedly parsing the
        // same ten page IDs and rejecting them as already known.
        const int maximumAttempts = 3;

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                string requestUri = $"{CommonsApiUrl}&requestid={Guid.NewGuid():N}";
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                request.Headers.Pragma.ParseAdd("no-cache");
                using HttpResponseMessage response = await RandomBackgroundHttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    SetCommonsCooldown(response, GetDefaultCommonsCooldown(response.StatusCode));
                    SaveRandomBackgroundCache();
                    Debug.WriteLine(
                        $"Wikimedia Commons batch request was rejected: " +
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. " +
                        $"Retry after {_commonsRetryAfterUtc:O}.");
                    return [];
                }

                await using Stream stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken);

                using JsonDocument document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken);

                List<RandomBackgroundEntry> batch =
                    ParseRandomBackgroundBatch(document.RootElement);

                Debug.WriteLine(
                    $"Received {batch.Count} new Wikimedia Commons background candidate(s) " +
                    $"from batch request {attempt}.");

                if (batch.Count > 0 || attempt == maximumAttempts)
                    return batch;

                // A valid random batch can contain only unsupported media or
                // entries already persisted locally. Ask for a fresh batch.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                SetCommonsCooldown(TimeSpan.FromSeconds(30));
                SaveRandomBackgroundCache();
                Debug.WriteLine($"Wikimedia Commons batch request timed out: {ex}");
                return [];
            }
            catch (HttpRequestException ex)
            {
                SetCommonsCooldown(TimeSpan.FromSeconds(30));
                SaveRandomBackgroundCache();
                Debug.WriteLine($"Wikimedia Commons batch request failed: {ex}");
                return [];
            }
            catch (Exception ex)
            {
                SetCommonsCooldown(TimeSpan.FromSeconds(30));
                SaveRandomBackgroundCache();
                Debug.WriteLine($"Wikimedia Commons batch request failed: {ex}");
                return [];
            }
        }

        return [];
    }

    private static bool IsCommonsCooldownActive()
    {
        lock (CommonsCooldownLock)
            return DateTimeOffset.UtcNow < _commonsRetryAfterUtc;
    }

    private static long GetCommonsRetryAfterUtcTicks()
    {
        lock (CommonsCooldownLock)
            return _commonsRetryAfterUtc.UtcDateTime.Ticks;
    }

    private static void SetCommonsCooldown(HttpResponseMessage response, TimeSpan fallback)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan delay = fallback;

        if (response.Headers.RetryAfter?.Delta is TimeSpan retryDelay &&
            retryDelay > TimeSpan.Zero)
        {
            delay = retryDelay;
        }
        else if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate &&
                 retryDate > now)
        {
            delay = retryDate - now;
        }

        SetCommonsCooldown(delay);
    }

    private static void SetCommonsCooldown(TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(1))
            delay = TimeSpan.FromSeconds(1);
        else if (delay > TimeSpan.FromDays(30))
            delay = TimeSpan.FromDays(30);

        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.Add(delay);

        lock (CommonsCooldownLock)
        {
            if (retryAfter > _commonsRetryAfterUtc)
                _commonsRetryAfterUtc = retryAfter;
        }
    }

    private static TimeSpan GetDefaultCommonsCooldown(System.Net.HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            System.Net.HttpStatusCode.TooManyRequests => TimeSpan.FromMinutes(5),
            System.Net.HttpStatusCode.ServiceUnavailable => TimeSpan.FromMinutes(1),
            System.Net.HttpStatusCode.Forbidden => TimeSpan.FromMinutes(5),
            _ when (int)statusCode >= 500 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(15)
        };
    }

    private List<RandomBackgroundEntry> ParseRandomBackgroundBatch(
        JsonElement root)
    {
        var entries = new List<RandomBackgroundEntry>();

        if (!root.TryGetProperty("query", out JsonElement query) ||
            !query.TryGetProperty("pages", out JsonElement pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (JsonElement page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("pageid", out JsonElement pageIdElement) ||
                !pageIdElement.TryGetInt64(out long pageId) ||
                !page.TryGetProperty("title", out JsonElement titleElement) ||
                string.IsNullOrWhiteSpace(titleElement.GetString()) ||
                !page.TryGetProperty("imageinfo", out JsonElement imageInfo) ||
                imageInfo.ValueKind != JsonValueKind.Array ||
                imageInfo.GetArrayLength() == 0)
            {
                continue;
            }

            JsonElement info = imageInfo[0];

            if (!info.TryGetProperty("thumburl", out JsonElement thumbnailElement) ||
                string.IsNullOrWhiteSpace(thumbnailElement.GetString()))
            {
                continue;
            }

            string id = pageId.ToString();

            if (_randomBackgroundEntries.ContainsKey(id))
                continue;

            string title = titleElement.GetString()!;

            if (!IsSupportedRandomBackgroundImage(title))
                continue;

            JsonElement metadata = info.TryGetProperty(
                    "extmetadata",
                    out JsonElement metadataElement)
                ? metadataElement
                : default;

            entries.Add(new RandomBackgroundEntry
            {
                Id = id,
                Name = GetCommonsMetadataValue(metadata, "ObjectName"),
                ThumbnailUrl = thumbnailElement.GetString()!,
                Title = title,
                DescriptionUrl = $"https://commons.wikimedia.org/?curid={id}",
                LicenseShortName = GetCommonsMetadataValue(
                    metadata,
                    "LicenseShortName"),
                LicenseUrl = GetCommonsMetadataValue(metadata, "LicenseUrl"),
                UsageTerms = GetCommonsMetadataValue(metadata, "UsageTerms"),
                Artist = GetCommonsMetadataValue(metadata, "Artist"),
                Credit = GetCommonsMetadataValue(metadata, "Credit"),
                Description = GetCommonsMetadataValue(metadata, "ImageDescription"),
                DateTaken = GetCommonsMetadataValue(metadata, "DateTimeOriginal")
            });
        }

        return entries;
    }

    private void SetImageMetadata(
        string? creator,
        string? description,
        string? name,
        string? dateTaken = null,
        string? licenseShortName = null,
        string? licenseUrl = null)
    {
        static string CleanMetadata(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string cleaned = System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(
                    value,
                    "<[^>]*>",
                    " "));

            return System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"\s+",
                " ").Trim();
        }

        string creatorText = CleanMetadata(creator);
        string descriptionText = CleanMetadata(description);

        if (string.IsNullOrWhiteSpace(descriptionText))
            descriptionText = CleanMetadata(name);

        CreatorTextBlock.Text = creatorText;
        CreatorField.Visibility = string.IsNullOrWhiteSpace(creatorText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        DescriptionTextBlock.Text = descriptionText;
        DescriptionField.Visibility = string.IsNullOrWhiteSpace(descriptionText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        string cleanedDate = CleanMetadata(dateTaken);

        string dateText = Helpers.FormatDate(cleanedDate) ?? string.Empty;

        DateTextBlock.Text = dateText;
        DateField.Visibility = string.IsNullOrWhiteSpace(dateText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        string licenseText = CleanMetadata(licenseShortName);
        bool hasLicenseUrl = Uri.TryCreate(
            licenseUrl,
            UriKind.Absolute,
            out Uri? licenseUri) &&
            (licenseUri.Scheme == Uri.UriSchemeHttp ||
             licenseUri.Scheme == Uri.UriSchemeHttps);

        LicenseRun.Text = licenseText;
        LicenseLink.NavigateUri = hasLicenseUrl ? licenseUri : null;
        LicenseField.Visibility = !string.IsNullOrWhiteSpace(licenseText) && hasLicenseUrl
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string? GetCommonsMetadataValue(
        JsonElement metadata,
        string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("value", out JsonElement text))
        {
            return null;
        }

        return text.GetString();
    }

    private static bool IsSupportedRandomBackgroundImage(string title)
    {
        string extension = Path.GetExtension(title);

        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private Task<bool> EnsureRandomBackgroundDownloadedAsync(
        RandomBackgroundEntry entry,
        CancellationToken cancellationToken)
    {
        if (File.Exists(GetRandomBackgroundFilePath(entry)))
            return Task.FromResult(true);

        lock (_randomBackgroundDownloadsLock)
        {
            if (_randomBackgroundDownloads.TryGetValue(
                    entry.Id,
                    out Task<bool>? existingDownload))
            {
                return existingDownload;
            }

            Task<bool> download = DownloadRandomBackgroundAsync(
                entry,
                cancellationToken);

            _randomBackgroundDownloads[entry.Id] = download;
            return AwaitAndRemoveRandomBackgroundDownloadAsync(entry.Id, download);
        }
    }

    private async Task<bool> AwaitAndRemoveRandomBackgroundDownloadAsync(
        string id,
        Task<bool> download)
    {
        try
        {
            return await download;
        }
        finally
        {
            lock (_randomBackgroundDownloadsLock)
            {
                if (_randomBackgroundDownloads.TryGetValue(id, out Task<bool>? current) &&
                    ReferenceEquals(current, download))
                {
                    _randomBackgroundDownloads.Remove(id);
                }
            }
        }
    }

    private async Task<bool> DownloadRandomBackgroundAsync(
        RandomBackgroundEntry entry,
        CancellationToken cancellationToken)
    {
        if (IsCommonsCooldownActive())
            return false;

        bool limiterAcquired = false;
        string? temporaryPath = null;

        try
        {
            await _randomBackgroundDownloadLimiter.WaitAsync(cancellationToken);
            limiterAcquired = true;

            // Another request may have triggered a cooldown while this download
            // was waiting for the single-download limiter.
            if (IsCommonsCooldownActive())
                return false;

            using var response = await RandomBackgroundHttpClient.GetAsync(
                entry.ThumbnailUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                    response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    (int)response.StatusCode >= 500)
                {
                    SetCommonsCooldown(
                        response,
                        GetDefaultCommonsCooldown(response.StatusCode));
                    SaveRandomBackgroundCache();
                }

                Debug.WriteLine(
                    $"Wikimedia Commons thumbnail request for '{entry.Title}' " +
                    $"failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
                return false;
            }

            byte[] imageBytes = await response.Content
                .ReadAsByteArrayAsync(cancellationToken);

            string targetPath = GetRandomBackgroundFilePath(entry);
            temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";

            Directory.CreateDirectory(Paths.RandomBackgrounds);
            await File.WriteAllBytesAsync(
                temporaryPath,
                imageBytes,
                cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
            temporaryPath = null;

            entry.DownloadedUtcTicks = DateTimeOffset.UtcNow.Ticks;
            SaveRandomBackgroundCache();

            Debug.WriteLine($"Downloaded Wikimedia Commons background: {entry.Title}");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            SetCommonsCooldown(TimeSpan.FromSeconds(30));
            SaveRandomBackgroundCache();
            Debug.WriteLine(
                $"Wikimedia Commons download for '{entry.Title}' timed out: {ex}");
            return false;
        }
        catch (HttpRequestException ex)
        {
            SetCommonsCooldown(TimeSpan.FromSeconds(30));
            SaveRandomBackgroundCache();
            Debug.WriteLine(
                $"Wikimedia Commons download for '{entry.Title}' failed: {ex}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Wikimedia Commons download for '{entry.Title}' failed: {ex}");
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch { }
            }

            if (limiterAcquired)
                _randomBackgroundDownloadLimiter.Release();
        }
    }

    private async Task<(BitmapImage Image, byte[] Bytes)?>
        LoadCachedRandomBackgroundAsync(
            RandomBackgroundEntry entry,
            CancellationToken cancellationToken)
    {
        string path = GetRandomBackgroundFilePath(entry);

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = 1920 };
            await image.SetSourceAsync(stream);

            return (image, bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Cached Wikimedia Commons image '{entry.Title}' is invalid: {ex.Message}");

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }

            return null;
        }
    }

    private string GetRandomBackgroundFilePath(RandomBackgroundEntry entry)
    {
        string extension = Path.GetExtension(entry.Title);

        if (string.IsNullOrWhiteSpace(extension))
            extension = ".jpg";

        return Path.Combine(Paths.RandomBackgrounds, entry.Id + extension);
    }

    private void RemoveQueuedRandomBackground(string id)
    {
        _randomBackgroundQueue.RemoveAll(entry => entry.Id == id);
        SaveRandomBackgroundCache();
    }

    private void LoadRandomBackgroundCache()
    {
        try
        {
            if (!File.Exists(RandomBackgroundMetadataPath))
                return;

            var data = MessagePackSerializer.Deserialize<RandomBackgroundCacheData>(
                File.ReadAllBytes(RandomBackgroundMetadataPath),
                MessagePackData.Options);

            if (data is null)
                return;

            if (data.CommonsRetryAfterUtcTicks > 0)
            {
                var cachedRetryAfter = new DateTimeOffset(
                    data.CommonsRetryAfterUtcTicks,
                    TimeSpan.Zero);

                lock (CommonsCooldownLock)
                {
                    if (cachedRetryAfter > _commonsRetryAfterUtc)
                        _commonsRetryAfterUtc = cachedRetryAfter;
                }
            }

            foreach (RandomBackgroundEntryData item in data.Entries ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Id) ||
                    string.IsNullOrWhiteSpace(item.ThumbnailUrl) ||
                    string.IsNullOrWhiteSpace(item.Title))
                {
                    continue;
                }

                _randomBackgroundEntries[item.Id] =
                    RandomBackgroundEntry.FromData(item);
            }

            foreach (string id in data.QueuedEntryIds ?? [])
            {
                if (_randomBackgroundEntries.TryGetValue(
                        id,
                        out RandomBackgroundEntry? entry))
                {
                    if (!_randomBackgroundQueue.Any(queued => queued.Id == entry.Id))
                        _randomBackgroundQueue.Add(entry);
                }
            }

            Debug.WriteLine(
                $"Restored {_randomBackgroundQueue.Count} queued Wikimedia " +
                $"Commons background(s) from cache.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to restore Wikimedia Commons background cache: {ex}");
            _randomBackgroundEntries.Clear();
            _randomBackgroundQueue.Clear();
        }
    }

    private void SaveRandomBackgroundCache()
    {
        lock (RandomBackgroundCacheLock)
        {
            try
            {
                Directory.CreateDirectory(Paths.RandomBackgrounds);
                TrimRandomBackgroundCache();

                var data = new RandomBackgroundCacheData
                {
                    Entries = _randomBackgroundEntries.Values
                        .Select(RandomBackgroundEntryData.FromEntry)
                        .ToList(),
                    QueuedEntryIds = _randomBackgroundQueue
                        .Select(entry => entry.Id)
                        .ToList(),
                    CommonsRetryAfterUtcTicks = GetCommonsRetryAfterUtcTicks()
                };

                string temporaryPath = RandomBackgroundMetadataPath + ".tmp";

                File.WriteAllBytes(
                    temporaryPath,
                    MessagePackSerializer.Serialize(data, MessagePackData.Options));
                File.Move(
                    temporaryPath,
                    RandomBackgroundMetadataPath,
                    overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Failed to save Wikimedia Commons background cache: {ex}");
            }
        }
    }

    private void TrimRandomBackgroundCache()
    {
        var queuedIds = _randomBackgroundQueue
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (RandomBackgroundEntry entry in _randomBackgroundEntries.Values
                     .Where(entry => !queuedIds.Contains(entry.Id))
                     .OrderBy(entry => entry.LastShownUtcTicks == 0
                         ? long.MaxValue
                         : entry.LastShownUtcTicks)
                     .ThenBy(entry => entry.DownloadedUtcTicks)
                     .Take(Math.Max(
                         0,
                         _randomBackgroundEntries.Count -
                             MaximumRandomBackgroundCacheEntries))
                     .ToList())
        {
            _randomBackgroundEntries.Remove(entry.Id);

            try
            {
                string path = GetRandomBackgroundFilePath(entry);

                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }

    private void SetBackgroundImage(string path, int requestId)
    {
        _ = SetBackgroundImageAsync(path, requestId);
    }

    private async Task SetBackgroundImageAsync(string path, int requestId)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);

            var image = new BitmapImage
            {
                DecodePixelWidth = 1920
            };

            await image.SetSourceAsync(stream);

            if (requestId != _randomBackgroundRequestId ||
                _settings.Mode != ImageMode)
            {
                return;
            }

            RootGrid.Background = new ImageBrush
            {
                ImageSource = image,
                Stretch = Stretch.UniformToFill
            };

            _ = UpdateIconForegroundFromFileAsync(path, requestId);
        }
        catch
        {
            if (requestId == _randomBackgroundRequestId)
                SetIconToThemeForeground();
        }
    }

    private async Task UpdateIconForegroundFromFileAsync(
        string path,
        int requestId)
    {
        try
        {
            var imageBytes = await File.ReadAllBytesAsync(path);
            await UpdateIconForegroundAsync(imageBytes, requestId);
        }
        catch
        {
            if (requestId == _randomBackgroundRequestId)
                SetIconToThemeForeground();
        }
    }

    private async Task UpdateIconForegroundAsync(
        byte[] imageBytes,
        int requestId)
    {
        try
        {
            var averageColor = await Helpers.GetAverageColorAsync(imageBytes);

            if (requestId != _randomBackgroundRequestId)
                return;

            var contrastBackground = Helpers.DarkenColor(averageColor, 0.40);
            var (blackContrast, whiteContrast) = Helpers.GetBlackAndWhiteContrastRatios(contrastBackground);

            if (blackContrast >= 3.0)
            {
                SettingsIcon.Foreground = new SolidColorBrush(Colors.Black);
                InfoIcon.Foreground = new SolidColorBrush(Colors.Black);
            }
            else if (whiteContrast >= 3.0)
            {
                SettingsIcon.Foreground = new SolidColorBrush(Colors.White);
                InfoIcon.Foreground = new SolidColorBrush(Colors.White);
            }
            else
            {
                SetIconToThemeForeground();
            }
        }
        catch
        {
            if (requestId == _randomBackgroundRequestId)
                SetIconToThemeForeground();
        }
    }

    private void SetIconToThemeForeground()
    {
        SettingsIcon.ClearValue(IconElement.ForegroundProperty);
        InfoIcon.ClearValue(IconElement.ForegroundProperty);
    }

    private void LoadBackgroundSettings()
    {
        bool rewriteSettings = false;

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var data = MessagePackSerializer.Deserialize<BackgroundSettingsData>(
                    File.ReadAllBytes(SettingsFilePath),
                    MessagePackData.Options);

                if (data is not null)
                {
                    var recentFileNames = data.RecentBackgroundFileNames ?? new();

                    rewriteSettings =
                        HasDirectory(data.SelectedBackgroundFileName) ||
                        recentFileNames.Any(HasDirectory);

                    _settings = new BackgroundSettings
                    {
                        Mode = data.Mode,
                        SelectedBackgroundPath = ResolveBackgroundPath(data.SelectedBackgroundFileName),
                        RecentBackgroundPaths = recentFileNames
                            .Select(ResolveBackgroundPath)
                            .OfType<string>()
                            .ToList()
                    };
                }
            }
            else if (File.Exists(LegacySettingsFilePath))
            {
                _settings = JsonSerializer.Deserialize<BackgroundSettings>(
                    File.ReadAllText(LegacySettingsFilePath)) ?? new();

                rewriteSettings = true;
            }
        }
        catch
        {
            _settings = new BackgroundSettings();
        }

        if (_settings.Mode == LegacySpotlightMode)
        {
            _settings.Mode = RandomMode;
        }
        else if (_settings.Mode != ImageMode &&
                 _settings.Mode != RandomMode &&
                 _settings.Mode != NoneMode)
        {
            _settings.Mode = ImageMode;
        }

        _settings.RecentBackgroundPaths ??= new();

        if (rewriteSettings)
            SaveBackgroundSettings();
    }

    private void RefreshRecentBackgrounds()
    {
        _isRefreshingRecentBackgrounds = true;

        try
        {
            var validPaths = _settings.RecentBackgroundPaths
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path) &&
                    File.Exists(path) &&
                    IsSupportedImage(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumRecentBackgrounds)
                .ToList();

            _settings.RecentBackgroundPaths = validPaths;

            if (string.IsNullOrWhiteSpace(_settings.SelectedBackgroundPath) ||
                !validPaths.Contains(
                    _settings.SelectedBackgroundPath,
                    StringComparer.OrdinalIgnoreCase))
            {
                _settings.SelectedBackgroundPath = validPaths.FirstOrDefault();
            }

            RecentBackgrounds.Clear();

            RecentBackgrounds.Add(new BackgroundImageItem
            {
                IsAddButton = true
            });

            foreach (var path in validPaths)
            {
                RecentBackgrounds.Add(new BackgroundImageItem
                {
                    FilePath = path,
                    Thumbnail = CreateBitmapImage(path, 256)
                });
            }

            RecentBackgroundsItemsView.Visibility =
                RecentBackgrounds.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            RecentBackgroundsItemsView.DeselectAll();

            var selectedIndex = validPaths.FindIndex(path =>
                string.Equals(
                    path,
                    _settings.SelectedBackgroundPath,
                    StringComparison.OrdinalIgnoreCase));

            if (selectedIndex >= 0)
            {
                RecentBackgroundsItemsView.Select(selectedIndex + 1);
            }
        }
        finally
        {
            _isRefreshingRecentBackgrounds = false;
        }
    }

    private void AddToRecentBackgrounds(string path)
    {
        _settings.RecentBackgroundPaths ??= new List<string>();
        _settings.RecentBackgroundPaths.RemoveAll(
            existingPath => string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentBackgroundPaths.Insert(0, path);

        while (_settings.RecentBackgroundPaths.Count > MaximumRecentBackgrounds)
        {
            var lastIndex = _settings.RecentBackgroundPaths.Count - 1;
            var removedPath = _settings.RecentBackgroundPaths[lastIndex];
            _settings.RecentBackgroundPaths.RemoveAt(lastIndex);

            try
            {
                if (File.Exists(removedPath))
                {
                    File.Delete(removedPath);
                }
            }
            catch { }
        }
    }

    private string? FindExistingBackground(string sourcePath)
    {
        var sourceLength = new FileInfo(sourcePath).Length;
        var sourceHash = GetImageHash(sourcePath);

        foreach (var path in Directory.EnumerateFiles(Paths.CustomBackgrounds)
                     .Where(IsSupportedImage))
        {
            try
            {
                if (new FileInfo(path).Length != sourceLength ||
                    !string.Equals(
                        GetImageHash(path),
                        sourceHash,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (FilesAreIdentical(sourcePath, path))
                {
                    return path;
                }
            }
            catch { }
        }

        return null;
    }

    private void DeleteDuplicateBackgrounds(string keepPath)
    {
        var keepLength = new FileInfo(keepPath).Length;
        var keepHash = GetImageHash(keepPath);

        foreach (var path in Directory.EnumerateFiles(Paths.CustomBackgrounds)
                     .Where(IsSupportedImage))
        {
            if (string.Equals(
                    path,
                    keepPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (new FileInfo(path).Length != keepLength ||
                    !string.Equals(
                        GetImageHash(path),
                        keepHash,
                        StringComparison.Ordinal) ||
                    !FilesAreIdentical(keepPath, path))
                {
                    continue;
                }

                File.Delete(path);

                _settings.RecentBackgroundPaths.RemoveAll(
                    existingPath => string.Equals(
                        existingPath,
                        path,
                        StringComparison.OrdinalIgnoreCase));

                if (string.Equals(
                        _settings.SelectedBackgroundPath,
                        path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _settings.SelectedBackgroundPath = keepPath;
                }
            }
            catch { }
        }
    }

    private static string GetImageHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = new XxHash3();
        hash.Append(stream);
        return Convert.ToHexString(hash.GetCurrentHash());
    }

    private static bool FilesAreIdentical(string firstPath, string secondPath)
    {
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);

        if (first.Length != second.Length)
        {
            return false;
        }

        var firstBuffer = new byte[81920];
        var secondBuffer = new byte[81920];

        while (true)
        {
            var firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
            var secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);

            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            if (!firstBuffer.AsSpan(0, firstRead)
                    .SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
            {
                return false;
            }
        }
    }

    private void SaveBackgroundSettings()
    {
        try
        {
            Directory.CreateDirectory(Paths.CustomBackgrounds);

            var data = new BackgroundSettingsData
            {
                Mode = _settings.Mode,
                SelectedBackgroundFileName = GetBackgroundFileName(
                    _settings.SelectedBackgroundPath),
                RecentBackgroundFileNames = _settings.RecentBackgroundPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => GetBackgroundFileName(path)!)
                    .ToList()
            };

            string temporaryPath = SettingsFilePath + ".tmp";

            byte[] serializedData = MessagePackSerializer.Serialize(
                data,
                MessagePackData.Options);

            File.WriteAllBytes(temporaryPath, serializedData);
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);

            if (File.Exists(LegacySettingsFilePath))
                File.Delete(LegacySettingsFilePath);
        }
        catch { }
    }

    private static BitmapImage CreateBitmapImage(string path, int decodePixelWidth)
    {
        return new BitmapImage
        {
            DecodePixelWidth = decodePixelWidth,
            UriSource = new Uri(path, UriKind.Absolute)
        };
    }

    private static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ShowErrorDialogAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Background",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };

        await dialog.ShowAsync();
    }

    public sealed class RandomBackgroundEntry
    {
        public string Id { get; init; } = string.Empty;
        public string? Name { get; init; }
        public string ThumbnailUrl { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string DescriptionUrl { get; init; } = string.Empty;
        public string? LicenseShortName { get; init; }
        public string? LicenseUrl { get; init; }
        public string? UsageTerms { get; init; }
        public string? Artist { get; init; }
        public string? Credit { get; init; }
        public string? Description { get; init; }
        public long DownloadedUtcTicks { get; set; }
        public long LastShownUtcTicks { get; set; }
        public string? DateTaken { get; init; }

        public static RandomBackgroundEntry FromData(
            RandomBackgroundEntryData data) => new()
            {
                Id = data.Id,
                Name = data.Name,
                ThumbnailUrl = data.ThumbnailUrl,
                Title = data.Title,
                DescriptionUrl = data.DescriptionUrl,
                LicenseShortName = data.LicenseShortName,
                LicenseUrl = data.LicenseUrl,
                UsageTerms = data.UsageTerms,
                Artist = data.Artist,
                Credit = data.Credit,
                Description = data.Description,
                DownloadedUtcTicks = data.DownloadedUtcTicks,
                LastShownUtcTicks = data.LastShownUtcTicks,
                DateTaken = data.DateTaken,
            };
    }

    [MessagePackObject]
    public sealed class RandomBackgroundCacheData
    {
        [Key(0)]
        public List<RandomBackgroundEntryData> Entries { get; set; } = [];

        [Key(1)]
        public List<string> QueuedEntryIds { get; set; } = [];

        [Key(2)]
        public long CommonsRetryAfterUtcTicks { get; set; }
    }

    [MessagePackObject]
    public sealed class RandomBackgroundEntryData
    {
        [Key(0)]
        public string Id { get; set; } = string.Empty;

        [Key(1)]
        public string ThumbnailUrl { get; set; } = string.Empty;

        [Key(2)]
        public string Title { get; set; } = string.Empty;

        [Key(3)]
        public string DescriptionUrl { get; set; } = string.Empty;

        [Key(4)]
        public string? LicenseShortName { get; set; }

        [Key(5)]
        public string? LicenseUrl { get; set; }

        [Key(6)]
        public string? UsageTerms { get; set; }

        [Key(7)]
        public string? Artist { get; set; }

        [Key(8)]
        public string? Credit { get; set; }

        [Key(9)]
        public string? Description { get; set; }

        [Key(10)]
        public long DownloadedUtcTicks { get; set; }

        [Key(11)]
        public long LastShownUtcTicks { get; set; }

        [Key(12)]
        public string? Name { get; set; }
        [Key(13)]
        public string? DateTaken { get; set; }

        public static RandomBackgroundEntryData FromEntry(
            RandomBackgroundEntry entry) => new()
            {
                Id = entry.Id,
                ThumbnailUrl = entry.ThumbnailUrl,
                Title = entry.Title,
                DescriptionUrl = entry.DescriptionUrl,
                LicenseShortName = entry.LicenseShortName,
                LicenseUrl = entry.LicenseUrl,
                UsageTerms = entry.UsageTerms,
                Artist = entry.Artist,
                Credit = entry.Credit,
                Description = entry.Description,
                DownloadedUtcTicks = entry.DownloadedUtcTicks,
                LastShownUtcTicks = entry.LastShownUtcTicks,
                Name = entry.Name,
                DateTaken = entry.DateTaken,
            };
    }

    [MessagePackObject]
    public sealed class BackgroundSettingsData
    {
        [Key(0)]
        public string Mode { get; set; } = ImageMode;

        [Key(1)]
        public string? SelectedBackgroundFileName { get; set; }

        [Key(2)]
        public List<string> RecentBackgroundFileNames { get; set; } = new();
    }

    private sealed class BackgroundSettings
    {
        public string Mode { get; set; } = ImageMode;
        public string? SelectedBackgroundPath { get; set; }
        public List<string> RecentBackgroundPaths { get; set; } = new();
    }
}

public sealed class BackgroundImageItem
{
    public string FilePath { get; set; } = string.Empty;
    public BitmapImage Thumbnail { get; set; } = new();
    public bool IsAddButton { get; set; }

    public Visibility AddButtonVisibility =>
        IsAddButton ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ImageVisibility =>
        IsAddButton ? Visibility.Collapsed : Visibility.Visible;
}
