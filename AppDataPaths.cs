using System.IO;
using Windows.Storage;

namespace FluentBrowser;

internal static class AppDataPaths
{
    private static readonly string LocalStatePath =
        ApplicationData.Current.LocalFolder.Path;

    private static readonly string DataDirectory =
        Path.Combine(LocalStatePath, "Data");

    private static readonly string SuggestionsDirectory =
        Path.Combine(LocalStatePath, "Suggestions");

    private static readonly object MigrationLock = new();

    private static bool _directoriesReady;

    internal static string DataFile(string fileName)
    {
        EnsureDirectories();
        return Path.Combine(DataDirectory, fileName);
    }

    internal static string SuggestionsFile(string fileName)
    {
        EnsureDirectories();
        return Path.Combine(SuggestionsDirectory, fileName);
    }

    private static void EnsureDirectories()
    {
        lock (MigrationLock)
        {
            if (_directoriesReady)
                return;

            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(SuggestionsDirectory);

            MoveLegacyFile("BrowserSession.msgpack", DataDirectory);
            MoveLegacyFile("CustomSites.msgpack", DataDirectory);

            MoveLegacyFile("SuggestionClicks.msgpack", SuggestionsDirectory);
            MoveLegacyFile("SuggestionImpressions.msgpack", SuggestionsDirectory);
            MoveLegacyFile("SuggestionClicks.jsonl", SuggestionsDirectory);
            MoveLegacyFile("SuggestionImpressions.jsonl", SuggestionsDirectory);
            MoveLegacyFile("suggestion-clicks.csv", SuggestionsDirectory);
            MoveLegacyFile("SuggestionRanker.zip", SuggestionsDirectory);
            MoveLegacyFile("suggestion-ranker.zip", SuggestionsDirectory, "SuggestionRanker.zip");

            _directoriesReady = true;
        }
    }

    private static void MoveLegacyFile(
        string fileName,
        string destinationDirectory,
        string? destinationFileName = null)
    {
        string sourcePath = Path.Combine(LocalStatePath, fileName);
        string destinationPath = Path.Combine(
            destinationDirectory,
            destinationFileName ?? fileName);

        if (!File.Exists(sourcePath) || File.Exists(destinationPath))
            return;

        File.Move(sourcePath, destinationPath);
    }
}
