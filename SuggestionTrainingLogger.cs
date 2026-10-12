using MessagePack;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentBrowser;

public static class SuggestionTrainingLogger
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static string GetPath(string fileName) =>
        AppDataPaths.SuggestionsFile(fileName);

    public static Task LogImpressionAsync(SuggestionImpression impression) =>
        AppendAsync(
            "SuggestionImpressions.msgpack",
            "SuggestionImpressions.jsonl",
            impression);

    public static Task LogClickAsync(SuggestionClick click) =>
        AppendAsync(
            "SuggestionClicks.msgpack",
            "SuggestionClicks.jsonl",
            click);

    public static async Task EnsureMessagePackLogsAsync()
    {
        await _lock.WaitAsync();

        try
        {
            await MigrateJsonLinesAsync<SuggestionImpression>(
                "SuggestionImpressions.jsonl",
                "SuggestionImpressions.msgpack");

            await MigrateJsonLinesAsync<SuggestionClick>(
                "SuggestionClicks.jsonl",
                "SuggestionClicks.msgpack");
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task AppendAsync<T>(
        string fileName,
        string legacyFileName,
        T value)
    {
        await _lock.WaitAsync();

        try
        {
            await MigrateJsonLinesAsync<T>(
                legacyFileName,
                fileName);

            byte[] data = MessagePackSerializer.Serialize(
                value,
                MessagePackData.Options);

            await using var stream = new FileStream(
                GetPath(fileName),
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: true);

            await stream.WriteAsync(data);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task MigrateJsonLinesAsync<T>(
        string legacyFileName,
        string fileName)
    {
        string legacyPath = GetPath(legacyFileName);
        string path = GetPath(fileName);

        if (!File.Exists(legacyPath))
            return;

        // If the target already exists, the migration completed before a
        // previous interruption and only legacy-file cleanup remains.
        if (File.Exists(path))
        {
            File.Delete(legacyPath);
            return;
        }

        string temporaryPath = path + ".tmp";

        try
        {
            string[] lines = await File.ReadAllLinesAsync(legacyPath);

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    T? value = JsonSerializer.Deserialize<T>(line);

                    if (value is null)
                        continue;

                    byte[] data = MessagePackSerializer.Serialize(
                        value,
                        MessagePackData.Options);

                    await stream.WriteAsync(data);
                }
            }

            File.Move(temporaryPath, path);
            File.Delete(legacyPath);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            throw;
        }
    }
}
