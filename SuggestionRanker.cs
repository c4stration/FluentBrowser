using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using Windows.Storage;

namespace FluentBrowser;

// Shared record – used by both MainWindow and the ranker
internal sealed record SuggestionCandidate(
    string Display,
    Uri Target,
    double Score,
    bool IsHistory,
    string? Title = null);

internal sealed class SuggestionRanker
{
    public sealed record RankedSuggestion(
        string Display,
        Uri Target,
        double Score,
        bool IsHistory,
        bool IsOpenTab);

    private readonly MLContext _mlContext = new(seed: 0);
    private ITransformer? _model;
    private PredictionEngine<SuggestionFeatures, SuggestionPrediction>? _engine;
    private readonly object _lock = new();

    private static readonly string ModelPath =
        Path.Combine(ApplicationData.Current.LocalFolder.Path, "suggestion-ranker.zip");

    public SuggestionRanker()
    {
        TryLoadModel();
        SuggestionModelTrainer.ModelTrained += OnModelTrained;
    }

    public IReadOnlyList<RankedSuggestion> Rank(
        IEnumerable<SuggestionCandidate> candidates,
        string query,
        IReadOnlyCollection<Uri> openTabUris)
    {
        var openSet = new HashSet<Uri>(openTabUris, new UriEqualityComparer());

        var list = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Display))
            .GroupBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .ToList();

        List<(SuggestionCandidate Candidate, double Score, bool IsOpen)> scored;

        lock (_lock)
        {
            if (_engine is not null)
            {
                scored = list.Select(c =>
                {
                    bool isOpen = openSet.Contains(c.Target);
                    var features = CreateFeatures(c, query, isOpen);
                    var pred = _engine.Predict(features);
                    return (c, (double)pred.Score, isOpen);
                }).ToList();
            }
            else
            {
                // Fallback to the heuristic you already have
                scored = list.Select(c =>
                {
                    bool isOpen = openSet.Contains(c.Target);
                    return (c, HeuristicScore(c, isOpen), isOpen);
                }).ToList();
            }
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(8)
            .Select(x => new RankedSuggestion(
                x.Candidate.Display,
                x.Candidate.Target,
                x.Score,
                x.Candidate.IsHistory,
                x.IsOpen))
            .ToList();
    }

    private static SuggestionFeatures CreateFeatures(
        SuggestionCandidate c,
        string query,
        bool isOpenTab)
    {
        string q = query.Trim().ToLowerInvariant();
        string display = c.Display.ToLowerInvariant();
        string host = c.Target.Host.ToLowerInvariant();
        string title = (c.Title ?? string.Empty).ToLowerInvariant();

        return new SuggestionFeatures
        {
            BaseScore = (float)c.Score,
            IsHistory = c.IsHistory ? 1f : 0f,
            IsOpenTab = isOpenTab ? 1f : 0f,
            DisplayLength = c.Display.Length,
            QueryLength = q.Length,
            ExactMatch = display == q ? 1f : 0f,
            PrefixMatch = display.StartsWith(q, StringComparison.Ordinal) ? 1f : 0f,
            HostPrefix =
                !string.IsNullOrEmpty(q) &&
                host.StartsWith(q, StringComparison.Ordinal)
                    ? 1f
                    : 0f,

            TitleContains =
                !string.IsNullOrEmpty(q) &&
                title.Contains(q, StringComparison.Ordinal)
                    ? 1f
                    : 0f,
                    };
    }

    private static double HeuristicScore(
     SuggestionCandidate c,
     bool isOpenTab)
    {
        double score = c.Score;

        if (isOpenTab) score += 800;
        if (c.IsHistory) score += 120;
        if (c.Display.Length < 40) score += 40;

        return score;
    }

    private void TryLoadModel()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(ModelPath))
                {
                    _model = null;
                    _engine = null;
                    return;
                }

                using FileStream stream =
                    File.OpenRead(ModelPath);

                _model = _mlContext.Model.Load(
                    stream,
                    out _);

                _engine =
                    _mlContext.Model.CreatePredictionEngine<
                        SuggestionFeatures,
                        SuggestionPrediction>(_model);
            }
            catch
            {
                _model = null;
                _engine = null;
            }
        }
    }

    private void OnModelTrained(object? sender, EventArgs e) => TryLoadModel();

    // ---------- ML.NET types ----------

    public sealed class SuggestionFeatures
    {
        public float BaseScore { get; set; }
        public float IsHistory { get; set; }
        public float IsOpenTab { get; set; }
        public float DisplayLength { get; set; }
        public float QueryLength { get; set; }
        public float ExactMatch { get; set; }
        public float PrefixMatch { get; set; }
        public float HostPrefix { get; set; }
        public float TitleContains { get; set; }
    }

    private sealed class SuggestionPrediction
    {
        [ColumnName("Score")]
        public float Score { get; set; }
    }

    private sealed class UriEqualityComparer : IEqualityComparer<Uri>
    {
        public bool Equals(Uri? x, Uri? y) =>
            x is not null && y is not null &&
            Uri.Compare(x, y,
                UriComponents.AbsoluteUri,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) == 0;

        public int GetHashCode(Uri obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AbsoluteUri);
    }
}
