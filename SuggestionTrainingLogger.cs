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

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    private static string GetPath(string fileName)
    {
        return Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            fileName);
    }

    public static Task LogImpressionAsync(SuggestionImpression impression)
    {
        return AppendAsync(
            "SuggestionImpressions.jsonl",
            impression);
    }

    public static Task LogClickAsync(SuggestionClick click)
    {
        return AppendAsync(
            "SuggestionClicks.jsonl",
            click);
    }

    private static async Task AppendAsync<T>(
        string fileName,
        T value)
    {
        string json = JsonSerializer.Serialize(
            value,
            _jsonOptions);

        await _lock.WaitAsync();

        try
        {
            await File.AppendAllTextAsync(
                GetPath(fileName),
                json + Environment.NewLine);
        }
        finally
        {
            _lock.Release();
        }
    }
}