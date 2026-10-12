using MessagePack;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace FluentBrowser;

internal static class CustomSitesStorage
{
    private const string SettingKey = "CustomSites";

    private static string GetPath() =>
        Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "CustomSites.msgpack");

    public static List<string> Load()
    {
        var settings = ApplicationData.Current.LocalSettings;
        string path = GetPath();

        if (File.Exists(path))
        {
            List<string> urls =
                MessagePackSerializer.Deserialize<List<string>>(
                    File.ReadAllBytes(path),
                    MessagePackData.Options) ?? [];

            settings.Values.Remove(SettingKey);
            return urls;
        }

        if (settings.Values[SettingKey] is not string json ||
            string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<string> legacyUrls =
            JsonSerializer.Deserialize<List<string>>(json) ?? [];

        Save(legacyUrls);
        return legacyUrls;
    }

    public static void Save(IEnumerable<string> urls)
    {
        string path = GetPath();
        string temporaryPath = path + ".tmp";
        byte[] data = MessagePackSerializer.Serialize(
            urls.ToList(),
            MessagePackData.Options);

        File.WriteAllBytes(temporaryPath, data);
        File.Move(temporaryPath, path, overwrite: true);

        ApplicationData.Current.LocalSettings.Values.Remove(SettingKey);
    }
}
