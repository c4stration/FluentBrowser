using FluentBrowser.Shared;
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
using Windows.Storage.Streams;
using Windows.UI;
using WinUI3Localizer;

namespace FluentBrowser.Pages;

public sealed partial class Home : Page
{
    private const string ImageMode = "Image";
    private const string RandomMode = "Random";
    private const string LegacySpotlightMode = "Spotlight";
    private const string NoneMode = "None";
    private const int MaximumRecentBackgrounds = 12;
    private static readonly HttpClient RandomBackgroundHttpClient = new();
    private bool _isRefreshingRecentBackgrounds;
    private CancellationTokenSource? _randomBackgroundLoadCancellation;
    private int _randomBackgroundRequestId;

    private readonly string _backgroundsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentBrowser",
        "Backgrounds");

    private bool _isUpdatingMode;
    private BackgroundSettings _settings = new();

    public ObservableCollection<BackgroundImageItem> RecentBackgrounds { get; } = new();

    public Home()
    {
        InitializeComponent();

        BackgroundModeComboBox.SelectionChanged += BackgroundModeComboBox_SelectionChanged;

        Loaded += Home_Loaded;
        Unloaded += Home_Unloaded;

        LoadBackgroundSettings();
        RefreshRecentBackgrounds();
        SynchronizeModeControls();
        UpdateBackgroundModeCards();
        ApplyCurrentBackground();
        SetRandomBackgroundDescription();
    }

    private void Home_Loaded(object sender, RoutedEventArgs e)
    {
        var localizer = Localizer.Get();

        localizer.LanguageChanged -= OnLanguageChanged;
        localizer.LanguageChanged += OnLanguageChanged;

        SetRandomBackgroundDescription();
    }

    private void Home_Unloaded(object sender, RoutedEventArgs e)
    {
        Localizer.Get().LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(
        object? sender,
        LanguageChangedEventArgs e)
    {
        SetRandomBackgroundDescription();
    }

    private string SettingsFilePath => Path.Combine(_backgroundsDirectory, "settings.json");

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

            Directory.CreateDirectory(_backgroundsDirectory);

            var destinationPath = FindExistingBackground(pickedFile.Path);

            if (destinationPath is null)
            {
                destinationPath = Path.Combine(
                    _backgroundsDirectory,
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
            NavigateUri = new Uri("https://picsum.photos/")
        };

        hyperlink.Inlines.Add(new Run { Text = "Lorem Picsum" });
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
        var requestId = ++_randomBackgroundRequestId;
        SetSettingsIconToThemeForeground();

        if (_settings.Mode == NoneMode)
        {
            RootGrid.Background = null;
            return;
        }

        if (_settings.Mode == RandomMode)
        {
            RootGrid.Background = null;
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
            var imageUri = new Uri(
                $"https://picsum.photos/1920/1080?random={Guid.NewGuid():N}");

            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.Pragma.ParseAdd("no-cache");

            using var response = await RandomBackgroundHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token);
            response.EnsureSuccessStatusCode();

            var imageBytes = await response.Content.ReadAsByteArrayAsync(cancellation.Token);

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);

            if (requestId != _randomBackgroundRequestId || _settings.Mode != RandomMode)
            {
                return;
            }

            RootGrid.Background = new ImageBrush
            {
                ImageSource = image,
                Stretch = Stretch.UniformToFill
            };

            _ = UpdateSettingsIconForegroundAsync(imageBytes, requestId);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (requestId == _randomBackgroundRequestId && _settings.Mode == RandomMode)
            {
                Debug.WriteLine("failed to load random image");
            }
        }
        finally
        {
            if (ReferenceEquals(_randomBackgroundLoadCancellation, cancellation))
            {
                _randomBackgroundLoadCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void SetBackgroundImage(string path, int requestId)
    {
        try
        {
            var image = new BitmapImage
            {
                DecodePixelWidth = 1920,
                UriSource = new Uri(path, UriKind.Absolute)
            };

            RootGrid.Background = new ImageBrush
            {
                ImageSource = image,
                Stretch = Stretch.UniformToFill
            };

            _ = UpdateSettingsIconForegroundFromFileAsync(path, requestId);
        }
        catch
        {
            RootGrid.Background = null;
            SetSettingsIconToThemeForeground();
        }
    }

    private async Task UpdateSettingsIconForegroundFromFileAsync(
        string path,
        int requestId)
    {
        try
        {
            var imageBytes = await File.ReadAllBytesAsync(path);
            await UpdateSettingsIconForegroundAsync(imageBytes, requestId);
        }
        catch
        {
            if (requestId == _randomBackgroundRequestId)
                SetSettingsIconToThemeForeground();
        }
    }

    private async Task UpdateSettingsIconForegroundAsync(
        byte[] imageBytes,
        int requestId)
    {
        try
        {
            var averageColor = await Helpers.GetAverageColorAsync(imageBytes);

            if (requestId != _randomBackgroundRequestId)
                return;

            var contrastBackground = Helpers.DarkenColor(averageColor, 0.30);
            var (blackContrast, whiteContrast) = Helpers.GetBlackAndWhiteContrastRatios(contrastBackground);

            if (blackContrast >= 3.0)
            {
                SettingsIcon.Foreground = new SolidColorBrush(Colors.Black);
            }
            else if (whiteContrast >= 3.0)
            {
                SettingsIcon.Foreground = new SolidColorBrush(Colors.White);
            }
            else
            {
                SetSettingsIconToThemeForeground();
            }
        }
        catch
        {
            if (requestId == _randomBackgroundRequestId)
                SetSettingsIconToThemeForeground();
        }
    }

    private void SetSettingsIconToThemeForeground()
    {
        SettingsIcon.ClearValue(IconElement.ForegroundProperty);
    }

    private void LoadBackgroundSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                _settings = JsonSerializer.Deserialize<BackgroundSettings>(File.ReadAllText(SettingsFilePath))
                    ?? new BackgroundSettings();
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

        _settings.RecentBackgroundPaths ??= new List<string>();
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

        foreach (var path in Directory.EnumerateFiles(_backgroundsDirectory)
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

        foreach (var path in Directory.EnumerateFiles(_backgroundsDirectory)
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
            Directory.CreateDirectory(_backgroundsDirectory);
            File.WriteAllText(
                SettingsFilePath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // The current selection still works for this session if persistence fails.
        }
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