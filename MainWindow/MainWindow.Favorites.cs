using MessagePack;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WinUI3Localizer;

namespace FluentBrowser;

[MessagePackObject]
public sealed class FavoriteWebsite : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _url = string.Empty;
    private ImageSource? _favicon;

    [Key(0)]
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;

            _name = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    [Key(1)]
    public string Url
    {
        get => _url;
        set
        {
            if (_url == value)
                return;

            _url = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Url)));
        }
    }

    [JsonIgnore]
    [IgnoreMember]
    public ImageSource? Favicon
    {
        get => _favicon;
        set
        {
            if (ReferenceEquals(_favicon, value))
                return;

            _favicon = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Favicon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MainWindow
{
    private const string FavoritesSettingKey = "Favorites";
    private string FavoritesFile =>
        AppDataPaths.DataFile("Favorites.msgpack");

    public ObservableCollection<FavoriteWebsite> Favorites { get; } = [];

    private void LoadFavorites()
    {
        string path = FavoritesFile;

        if (File.Exists(path))
        {
            try
            {
                List<FavoriteWebsite>? favorites =
                    MessagePackSerializer.Deserialize<List<FavoriteWebsite>>(
                        File.ReadAllBytes(path),
                        MessagePackData.Options);

                if (favorites is not null)
                {
                    AddLoadedFavorites(favorites);
                    _settings.Values.Remove(FavoritesSettingKey);
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load favorites: {ex}");
            }
        }

        if (_settings.Values[FavoritesSettingKey] is not string json ||
            string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            List<FavoriteWebsite>? favorites =
                JsonSerializer.Deserialize<List<FavoriteWebsite>>(json);

            if (favorites is null)
                return;

            AddLoadedFavorites(favorites);
            SaveFavorites();
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"failed to migrate favorites: {ex}");
        }
    }

    private void AddLoadedFavorites(
        IEnumerable<FavoriteWebsite> favorites)
    {
        foreach (FavoriteWebsite? favorite in favorites)
        {
            if (favorite is not null &&
                !string.IsNullOrWhiteSpace(favorite.Name) &&
                TryCreateAddressUri(favorite.Url, out Uri? uri) &&
                uri is not null)
            {
                favorite.Url = uri.AbsoluteUri;
                Favorites.Add(favorite);
            }
        }
    }

    private void SaveFavorites()
    {
        string path = FavoritesFile;
        string temporaryPath = path + ".tmp";

        try
        {
            byte[] data = MessagePackSerializer.Serialize(
                Favorites.ToList(),
                MessagePackData.Options);

            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, path, overwrite: true);

            _settings.Values.Remove(FavoritesSettingKey);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"failed to save favorites: {ex}");
        }
    }

    private void FavoritedWebsite_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not FavoriteWebsite favorite ||
            SelectedTab is not BrowserTab tab ||
            !Uri.TryCreate(
                favorite.Url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return;
        }

        Navigate(tab, uri);
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: FavoriteWebsite favorite })
        {
            Favorites.Remove(favorite);
            SaveFavorites();
        }
    }

    private async void EditFavorite_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem
            {
                DataContext: FavoriteWebsite favorite
            })
        {
            return;
        }

        var nameTextBox = new TextBox
        {
            Header = LocalizerExtensions.GetLocalizedString("Name"),
            Text = favorite.Name,
            PlaceholderText = favorite.Name
        };

        var urlTextBox = new TextBox
        {
            Header = "URL",
            Text = favorite.Url,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            PlaceholderText = favorite.Url
        };

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
        {
            nameTextBox,
            urlTextBox
        }
        };

        var dialog = new ContentDialog
        {
            Title = LocalizerExtensions.GetLocalizedString("EditFavoriteTitle"),
            Content = content,
            PrimaryButtonText = LocalizerExtensions.GetLocalizedString("SaveButton"),
            CloseButtonText = LocalizerExtensions.GetLocalizedString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = MainTabView.XamlRoot,
            Style = (Style)Application.Current.Resources[
                "FixedContentDialogStyle"]
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        string name = string.IsNullOrWhiteSpace(nameTextBox.Text)
            ? favorite.Name
            : nameTextBox.Text.Trim();

        string url = urlTextBox.Text.Trim();

        if (!TryCreateAddressUri(url, out Uri? uri) || uri is null)
            return;

        bool urlChanged = !string.Equals(
            favorite.Url,
            uri.AbsoluteUri,
            StringComparison.Ordinal);

        favorite.Name = name;

        if (urlChanged)
        {
            favorite.Url = uri.AbsoluteUri;
            favorite.Favicon = null;

            _ = LoadFavoriteFaviconAsync(favorite);
        }

        SaveFavorites();
    }

    private void Favorites_CollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
            return;

        foreach (var item in e.NewItems)
        {
            if (item is FavoriteWebsite { Favicon: null } favorite)
                _ = LoadFavoriteFaviconAsync(favorite);
        }
    }

    private async Task LoadFavoriteFaviconAsync(FavoriteWebsite favorite)
    {
        if (!Uri.TryCreate(
                favorite.Url,
                UriKind.Absolute,
                out var uri))
        {
            return;
        }

        try
        {
            var favicon = await GetSuggestionFaviconAsync(uri);

            if (favicon is not null && Favorites.Contains(favorite))
                favorite.Favicon = favicon;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to load favorite favicon: {ex}");
        }
    }
}
