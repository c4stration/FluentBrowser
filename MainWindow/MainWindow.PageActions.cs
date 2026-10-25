using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Linq;
using WinUI3Localizer;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private void PrintMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SelectedWebView?.CoreWebView2?.ShowPrintUI(
            CoreWebView2PrintDialogKind.Browser);

    private void ViewSourceMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedWebView?.Source is Uri uri)
            OpenNewTab($"view-source:{uri}");
    }

    private void DevToolsMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SelectedWebView?.CoreWebView2?.OpenDevToolsWindow();

    private async void FavoriteWebsiteItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SelectedWebView is not WebView2 webView ||
            webView.Source is not Uri uri)
        {
            return;
        }

        var existing = Favorites.FirstOrDefault(f =>
            string.Equals(f.Url, uri.AbsoluteUri, StringComparison.Ordinal));

        if (existing is not null)
        {
            Favorites.Remove(existing);
            SaveFavorites();
            return;
        }

        string tabName = SelectedTab?.Title.Text ?? uri.Host;

        if (string.IsNullOrWhiteSpace(tabName))
            tabName = uri.Host;

        var nameTextBox = new TextBox
        {
            Header = LocalizerExtensions.GetLocalizedString("Name"),
            PlaceholderText = SelectedTab?.Title.Text ?? string.Empty
        };

        var dialog = new ContentDialog
        {
            Title = LocalizerExtensions.GetLocalizedString("FavoriteTitle"),
            Content = nameTextBox,
            PrimaryButtonText = LocalizerExtensions.GetLocalizedString("Add"),
            CloseButtonText = LocalizerExtensions.GetLocalizedString("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = webView.XamlRoot,
            Style = (Style)Application.Current.Resources[
                "FixedContentDialogStyle"]
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        string name = string.IsNullOrWhiteSpace(nameTextBox.Text)
            ? tabName
            : nameTextBox.Text.Trim();

        Favorites.Add(new FavoriteWebsite
        {
            Name = name,
            Url = uri.AbsoluteUri
        });

        SaveFavorites();
    }

    private void MoreMenuFlyout_Opening(object sender, object e)
    {
        var isFavorited =
            Uri.TryCreate(
                SelectedWebView?.CoreWebView2?.Source,
                UriKind.Absolute,
                out var currentUri) &&
            Favorites.Any(favorite =>
                Uri.TryCreate(favorite.Url, UriKind.Absolute, out var favoriteUri) &&
                Uri.Compare(
                    favoriteUri,
                    currentUri,
                    UriComponents.AbsoluteUri,
                    UriFormat.SafeUnescaped,
                    StringComparison.Ordinal) == 0);

        FavoriteWebsiteItem.Text = LocalizerExtensions.GetLocalizedString(
            isFavorited ? "RemoveFavorite" : "Favorite");
        FavoriteWebsiteItemIcon.Glyph = isFavorited ? "\uE8D9" : "\uE734";
    }
}
