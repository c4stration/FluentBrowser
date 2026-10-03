using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace FluentBrowser;

public static class SuggestionModelTrainer
{
    private static readonly SemaphoreSlim _trainingLock = new(1, 1);

    public static event EventHandler? ModelTrained;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Queues model training without tying it to the UI synchronization context.
    /// Duplicate requests while a training run is in progress are ignored.
    /// </summary>
    public static Task TrainInBackgroundAsync() =>
        Task.Run(TrainIfNeededAsync);

    private static async Task TrainIfNeededAsync()
    {
        if (!await _trainingLock.WaitAsync(0))
            return;

        try
        {
            await TrainAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Suggestion model training failed: {ex}");
        }
        finally
        {
            _trainingLock.Release();
        }
    }

    private static async Task TrainAsync()
    {
        string localPath =
            ApplicationData.Current.LocalFolder.Path;

        string impressionsPath = Path.Combine(
            localPath,
            "SuggestionImpressions.jsonl");

        string clicksPath = Path.Combine(
            localPath,
            "SuggestionClicks.jsonl");

        if (!File.Exists(impressionsPath) ||
            !File.Exists(clicksPath))
        {
            return;
        }

        var impressions = await ReadJsonLinesAsync<SuggestionImpression>(
            impressionsPath);

        var clicks = await ReadJsonLinesAsync<SuggestionClick>(
            clicksPath);

        var clickedCandidates = clicks
            .GroupBy(x => x.GroupId)
            .ToDictionary(
                x => x.Key,
                x => x.Last().CandidateId);

        var groups = impressions
            .GroupBy(x => x.GroupId)
            .Where(x =>
                x.Count() >= 2 &&
                clickedCandidates.ContainsKey(x.Key))
            .ToList();

        if (groups.Count < 10)
            return;

        var groupIds = groups
            .Select((group, index) => new
            {
                group.Key,
                NumericId = (uint)(index + 1)
            })
            .ToDictionary(
                x => x.Key,
                x => x.NumericId);

        var rows = new List<SuggestionTrainingRow>();

        foreach (var group in groups)
        {
            string selectedCandidate =
                clickedCandidates[group.Key];

            uint numericGroupId =
                groupIds[group.Key];

            foreach (var impression in group)
            {
                rows.Add(
                    new SuggestionTrainingRow
                    {
                        GroupId = numericGroupId,

                        Label =
                            impression.CandidateId ==
                            selectedCandidate
                                ? 1f
                                : 0f,

                        BaseScore = impression.BaseScore,
                        IsHistory = impression.IsHistory,
                        IsOpenTab = impression.IsOpenTab,
                        DisplayLength = impression.DisplayLength,
                        QueryLength = impression.QueryLength,
                        ExactMatch = impression.ExactMatch,
                        PrefixMatch = impression.PrefixMatch,
                        HostPrefix = impression.HostPrefix,
                        TitleContains = impression.TitleContains
                    });
            }
        }

        rows = rows
            .OrderBy(x => x.GroupId)
            .ToList();

        var mlContext = new MLContext(seed: 42);

        IDataView data =
            mlContext.Data.LoadFromEnumerable(rows);

        IEstimator<ITransformer> pipeline =
            mlContext.Transforms.Concatenate(
                "Features",
                nameof(SuggestionTrainingRow.BaseScore),
                nameof(SuggestionTrainingRow.IsHistory),
                nameof(SuggestionTrainingRow.IsOpenTab),
                nameof(SuggestionTrainingRow.DisplayLength),
                nameof(SuggestionTrainingRow.QueryLength),
                nameof(SuggestionTrainingRow.ExactMatch),
                nameof(SuggestionTrainingRow.PrefixMatch),
                nameof(SuggestionTrainingRow.HostPrefix),
                nameof(SuggestionTrainingRow.TitleContains))
            .Append(
                mlContext.Transforms.Conversion.MapValueToKey(
                    "GroupId"))
            .Append(
                mlContext.Ranking.Trainers.LightGbm(
                    labelColumnName:
                        nameof(SuggestionTrainingRow.Label),
                    featureColumnName:
                        "Features",
                    rowGroupColumnName:
                        nameof(SuggestionTrainingRow.GroupId),
                    numberOfLeaves: 31,
                    minimumExampleCountPerLeaf: 10,
                    learningRate: 0.05,
                    numberOfIterations: 100));

        ITransformer model =
            pipeline.Fit(data);

        string temporaryPath = Path.Combine(
            localPath,
            "SuggestionRanker.tmp.zip");

        string modelPath = Path.Combine(
            localPath,
            "SuggestionRanker.zip");

        await Task.Run(() =>
        {
            using var stream =
                File.Create(temporaryPath);

            mlContext.Model.Save(
                model,
                data.Schema,
                stream);
        });

        File.Move(
            temporaryPath,
            modelPath,
            overwrite: true);

        ModelTrained?.Invoke(null, EventArgs.Empty);
    }

    private static async Task<List<T>> ReadJsonLinesAsync<T>(
        string path)
    {
        var result = new List<T>();

        foreach (string line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            T? value =
                JsonSerializer.Deserialize<T>(
                    line,
                    _jsonOptions);

            if (value != null)
                result.Add(value);
        }

        return result;
    }
}
