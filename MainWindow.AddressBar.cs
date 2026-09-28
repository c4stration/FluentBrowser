using FluentBrowser.Pages;
using FluentBrowser.Utilities;

using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Windows.System;
using Windows.Foundation;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private TextBox? _addressTextBox;
    private ScrollViewer? _addressTextHost;

    private int _addressBarAnimationVersion;
    private int _addressBarFocusVersion;
    private bool _addressBarFocused;
    private bool _addressBarAnimating;
    private bool _addressBarUserEditing;
    private CancellationTokenSource? _addressBarBlurCancellation;

    private readonly HttpClient _httpClient = new();
    private readonly HistorySuggestionProvider _historySuggestionProvider = new();

    private readonly Dictionary<string, Uri> _suggestionTargets =
        new(StringComparer.OrdinalIgnoreCase);

    private Uri? _selectedSuggestionTarget;
    private int _suggestionRequestVersion;
    private CancellationTokenSource? _suggestionCancellation;
    private bool _suggestionWasChosen;

    private sealed record SuggestionCandidate(
        string Display,
        Uri Target,
        double Score,
        bool IsHistory);

    private bool AreSearchSuggestionsEnabled() =>
        _settings.Values["SearchSuggestions"] as bool? ?? true;

    private void AddressBar_Loaded(object sender, RoutedEventArgs e)
    {
        FindAddressBarTextHost();

        if (_addressTextBox is null)
            return;

        var deleteButton = UIHelpers.FindDescendant<Button>(
            _addressTextBox,
            "DeleteButton");

        if (deleteButton?.Parent is Grid parent)
            parent.Children.Remove(deleteButton);
    }

    private async void AddressBar_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        _addressBarUserEditing = true;

        if (!AreSearchSuggestionsEnabled())
        {
            CancelSuggestionRequest();
            ClearSuggestions(sender);
            _suggestionTargets.Clear();
            _selectedSuggestionTarget = null;
            _suggestionWasChosen = false;
            return;
        }

        ++_addressBarFocusVersion;

        _addressBarBlurCancellation?.Cancel();
        _addressBarBlurCancellation = null;

        string query = sender.Text.Trim();

        CancelSuggestionRequest();

        int requestVersion = _suggestionRequestVersion;

        using var cancellation = new CancellationTokenSource();
        _suggestionCancellation = cancellation;

        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                ClearSuggestions(sender);
                return;
            }

            await Task.Delay(150, cancellation.Token);

            if (!IsCurrentSuggestionRequest(
                    sender,
                    query,
                    requestVersion,
                    cancellation))
            {
                return;
            }

            Task<IReadOnlyList<HistorySuggestion>> historyTask =
                SelectedWebView?.CoreWebView2 is { } historyCore
                    ? _historySuggestionProvider.GetSuggestionsAsync(
                        historyCore,
                        query,
                        12,
                        cancellation.Token)
                    : Task.FromResult<IReadOnlyList<HistorySuggestion>>(
                        Array.Empty<HistorySuggestion>());

            Task<IReadOnlyList<string>> searchTask =
                GetSearchSuggestionsAsync(
                    query,
                    cancellation.Token);

            var pendingTasks = new List<Task>
            {
                historyTask,
                searchTask
            };

            var candidates = new List<SuggestionCandidate>();

            while (pendingTasks.Count > 0)
            {
                Task completedTask = await Task.WhenAny(pendingTasks);
                pendingTasks.Remove(completedTask);

                if (!IsCurrentSuggestionRequest(
                        sender,
                        query,
                        requestVersion,
                        cancellation))
                {
                    return;
                }

                if (ReferenceEquals(completedTask, historyTask))
                {
                    try
                    {
                        foreach (HistorySuggestion item in await historyTask)
                        {
                            candidates.Add(
                                new SuggestionCandidate(
                                    item.DisplayText,
                                    item.TargetUri,
                                    item.Score,
                                    true));
                        }
                    }
                    catch
                    {
                        // History is optional.
                    }
                }
                else
                {
                    try
                    {
                        IReadOnlyList<string> suggestions =
                            await searchTask;

                        for (int i = 0; i < suggestions.Count; i++)
                        {
                            string suggestion = suggestions[i];

                            if (string.IsNullOrWhiteSpace(suggestion))
                                continue;

                            Uri target =
                                TryCreateAddressUri(
                                    suggestion,
                                    out Uri? addressUri) &&
                                addressUri is not null
                                    ? addressUri
                                    : CreateSearchUri(suggestion);

                            candidates.Add(
                                new SuggestionCandidate(
                                    suggestion,
                                    target,
                                    4000 - i * 250,
                                    false));
                        }
                    }
                    catch
                    {
                        // Search suggestions are optional.
                    }
                }
            }

            ApplyRankedSuggestions(
                sender,
                query,
                candidates,
                requestVersion,
                cancellation);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_suggestionCancellation, cancellation))
                _suggestionCancellation = null;
        }
    }

    private void ApplyRankedSuggestions(
        AutoSuggestBox sender,
        string query,
        List<SuggestionCandidate> candidates,
        int requestVersion,
        CancellationTokenSource cancellation)
    {
        if (!IsCurrentSuggestionRequest(
                sender,
                query,
                requestVersion,
                cancellation))
        {
            return;
        }

        var ranked = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Display))
            .GroupBy(
                candidate => candidate.Display,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .ToList();

        foreach (SuggestionCandidate candidate in ranked.ToList())
        {
            if (!candidate.IsHistory)
                continue;

            bool isOpen = MainTabView.TabItems
                .OfType<TabViewItem>()
                .Any(tab =>
                    tab.Tag is BrowserTab browserTab &&
                    browserTab.WebView is WebView2 webView &&
                    webView.Source is Uri uri &&
                    Uri.Compare(
                        uri,
                        candidate.Target,
                        UriComponents.AbsoluteUri,
                        UriFormat.Unescaped,
                        StringComparison.OrdinalIgnoreCase) == 0);

            if (!isOpen)
                continue;

            int index = ranked.IndexOf(candidate);

            ranked[index] = candidate with
            {
                Score = candidate.Score + 250
            };
        }

        ranked = ranked
            .OrderByDescending(candidate => candidate.Score)
            .Take(8)
            .ToList();

        if (ranked.Count == 1 &&
            SelectedWebView?.Source is Uri currentUri &&
            Uri.Compare(
                ranked[0].Target,
                currentUri,
                UriComponents.AbsoluteUri,
                UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            ClearSuggestions(sender);
            return;
        }

        _suggestionTargets.Clear();

        foreach (SuggestionCandidate candidate in ranked)
            _suggestionTargets[candidate.Display] = candidate.Target;

        string[] items = ranked
            .Select(candidate => candidate.Display)
            .ToArray();

        if (sender.ItemsSource is not string[] currentItems ||
            !currentItems.SequenceEqual(items))
        {
            sender.ItemsSource = items;
        }

        bool shouldOpen = items.Length > 0;

        if (sender.IsSuggestionListOpen != shouldOpen)
            sender.IsSuggestionListOpen = shouldOpen;
    }

    private void CancelSuggestionRequest()
    {
        ++_suggestionRequestVersion;
        _suggestionCancellation?.Cancel();
        _suggestionCancellation = null;
    }

    private bool IsCurrentSuggestionRequest(
        AutoSuggestBox sender,
        string query,
        int requestVersion,
        CancellationTokenSource cancellation) =>
        AreSearchSuggestionsEnabled() &&
        requestVersion == _suggestionRequestVersion &&
        !cancellation.IsCancellationRequested &&
        string.Equals(
            sender.Text.Trim(),
            query,
            StringComparison.Ordinal);

    private static void ClearSuggestions(AutoSuggestBox sender)
    {
        if (sender.ItemsSource is not null)
            sender.ItemsSource = null;

        if (sender.IsSuggestionListOpen)
            sender.IsSuggestionListOpen = false;
    }

    private string GetSearchEngine() =>
    _settings.Values["SearchEngine"] as string
    ?? "Google";

    private async Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        string engine = GetSearchEngine();

        if (engine == "Google")
        {
            return await GetGoogleSuggestionsAsync(
                query,
                cancellationToken);
        }

        IReadOnlyList<string> suggestions = engine switch
        {
            "Bing" => await GetBingSuggestionsAsync(
                query,
                cancellationToken),

            "Yahoo" => await GetYahooSuggestionsAsync(
                query,
                cancellationToken),

            "DuckDuckGo" => await GetDuckDuckGoSuggestionsAsync(
                query,
                cancellationToken),

            _ => Array.Empty<string>()
        };

        // Fall back to Google when the selected provider has
        // no usable autocomplete endpoint or it becomes unavailable.
        if (suggestions.Count > 0)
            return suggestions;

        return await GetGoogleSuggestionsAsync(
            query,
            cancellationToken);
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

        string? json = await GetSuggestionResponseAsync(
            url,
            cancellationToken);

        return json is null
            ? Array.Empty<string>()
            : ParsePairSuggestions(json);
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

        string? json = await GetSuggestionResponseAsync(
            url,
            cancellationToken);

        return json is null
            ? Array.Empty<string>()
            : ParsePairSuggestions(json);
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

        string? json = await GetSuggestionResponseAsync(
            url,
            cancellationToken);

        return json is null
            ? Array.Empty<string>()
            : ParseYahooSuggestions(json);
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

        string? json = await GetSuggestionResponseAsync(
            url,
            cancellationToken);

        return json is null
            ? Array.Empty<string>()
            : ParsePairSuggestions(json);
    }

    private async Task<string?> GetSuggestionResponseAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                url);

            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/154.0.0.0 Safari/537.36");

            request.Headers.TryAddWithoutValidation(
                "Accept",
                "application/json,text/plain,*/*");

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeout.CancelAfter(
                TimeSpan.FromSeconds(3));

            using HttpResponseMessage response =
                await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(
                timeout.Token);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ParsePairSuggestions(
        string json)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(json);

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

    private static IReadOnlyList<string> ParseYahooSuggestions(
        string json)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(json);

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

    private Uri CreateSearchUri(string query)
    {
        string encoded =
            Uri.EscapeDataString(query);

        return GetSearchEngine() switch
        {
            "Bing" =>
                new Uri(
                    $"https://www.bing.com/search?q={encoded}"),

            "Yahoo" =>
                new Uri(
                    $"https://search.yahoo.com/search?p={encoded}"),

            "DuckDuckGo" =>
                new Uri(
                    $"https://duckduckgo.com/?q={encoded}"),

            _ =>
                new Uri(
                    $"https://www.google.com/search?q={encoded}")
        };
    }

    private Uri CreateSearchEngineHomeUri()
    {
        return GetSearchEngine() switch
        {
            "Bing" =>
                new Uri("https://www.bing.com/"),

            "Yahoo" =>
                new Uri("https://search.yahoo.com/"),

            "DuckDuckGo" =>
                new Uri("https://duckduckgo.com/"),

            _ =>
                new Uri("https://www.google.com/")
        };
    }

    private void AddressBar_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string suggestion)
            return;

        _selectedSuggestionTarget = _suggestionTargets.TryGetValue(
            suggestion,
            out Uri? target)
                ? target
                : null;

        _suggestionWasChosen = true;
        _addressBarUserEditing = true;
    }

    private void AddressBar_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string text = args.QueryText.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            _suggestionWasChosen = false;
            _selectedSuggestionTarget = null;

            CancelSuggestionRequest();
            ClearSuggestions(sender);
            _suggestionTargets.Clear();
            return;
        }

        Uri target;

        if (_suggestionWasChosen &&
            _selectedSuggestionTarget is Uri selectedTarget)
        {
            target = selectedTarget;
        }
        else if (TryCreateAddressUri(text, out Uri? addressUri) &&
                 addressUri is not null)
        {
            target = addressUri;
        }
        else if (_suggestionTargets.TryGetValue(text, out Uri? suggestedTarget) &&
                 suggestedTarget is not null)
        {
            target = suggestedTarget;
        }
        else
        {
            target = CreateSearchUri(text);
        }

        _suggestionWasChosen = false;
        _selectedSuggestionTarget = null;

        CancelSuggestionRequest();
        ClearSuggestions(sender);
        _suggestionTargets.Clear();
        _addressBarUserEditing = false;

        if (SelectedTab is not BrowserTab tab)
            return;

        if (tab.WebView is WebView2 webView)
            Navigate(webView, target);
        else
            ConvertInternalTabToWeb(tab, target);
    }

    private void AddressBar_GotFocus(object sender, RoutedEventArgs e)
    {
        ++_addressBarFocusVersion;

        _addressBarBlurCancellation?.Cancel();
        _addressBarBlurCancellation = null;

        bool freshFocus = !_addressBarUserEditing;

        if (freshFocus)
        {
            CancelSuggestionRequest();

            if (SelectedWebView?.Source is Uri uri)
                AddressBar.Text = uri.AbsoluteUri;

            FindAddressBarTextHost();
            _addressTextBox?.SelectAll();
        }

        _addressBarUserEditing = true;
        AnimateAddressBar(true);
    }

    private async void AddressBar_LostFocus(object sender, RoutedEventArgs e)
    {
        _addressBarBlurCancellation?.Cancel();

        var cancellation = new CancellationTokenSource();
        _addressBarBlurCancellation = cancellation;

        int focusVersion = _addressBarFocusVersion;

        try
        {
            await Task.Yield();

            if (focusVersion != _addressBarFocusVersion ||
                cancellation.IsCancellationRequested)
            {
                return;
            }

            if (AddressBar.FocusState != FocusState.Unfocused ||
                AddressBar.IsSuggestionListOpen)
            {
                return;
            }

            _addressBarUserEditing = false;

            CancelSuggestionRequest();
            ClearSuggestions(AddressBar);

            if (SelectedWebView is WebView2 webView)
                UpdateAddressBar(webView);

            AnimateAddressBar(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_addressBarBlurCancellation, cancellation))
                _addressBarBlurCancellation = null;

            cancellation.Dispose();
        }
    }

    private void FindAddressBarTextHost()
    {
        if (_addressTextBox is not null)
            return;

        _addressTextBox = UIHelpers.FindDescendant<TextBox>(AddressBar);

        if (_addressTextBox is null)
            return;

        _addressTextBox.TextAlignment = TextAlignment.Left;

        _addressTextHost = UIHelpers.FindDescendant<ScrollViewer>(_addressTextBox);

        if (_addressTextHost is null)
            return;

        ElementCompositionPreview.SetIsTranslationEnabled(
            _addressTextHost,
            true);

        _addressTextHost.SizeChanged +=
            (_, _) => UpdateUnfocusedTranslation();

        _addressTextBox.TextChanged +=
            (_, _) => UpdateUnfocusedTranslation();

        _addressTextBox.KeyDown += AddressBarTextBox_KeyDown;
    }

    private void AddressBarTextBox_KeyDown(
        object sender,
        Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space)
            return;

        if (_selectedSuggestionTarget is not Uri target ||
            SelectedWebView is not WebView2 webView)
        {
            return;
        }

        e.Handled = true;

        _suggestionWasChosen = false;
        _selectedSuggestionTarget = null;

        CancelSuggestionRequest();
        ClearSuggestions(AddressBar);
        _suggestionTargets.Clear();
        _addressBarUserEditing = false;

        Navigate(webView, target);
    }

    private void AnimateAddressBar(bool focused)
    {
        FindAddressBarTextHost();

        if (_addressTextBox is null || _addressTextHost is null)
            return;

        _addressBarFocused = focused;
        _addressBarAnimating = true;

        var visual = ElementCompositionPreview.GetElementVisual(_addressTextHost);
        var compositor = visual.Compositor;

        int version = ++_addressBarAnimationVersion;

        _addressTextHost.UpdateLayout();

        float center = (float)CalculateCenterOffset();
        float startX = focused ? center : 0;
        float targetX = focused ? 0 : center;

        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3(
            "Translation",
            new Vector3(startX, 0, 0));

        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Translation";
        animation.InsertKeyFrame(
            1f,
            new Vector3(targetX, 0, 0),
            CompositionEasingFunction.CreateExponentialEasingFunction(
                compositor,
                CompositionEasingFunctionMode.Out,
                7f));
        animation.Duration = TimeSpan.FromMilliseconds(350);

        var batch = compositor.CreateScopedBatch(
            CompositionBatchTypes.Animation);

        visual.StartAnimation("Translation", animation);

        batch.Completed += (_, _) =>
        {
            if (version != _addressBarAnimationVersion)
                return;

            visual.Properties.InsertVector3(
                "Translation",
                new Vector3(targetX, 0, 0));

            _addressBarAnimating = false;
        };

        batch.End();
    }

    private void UpdateUnfocusedTranslation()
    {
        if (_addressBarFocused ||
            _addressBarAnimating ||
            _addressTextHost is null)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(_addressTextHost);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3(
            "Translation",
            new Vector3((float)CalculateCenterOffset(), 0, 0));
    }

    private double CalculateCenterOffset()
    {
        if (_addressTextBox is null || _addressTextHost is null)
            return 0;

        double available =
            _addressTextHost.ActualWidth -
            _addressTextBox.Padding.Left -
            _addressTextBox.Padding.Right;

        return Math.Max(0, (available - MeasureAddressTextWidth()) / 2);
    }

    private double MeasureAddressTextWidth()
    {
        if (_addressTextBox is null || string.IsNullOrEmpty(_addressTextBox.Text))
            return 0;

        var measurer = new TextBlock
        {
            Text = _addressTextBox.Text,
            FontFamily = _addressTextBox.FontFamily,
            FontSize = _addressTextBox.FontSize,
            FontWeight = _addressTextBox.FontWeight,
            FontStyle = _addressTextBox.FontStyle,
            CharacterSpacing = _addressTextBox.CharacterSpacing,
            TextWrapping = TextWrapping.NoWrap
        };

        measurer.Measure(new Size(
            double.PositiveInfinity,
            double.PositiveInfinity));

        return measurer.DesiredSize.Width;
    }

    private void UpdateAddressBar(WebView2 webView)
    {
        if (webView.Source is Uri uri)
            UpdateAddressBar(uri);
    }

    private void UpdateAddressBar(Uri uri)
    {
        if (_addressBarUserEditing ||
            AddressBar.FocusState != FocusState.Unfocused)
        {
            return;
        }

        AddressBar.Text = GetAddressName(uri);
    }

    private string GetAddressName(Uri uri)
    {
        bool fullWebAddress =
            _settings.Values["FullWebAddress"] as bool? ?? false;

        if (fullWebAddress)
            return uri.AbsoluteUri;

        string host = uri.Host;

        return host.StartsWith(
                "www.",
                StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host;
    }

    private static bool IsHttpUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp ||
        uri.Scheme == Uri.UriSchemeHttps;

    private static bool TryCreateAddressUri(string text, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();

        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute) &&
            absolute is not null &&
            IsHttpUri(absolute))
        {
            uri = absolute;
            return true;
        }

        int separator = text.IndexOfAny(new[] { '/', '\\' });

        string hostPart = separator >= 0
            ? text[..separator]
            : text;

        if (string.IsNullOrWhiteSpace(hostPart) ||
            hostPart.Contains(' ') ||
            !hostPart.Contains('.'))
        {
            return false;
        }

        if (!Uri.TryCreate(
                $"https://{hostPart}",
                UriKind.Absolute,
                out Uri? domainUri) ||
            domainUri is null ||
            !IsHttpUri(domainUri) ||
            string.IsNullOrWhiteSpace(domainUri.Host))
        {
            return false;
        }

        uri = domainUri;
        return true;
    }
}
