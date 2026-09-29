using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentBrowser;

internal sealed record HistorySuggestion(
    string DisplayText,
    Uri TargetUri,
    string Title,
    double Score);

internal sealed class HistorySuggestionProvider
{
    private readonly SemaphoreSlim _queryGate = new(1, 1);

    public async Task<IReadOnlyList<HistorySuggestion>> GetSuggestionsAsync(
        CoreWebView2 core,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<HistorySuggestion>();

        string historyPath = Path.Combine(
            core.Profile.ProfilePath,
            "History");

        if (!File.Exists(historyPath))
            return Array.Empty<HistorySuggestion>();

        bool entered = false;

        try
        {
            await _queryGate.WaitAsync(cancellationToken);
            entered = true;

            return await Task.Run(
                () => QueryHistorySnapshotWithRetry(
                    historyPath,
                    query,
                    maxResults,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<HistorySuggestion>();
        }
        finally
        {
            if (entered)
                _queryGate.Release();
        }
    }

    private static IReadOnlyList<HistorySuggestion>
        QueryHistorySnapshotWithRetry(
            string historyPath,
            string query,
            int maxResults,
            CancellationToken cancellationToken)
    {
        string snapshotDirectory = Path.Combine(
            Path.GetTempPath(),
            "FluentBrowser",
            "HistorySnapshots");

        string snapshotPath = Path.Combine(
            snapshotDirectory,
            $"History-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(snapshotDirectory);
            CopyHistorySnapshot(historyPath, snapshotPath);

            return QueryHistoryWithRetry(
                snapshotPath,
                query,
                maxResults,
                cancellationToken);
        }
        catch (IOException)
        {
            return QueryHistoryWithRetry(
                historyPath,
                query,
                maxResults,
                cancellationToken);
        }
        finally
        {
            DeleteSnapshotFile(snapshotPath);
            DeleteSnapshotFile(snapshotPath + "-wal");
            DeleteSnapshotFile(snapshotPath + "-shm");
        }
    }

    private static void CopyHistorySnapshot(
        string historyPath,
        string snapshotPath)
    {
        File.Copy(historyPath, snapshotPath, true);

        CopyCompanionFile(
            historyPath + "-wal",
            snapshotPath + "-wal");

        CopyCompanionFile(
            historyPath + "-shm",
            snapshotPath + "-shm");
    }

    private static void CopyCompanionFile(
        string sourcePath,
        string destinationPath)
    {
        if (File.Exists(sourcePath))
            File.Copy(sourcePath, destinationPath, true);
    }

    private static void DeleteSnapshotFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static IReadOnlyList<HistorySuggestion>
        QueryHistoryWithRetry(
            string historyPath,
            string query,
            int maxResults,
            CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Array.Empty<HistorySuggestion>();

            try
            {
                return QueryHistory(
                    historyPath,
                    query,
                    maxResults,
                    cancellationToken);
            }
            catch (SqliteException ex)
                when (ex.SqliteErrorCode == 5 ||
                      ex.SqliteErrorCode == 6)
            {
                if (!WaitBeforeRetry(attempt, cancellationToken))
                    return Array.Empty<HistorySuggestion>();
            }
            catch (IOException)
            {
                if (!WaitBeforeRetry(attempt, cancellationToken))
                    return Array.Empty<HistorySuggestion>();
            }
        }

        return Array.Empty<HistorySuggestion>();
    }

    private static bool WaitBeforeRetry(
        int attempt,
        CancellationToken cancellationToken)
    {
        int delay = Math.Min(
            100 + attempt * 150,
            1000);

        return !cancellationToken.WaitHandle.WaitOne(delay);
    }

    private static IReadOnlyList<HistorySuggestion> QueryHistory(
        string historyPath,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        long now = DateTimeOffset.UtcNow.ToFileTime() / 10;

        long cutoff =
            now -
            90L * 24 * 60 * 60 * 1_000_000;

        long day =
            now -
            24L * 60 * 60 * 1_000_000;

        long week =
            now -
            7L * 24 * 60 * 60 * 1_000_000;

        long month =
            now -
            30L * 24 * 60 * 60 * 1_000_000;

        var connectionString =
            new SqliteConnectionStringBuilder
            {
                DataSource = historyPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 2
            }.ToString();

        using var connection =
            new SqliteConnection(connectionString);

        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA busy_timeout = 2000;";

            pragma.ExecuteNonQuery();
        }

        using var command =
            connection.CreateCommand();

        command.CommandTimeout = 2;

        command.CommandText = """
            SELECT
                u.url,
                u.title,
                u.visit_count,
                u.typed_count,
                u.last_visit_time,

                COALESCE(
                    SUM(
                        CASE
                            WHEN v.visit_time >= $cutoff THEN
                                CASE
                                    WHEN v.visit_time >= $day THEN 1.0
                                    WHEN v.visit_time >= $week THEN 0.75
                                    WHEN v.visit_time >= $month THEN 0.45
                                    ELSE 0.20
                                END
                            ELSE 0.0
                        END
                    ),
                    0
                ) AS frecency,

                COALESCE(
                    SUM(
                        CASE
                            WHEN v.visit_time >= $cutoff
                                 AND (v.transition & 255) = 1
                            THEN
                                CASE
                                    WHEN v.visit_time >= $day THEN 1.5
                                    WHEN v.visit_time >= $week THEN 1.1
                                    WHEN v.visit_time >= $month THEN 0.7
                                    ELSE 0.3
                                END
                            ELSE 0.0
                        END
                    ),
                    0
                ) AS typed_frecency

            FROM urls u

            LEFT JOIN visits v
                ON v.url = u.id

            WHERE u.hidden = 0
              AND u.visit_count > 0

            GROUP BY
                u.id,
                u.url,
                u.title,
                u.visit_count,
                u.typed_count,
                u.last_visit_time

            ORDER BY u.last_visit_time DESC
            LIMIT 5000;
            """;

        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$week", week);
        command.Parameters.AddWithValue("$month", month);

        var candidates =
            new List<HistorySuggestionCandidate>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (cancellationToken.IsCancellationRequested)
                return Array.Empty<HistorySuggestion>();

            string rawUrl =
                reader.IsDBNull(0)
                    ? string.Empty
                    : reader.GetString(0);

            if (!Uri.TryCreate(
                    rawUrl,
                    UriKind.Absolute,
                    out Uri? uri) ||
                uri is null ||
                !IsHttpUri(uri))
            {
                continue;
            }

            string title =
                reader.IsDBNull(1)
                    ? string.Empty
                    : reader.GetString(1);

            int visitCount =
                reader.IsDBNull(2)
                    ? 0
                    : reader.GetInt32(2);

            int typedCount =
                reader.IsDBNull(3)
                    ? 0
                    : reader.GetInt32(3);

            long lastVisitValue =
                reader.IsDBNull(4)
                    ? 0
                    : reader.GetInt64(4);

            double frecency =
                reader.IsDBNull(5)
                    ? 0
                    : reader.GetDouble(5);

            double typedFrecency =
                reader.IsDBNull(6)
                    ? 0
                    : reader.GetDouble(6);

            DateTimeOffset lastVisit =
                ConvertChromiumTime(lastVisitValue);

            string? searchQuery =
                ExtractSearchQuery(uri);

            string display =
                !string.IsNullOrWhiteSpace(searchQuery)
                    ? searchQuery
                    : FormatForAddressBar(uri);

            double score =
                ScoreCandidate(
                    query,
                    uri,
                    display,
                    title,
                    searchQuery,
                    visitCount,
                    typedCount,
                    frecency,
                    typedFrecency,
                    lastVisit);

            if (score <= 0)
                continue;

            candidates.Add(
                new HistorySuggestionCandidate(
                    display,
                    uri,
                    title,
                    lastVisit,
                    score));
        }

        return candidates
            .GroupBy(
                x => x.DisplayText,
                StringComparer.OrdinalIgnoreCase)
            .Select(
                group => group
                    .OrderByDescending(x => x.Score)
                    .First())
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.LastVisit)
            .Take(Math.Max(1, maxResults))
            .Select(
                x => new HistorySuggestion(
                    x.DisplayText,
                    x.TargetUri,
                    x.Title,
                    x.Score))
            .ToArray();
    }

    private static double ScoreCandidate(
        string query,
        Uri uri,
        string display,
        string title,
        string? searchQuery,
        int visitCount,
        int typedCount,
        double frecency,
        double typedFrecency,
        DateTimeOffset lastVisit)
    {
        string q = Normalize(query);
        string normalizedDisplay = Normalize(display);
        string normalizedUri = Normalize(uri.AbsoluteUri);
        string normalizedHost = Normalize(uri.Host);
        string normalizedTitle = Normalize(title);

        string normalizedSearch =
            searchQuery is null
                ? string.Empty
                : Normalize(searchQuery);

        bool exact =
            normalizedDisplay.Equals(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool hostExact =
            normalizedHost.Equals(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool hostPrefix =
            normalizedHost.StartsWith(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool displayPrefix =
            normalizedDisplay.StartsWith(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool uriPrefix =
            normalizedUri.StartsWith(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool titlePrefix =
            normalizedTitle.StartsWith(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool titleContains =
            normalizedTitle.Contains(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool searchExact =
            !string.IsNullOrEmpty(normalizedSearch) &&
            normalizedSearch.Equals(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool searchPrefix =
            !string.IsNullOrEmpty(normalizedSearch) &&
            normalizedSearch.StartsWith(
                q,
                StringComparison.OrdinalIgnoreCase);

        bool searchContains =
            !string.IsNullOrEmpty(normalizedSearch) &&
            normalizedSearch.Contains(
                q,
                StringComparison.OrdinalIgnoreCase);

        if (!exact &&
            !hostExact &&
            !hostPrefix &&
            !displayPrefix &&
            !uriPrefix &&
            !titlePrefix &&
            !titleContains &&
            !searchExact &&
            !searchPrefix &&
            !searchContains)
        {
            return 0;
        }

        double score = 0;

        if (searchExact)
            score += 5000;

        if (exact)
            score += 4800;

        if (hostExact)
            score += 4500;

        if (searchPrefix)
            score += 3500;

        if (hostPrefix)
            score += 3200;

        if (displayPrefix)
            score += 3000;

        if (uriPrefix)
            score += 1800;

        if (titlePrefix)
            score += 1200;

        if (titleContains)
            score += 500;

        if (searchContains)
            score += 350;

        score += typedFrecency * 140;
        score += frecency * 45;

        score += Math.Min(typedCount, 100) * 4;
        score += Math.Min(visitCount, 500) * 0.5;

        double ageDays =
            Math.Max(
                0,
                (DateTimeOffset.UtcNow - lastVisit).TotalDays);

        score += Math.Max(
            0,
            300 - ageDays * 5);

        return score;
    }

    private sealed record HistorySuggestionCandidate(
        string DisplayText,
        Uri TargetUri,
        string Title,
        DateTimeOffset LastVisit,
        double Score);

    private static string? ExtractSearchQuery(Uri uri)
    {
        string host = uri.Host;

        if (host.Equals(
                "google.com",
                StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith(
                "google.",
                StringComparison.OrdinalIgnoreCase) ||
            host.Contains(
                ".google.",
                StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.Equals(
                    "/search",
                    StringComparison.OrdinalIgnoreCase))
            {
                return GetQueryParameter(uri, "q");
            }
        }

        if (host.Equals(
                "bing.com",
                StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(
                ".bing.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return GetQueryParameter(uri, "q");
        }

        if (host.Equals(
                "duckduckgo.com",
                StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(
                ".duckduckgo.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return GetQueryParameter(uri, "q");
        }

        if (host.Equals(
                "search.yahoo.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return GetQueryParameter(uri, "p");
        }

        if (host.Equals(
                "search.brave.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return GetQueryParameter(uri, "q");
        }

        return null;
    }

    private static string? GetQueryParameter(
        Uri uri,
        string parameterName)
    {
        string query =
            uri.Query.TrimStart('?');

        if (query.Length == 0)
            return null;

        foreach (string part in query.Split('&'))
        {
            int equals = part.IndexOf('=');

            string key =
                equals >= 0
                    ? part[..equals]
                    : part;

            try
            {
                if (!string.Equals(
                        Uri.UnescapeDataString(key),
                        parameterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value =
                    equals >= 0
                        ? part[(equals + 1)..]
                        : string.Empty;

                return Uri.UnescapeDataString(
                    value.Replace('+', ' '))
                    .Trim();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string Normalize(string value)
    {
        string result = value.Trim();

        if (result.StartsWith(
                "https://",
                StringComparison.OrdinalIgnoreCase))
        {
            result = result[8..];
        }
        else if (result.StartsWith(
                     "http://",
                     StringComparison.OrdinalIgnoreCase))
        {
            result = result[7..];
        }

        if (result.StartsWith(
                "www.",
                StringComparison.OrdinalIgnoreCase))
        {
            result = result[4..];
        }

        return result.TrimEnd('/');
    }

    private static string FormatForAddressBar(Uri uri)
    {
        string host = uri.Host;

        if (host.StartsWith(
                "www.",
                StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        string path =
            uri.AbsolutePath == "/"
                ? string.Empty
                : uri.AbsolutePath;

        return string.Concat(
            host,
            path,
            uri.Query,
            uri.Fragment);
    }

    private static DateTimeOffset ConvertChromiumTime(long value)
    {
        if (value <= 0)
            return DateTimeOffset.MinValue;

        try
        {
            return DateTimeOffset
                .FromFileTime(value * 10);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static bool IsHttpUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp ||
        uri.Scheme == Uri.UriSchemeHttps;
}