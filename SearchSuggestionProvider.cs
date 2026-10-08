using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentBrowser;

internal sealed class SearchSuggestionProvider
{
    private readonly HttpClient _httpClient;

    public SearchSuggestionProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<string>> GetSuggestionsAsync(
        string query,
        string engine,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> suggestions = engine switch
        {
            "Bing" => await GetBingSuggestionsAsync(query, cancellationToken),
            "Yahoo" => await GetYahooSuggestionsAsync(query, cancellationToken),
            "DuckDuckGo" => await GetDuckDuckGoSuggestionsAsync(query, cancellationToken),
            "Google" => await GetGoogleSuggestionsAsync(query, cancellationToken),
            _ => Array.Empty<string>()
        };

        // Prefer the configured engine; fall back to Google when empty.
        if (suggestions.Count > 0 || engine == "Google")
            return suggestions;

        return await GetGoogleSuggestionsAsync(query, cancellationToken);
    }

    public Uri CreateSearchUri(string query, string engine)
    {
        string encoded = Uri.EscapeDataString(query);

        return engine switch
        {
            "Bing" => new Uri($"https://www.bing.com/search?q={encoded}"),
            "Yahoo" => new Uri($"https://search.yahoo.com/search?p={encoded}"),
            "DuckDuckGo" => new Uri($"https://duckduckgo.com/?q={encoded}"),
            _ => new Uri($"https://www.google.com/search?q={encoded}")
        };
    }

    public Uri CreateHomeUri(string engine)
    {
        return engine switch
        {
            "Bing" => new Uri("https://www.bing.com/"),
            "Yahoo" => new Uri("https://search.yahoo.com/"),
            "DuckDuckGo" => new Uri("https://duckduckgo.com/"),
            _ => new Uri("https://www.google.com/")
        };
    }

    private async Task<IReadOnlyList<string>> GetGoogleSuggestionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        string url =
            "https://suggestqueries.google.com/complete/search" +
            "?client=firefox" +
            "&hl=en" +
            $"&q={Uri.EscapeDataString(query)}";

        string? json = await GetSuggestionResponseAsync(url, cancellationToken);
        return json is null ? Array.Empty<string>() : ParsePairSuggestions(json);
    }

    private async Task<IReadOnlyList<string>> GetBingSuggestionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        string url =
            "https://www.bing.com/qbox" +
            "?query=" +
            Uri.EscapeDataString(query) +
            "&language=en-US";

        string? json = await GetSuggestionResponseAsync(url, cancellationToken);
        return json is null ? Array.Empty<string>() : ParsePairSuggestions(json);
    }

    private async Task<IReadOnlyList<string>> GetYahooSuggestionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        string url =
            "https://search.yahoo.com/sugg/gossip/gossip-us-ura/" +
            "?output=sd1" +
            "&appid=search.yahoo.com" +
            "&nresults=10" +
            "&command=" +
            Uri.EscapeDataString(query);

        string? json = await GetSuggestionResponseAsync(url, cancellationToken);
        return json is null ? Array.Empty<string>() : ParseYahooSuggestions(json);
    }

    private async Task<IReadOnlyList<string>> GetDuckDuckGoSuggestionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        string url =
            "https://duckduckgo.com/ac/" +
            "?q=" +
            Uri.EscapeDataString(query) +
            "&type=list";

        string? json = await GetSuggestionResponseAsync(url, cancellationToken);
        return json is null ? Array.Empty<string>() : ParsePairSuggestions(json);
    }

    private async Task<string?> GetSuggestionResponseAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/154.0.0.0 Safari/537.36");

            request.Headers.TryAddWithoutValidation(
                "Accept",
                "application/json,text/plain,*/*");

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ParsePairSuggestions(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array ||
                root.GetArrayLength() < 2)
            {
                return Array.Empty<string>();
            }

            JsonElement suggestions = root[1];

            if (suggestions.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return suggestions
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .OfType<string>()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ParseYahooSuggestions(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array ||
                root.GetArrayLength() == 0)
            {
                return Array.Empty<string>();
            }

            JsonElement suggestions = root[0];

            if (suggestions.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var results = new List<string>();

            foreach (JsonElement item in suggestions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array ||
                    item.GetArrayLength() == 0)
                {
                    continue;
                }

                JsonElement value = item[0];

                if (value.ValueKind != JsonValueKind.String)
                    continue;

                string? suggestion = value.GetString();

                if (!string.IsNullOrWhiteSpace(suggestion))
                    results.Add(suggestion.Trim());
            }

            return results
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
