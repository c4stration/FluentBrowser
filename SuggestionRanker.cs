using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentBrowser;

// Shared record – used by both MainWindow and the ranker
internal sealed record SuggestionCandidate(
    string Display,
    Uri Target,
    double Score,
    bool IsHistory);

internal sealed class SuggestionRanker
{
    public sealed record RankedSuggestion(
        string Display,
        Uri Target,
        double Score,
        bool IsHistory,
        bool IsOpenTab);

    public IReadOnlyList<RankedSuggestion> Rank(
        IEnumerable<SuggestionCandidate> candidates,
        string query,
        IReadOnlyCollection<Uri> openTabUris)
    {
        var openSet = new HashSet<Uri>(openTabUris, new UriEqualityComparer());

        var ranked = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Display))
            .GroupBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .Select(c =>
            {
                bool isOpen = openSet.Contains(c.Target);
                double finalScore = ComputeFinalScore(c, query, isOpen);

                return new RankedSuggestion(
                    c.Display,
                    c.Target,
                    finalScore,
                    c.IsHistory,
                    isOpen);
            })
            .OrderByDescending(x => x.Score)
            .Take(8)
            .ToList();

        return ranked;
    }

    private static double ComputeFinalScore(
        SuggestionCandidate c,
        string query,
        bool isOpenTab)
    {
        double score = c.Score;

        // Strong boost for open tabs (Safari / Chrome style)
        if (isOpenTab)
            score += 800;

        // Prefer history over pure search suggestions
        if (c.IsHistory)
            score += 120;

        // Slight preference for cleaner/shorter display text
        if (c.Display.Length < 40)
            score += 40;

        return score;
    }

    private sealed class UriEqualityComparer : IEqualityComparer<Uri>
    {
        public bool Equals(Uri? x, Uri? y) =>
            x is not null && y is not null &&
            Uri.Compare(
                x, y,
                UriComponents.AbsoluteUri,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) == 0;

        public int GetHashCode(Uri obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AbsoluteUri);
    }
}