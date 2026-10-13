using MessagePack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

            string impressionsPath = GetPath("SuggestionImpressions.msgpack");
            string clicksPath = GetPath("SuggestionClicks.msgpack");

            await EnsureFramedAsync<SuggestionImpression>(impressionsPath);
            await EnsureFramedAsync<SuggestionClick>(clicksPath);

            await CompactIfNeededAsync<SuggestionImpression>(impressionsPath);
            await CompactIfNeededAsync<SuggestionClick>(clicksPath);
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

            string path = GetPath(fileName);

            await EnsureFramedAsync<T>(path);
            await CompactIfNeededAsync<T>(path);

            byte[] data = MessagePackSerializer.Serialize(
                value,
                MessagePackData.Options);

            await using (var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                useAsync: true))
            {
                if (stream.Length == 0)
                    await SuggestionTrainingLogFormat.WriteHeaderAsync(stream);

                await SuggestionTrainingLogFormat.WriteRecordAsync(
                    stream,
                    data);
            }

            await CompactIfNeededAsync<T>(path);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task EnsureFramedAsync<T>(string path)
    {
        if (!File.Exists(path))
            return;

        byte[] data = await File.ReadAllBytesAsync(path);

        if (SuggestionTrainingLogFormat.HasHeader(data))
        {
            _ = SuggestionTrainingLogFormat.Deserialize<T>(
                data,
                out int validLength);

            if (validLength < data.Length)
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    useAsync: true);

                stream.SetLength(validLength);
                await stream.FlushAsync();
            }

            return;
        }

        List<T> records = SuggestionTrainingLogFormat.Deserialize<T>(data);

        await RewriteFramedAsync(path, records);
    }

    private static async Task RewriteFramedAsync<T>(
        string path,
        IEnumerable<T> records)
    {
        string temporaryPath = path + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await SuggestionTrainingLogFormat.WriteHeaderAsync(stream);

                foreach (T record in records)
                {
                    byte[] data = MessagePackSerializer.Serialize(
                        record,
                        MessagePackData.Options);

                    await SuggestionTrainingLogFormat.WriteRecordAsync(
                        stream,
                        data);
                }

                await stream.FlushAsync();
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            throw;
        }
    }

    private static async Task CompactIfNeededAsync<T>(string path)
    {
        if (!File.Exists(path) ||
            new FileInfo(path).Length <=
                SuggestionTrainingLogFormat.MaximumFileSizeBytes)
        {
            return;
        }

        byte[] data = await File.ReadAllBytesAsync(path);
        List<T> records = SuggestionTrainingLogFormat.Deserialize<T>(data);

        var retainedRecords = new List<byte[]>();
        long retainedSize = SuggestionTrainingLogFormat.HeaderLength;

        for (int i = records.Count - 1; i >= 0; i--)
        {
            byte[] recordData = MessagePackSerializer.Serialize(
                records[i],
                MessagePackData.Options);

            long recordSize =
                SuggestionTrainingLogFormat.LengthPrefixSize +
                recordData.Length;

            if (retainedRecords.Count > 0 &&
                retainedSize + recordSize >
                    SuggestionTrainingLogFormat.CompactedFileSizeBytes)
            {
                break;
            }

            retainedRecords.Add(recordData);
            retainedSize += recordSize;

            if (retainedSize >=
                SuggestionTrainingLogFormat.CompactedFileSizeBytes)
            {
                break;
            }
        }

        retainedRecords.Reverse();

        string temporaryPath = path + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await SuggestionTrainingLogFormat.WriteHeaderAsync(stream);

                foreach (byte[] recordData in retainedRecords)
                {
                    await SuggestionTrainingLogFormat.WriteRecordAsync(
                        stream,
                        recordData);
                }

                await stream.FlushAsync();
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            throw;
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
                await SuggestionTrainingLogFormat.WriteHeaderAsync(stream);

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

                    await SuggestionTrainingLogFormat.WriteRecordAsync(
                        stream,
                        data);
                }

                await stream.FlushAsync();
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
