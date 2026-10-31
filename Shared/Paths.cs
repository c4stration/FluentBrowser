using System.IO;
using Windows.Storage;

namespace FluentBrowser.Shared;

public static class Paths
{
    public static readonly string FaviconCache = Path.Combine(
        ApplicationData.Current.LocalCacheFolder.Path,
        "Favicons");

    public static readonly string CustomBackgrounds = Path.Combine(
        ApplicationData.Current.LocalFolder.Path,
        "Backgrounds");

    public static readonly string RandomBackgrounds = Path.Combine(
        ApplicationData.Current.LocalFolder.Path,
        "RandomBackgrounds");
}
