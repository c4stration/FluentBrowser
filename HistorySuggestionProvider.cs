using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

using System;
using System.Collections.Generic;
using System.IO;
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
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _queryGate = new(1, 1);

    private string? _cachedHistoryPath;
    private DateTimeOffset _cacheExpiresAt;
    private HistorySuggestionCandidate[] _cachedCandidates =
        Array.Empty<HistorySuggestionCandidate>();

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

            if (cancellationToken.IsCancellationRequested)
                return Array.Empty<HistorySuggestion>();

            if (!string.Equals(
                    _cachedHistoryPath,
                    historyPath,
                    StringComparison.OrdinalIgnoreCase) ||
                DateTimeOffset.UtcNow >= _cacheExpiresAt)
            {
                // Complete the refresh even if this keystroke becomes stale.
                // The next request can then reuse the populated cache.
                HistorySuggestionCandidate[] candidates = await Task.Run(
                    () => QueryHistorySnapshotWithRetry(
                        historyPath,
                        CancellationToken.None),
                    CancellationToken.None);

                _cachedHistoryPath = historyPath;
                _cachedCandidates = candidates;
                _cacheExpiresAt = DateTimeOffset.UtcNow + CacheDuration;
            }

            if (cancellationToken.IsCancellationRequested)
                return Array.Empty<HistorySuggestion>();

            return RankCandidates(
                _cachedCandidates,
                query,
                maxResults,
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

    private static HistorySuggestionCandidate[]
        QueryHistorySnapshotWithRetry(
            string historyPath,
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
                cancellationToken);
        }
        catch (IOException)
        {
            return QueryHistoryWithRetry(
                historyPath,
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

    private static HistorySuggestionCandidate[]
        QueryHistoryWithRetry(
            string historyPath,
            CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Array.Empty<HistorySuggestionCandidate>();

            try
            {
                return QueryHistory(
                    historyPath,
                    cancellationToken);
            }
            catch (SqliteException ex)
                when (ex.SqliteErrorCode == 5 ||
                      ex.SqliteErrorCode == 6)
            {
                if (!WaitBeforeRetry(attempt, cancellationToken))
                    return Array.Empty<HistorySuggestionCandidate>();
            }
            catch (IOException)
            {
                if (!WaitBeforeRetry(attempt, cancellationToken))
                    return Array.Empty<HistorySuggestionCandidate>();
            }
        }

        return Array.Empty<HistorySuggestionCandidate>();
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

    private static HistorySuggestionCandidate[] QueryHistory(
        string historyPath,
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

        // Limit URLs before joining visits, and ignore visits outside the
        // frecency window because they contribute zero to both aggregates.
        command.CommandText = """
            WITH recent_urls AS (
                SELECT
                    id,
                    url,
                    title,
                    visit_count,
                    typed_count,
                    last_visit_time
                FROM urls
                WHERE hidden = 0
                  AND visit_count > 0
                ORDER BY last_visit_time DESC
                LIMIT 5000
            )
            SELECT
                u.url,
                u.title,
                u.visit_count,
                u.typed_count,
                u.last_visit_time,

                COALESCE(
                    SUM(
                        CASE
                            WHEN v.visit_time IS NULL THEN 0.0
                            WHEN v.visit_time >= $day THEN 1.0
                            WHEN v.visit_time >= $week THEN 0.75
                            WHEN v.visit_time >= $month THEN 0.45
                            ELSE 0.20
                        END
                    ),
                    0
                ) AS frecency,

                COALESCE(
                    SUM(
                        CASE
                            WHEN v.visit_time IS NOT NULL
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

            FROM recent_urls u

            LEFT JOIN visits v
                ON v.url = u.id
               AND v.visit_time >= $cutoff

            GROUP BY
                u.id,
                u.url,
                u.title,
                u.visit_count,
                u.typed_count,
                u.last_visit_time

            ORDER BY u.last_visit_time DESC;
            """;

        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$week", week);
        command.Parameters.AddWithValue("$month", month);

        var candidates = new List<HistorySuggestionCandidate>(5000);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (cancellationToken.IsCancellationRequested)
                return Array.Empty<HistorySuggestionCandidate>();

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

            string? searchQuery = ExtractSearchQuery(uri);

            string display =
                !string.IsNullOrWhiteSpace(searchQuery)
                    ? searchQuery
                    : FormatForAddressBar(uri);

            candidates.Add(
                new HistorySuggestionCandidate(
                    display,
                    uri,
                    title,
                    lastVisit,
                    visitCount,
                    typedCount,
                    frecency,
                    typedFrecency,
                    Normalize(display),
                    Normalize(uri.AbsoluteUri),
                    Normalize(uri.Host),
                    Normalize(title),
                    searchQuery is null
                        ? string.Empty
                        : Normalize(searchQuery)));
        }

        return candidates.ToArray();
    }

    private static IReadOnlyList<HistorySuggestion> RankCandidates(
        HistorySuggestionCandidate[] candidates,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        string normalizedQuery = Normalize(query);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var bestByDisplay = new Dictionary<string, ScoredHistorySuggestion>(
            Math.Min(candidates.Length, 256),
            StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < candidates.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Array.Empty<HistorySuggestion>();

            HistorySuggestionCandidate candidate = candidates[i];
            double score = ScoreCandidate(normalizedQuery, candidate, now);

            if (score <= 0)
                continue;

            if (bestByDisplay.TryGetValue(
                    candidate.DisplayText,
                    out ScoredHistorySuggestion existing))
            {
                if (score <= existing.Score)
                    continue;

                bestByDisplay[candidate.DisplayText] =
                    new ScoredHistorySuggestion(
                        candidate,
                        score,
                        existing.FirstIndex);
            }
            else
            {
                bestByDisplay.Add(
                    candidate.DisplayText,
                    new ScoredHistorySuggestion(candidate, score, i));
            }
        }

        if (bestByDisplay.Count == 0)
            return Array.Empty<HistorySuggestion>();

        var ranked = new List<ScoredHistorySuggestion>(bestByDisplay.Values);

        ranked.Sort(static (left, right) =>
        {
            int scoreComparison = right.Score.CompareTo(left.Score);

            if (scoreComparison != 0)
                return scoreComparison;

            int visitComparison =
                right.Candidate.LastVisit.CompareTo(left.Candidate.LastVisit);

            return visitComparison != 0
                ? visitComparison
                : left.FirstIndex.CompareTo(right.FirstIndex);
        });

        int resultCount = Math.Min(
            Math.Max(1, maxResults),
            ranked.Count);

        var results = new HistorySuggestion[resultCount];

        for (int i = 0; i < resultCount; i++)
        {
            ScoredHistorySuggestion item = ranked[i];

            results[i] = new HistorySuggestion(
                item.Candidate.DisplayText,
                item.Candidate.TargetUri,
                item.Candidate.Title,
                item.Score);
        }

        return results;
    }

    private static double ScoreCandidate(
        string query,
        HistorySuggestionCandidate candidate,
        DateTimeOffset now)
    {
        string normalizedDisplay = candidate.NormalizedDisplay;
        string normalizedUri = candidate.NormalizedUri;
        string normalizedHost = candidate.NormalizedHost;
        string normalizedTitle = candidate.NormalizedTitle;
        string normalizedSearch = candidate.NormalizedSearch;

        bool exact =
            normalizedDisplay.Equals(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool hostExact =
            normalizedHost.Equals(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool hostPrefix =
            normalizedHost.StartsWith(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool displayPrefix =
            normalizedDisplay.StartsWith(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool uriPrefix =
            normalizedUri.StartsWith(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool titlePrefix =
            normalizedTitle.StartsWith(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool titleContains =
            normalizedTitle.Contains(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool searchExact =
            normalizedSearch.Length > 0 &&
            normalizedSearch.Equals(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool searchPrefix =
            normalizedSearch.Length > 0 &&
            normalizedSearch.StartsWith(
                query,
                StringComparison.OrdinalIgnoreCase);

        bool searchContains =
            normalizedSearch.Length > 0 &&
            normalizedSearch.Contains(
                query,
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

        score += candidate.TypedFrecency * 140;
        score += candidate.Frecency * 45;

        score += Math.Min(candidate.TypedCount, 100) * 4;
        score += Math.Min(candidate.VisitCount, 500) * 0.5;

        double ageDays =
            Math.Max(
                0,
                (now - candidate.LastVisit).TotalDays);

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
        int VisitCount,
        int TypedCount,
        double Frecency,
        double TypedFrecency,
        string NormalizedDisplay,
        string NormalizedUri,
        string NormalizedHost,
        string NormalizedTitle,
        string NormalizedSearch);

    private readonly record struct ScoredHistorySuggestion(
        HistorySuggestionCandidate Candidate,
        double Score,
        int FirstIndex);

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