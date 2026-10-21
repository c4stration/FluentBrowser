using CommunityToolkit.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using WinUI3Localizer;
using FluentBrowser.Shared;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private TextBox? _addressTextBox;
    private Thickness _addressTextBoxBasePadding;
    private ScrollViewer? _addressTextHost;

    private int _addressBarAnimationVersion;
    private int _addressBarFocusVersion;
    private bool _addressBarFocused;
    private bool _addressBarAnimating;
    private bool _addressBarUserEditing;
    private bool _addressBarSubmitting;
    private bool _addressBarContextFlyoutOpen;
    private CancellationTokenSource? _addressBarBlurCancellation;
    private Popup? _addressSuggestionPopup;
    private FrameworkElement? _addressSuggestionContainer;
    private ListView? _addressSuggestionList;
    private Storyboard? _addressSuggestionHeightStoryboard;
    private Uri? _pendingAddressBarUri;
    private WebView2? _pendingAddressBarFocusWebView;

    private readonly HttpClient _httpClient = new();
    private readonly HistorySuggestionProvider _historySuggestionProvider = new();
    private readonly SuggestionRanker _suggestionRanker = new();
    private readonly Dictionary<string, (string GroupId, string CandidateId)> _suggestionTrainingIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Uri> _suggestionTargets =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SearchSuggestionProvider _searchSuggestionProvider;

    private Uri? _selectedSuggestionTarget;
    private int _suggestionRequestVersion;
    private CancellationTokenSource? _suggestionCancellation;
    private bool _suggestionWasChosen;

    private sealed class AddressBarSuggestion : INotifyPropertyChanged
    {
        private ImageSource? _favicon;

        public AddressBarSuggestion(
            string display,
            Uri target,
            string iconGlyph,
            bool isHistory)
        {
            Display = display;
            Target = target;
            IconGlyph = iconGlyph;
            IsHistory = isHistory;
        }

        public string Display { get; }
        public Uri Target { get; }
        public string IconGlyph { get; }
        public bool IsHistory { get; }

        public ImageSource? Favicon
        {
            get => _favicon;
            set
            {
                if (ReferenceEquals(_favicon, value))
                    return;

                _favicon = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(Favicon)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /*private sealed record SuggestionCandidate(
        string Display,
        Uri Target,
        double Score,
        bool IsHistory);*/

    private bool AreSearchSuggestionsEnabled() =>
        _settings.Values["SearchSuggestions"] as bool? ?? true;

    private void AddressBar_Loaded(object sender, RoutedEventArgs e)
    {
        FindAddressBarTextHost();
        InitializeSuggestionPopupAnimation();

        if (_addressTextBox is null)
            return;

        _addressTextBoxBasePadding = _addressTextBox.Padding;
        UpdateAddressBarTextPadding();

        var deleteButton = _addressTextBox.FindDescendant<Button>(x => x.Name == "DeleteButton");

        if (deleteButton?.Parent is Grid parent)
            parent.Children.Remove(deleteButton);
    }

    private async void AddressBar_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput ||
            _addressBarSubmitting)
        {
            return;
        }

        _addressBarUserEditing = true;

        if (!AreSearchSuggestionsEnabled())
        {
            CancelSuggestionRequest();
            ClearSuggestions(sender);
            _suggestionTargets.Clear();
            _suggestionTrainingIds.Clear();
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
                _searchSuggestionProvider.GetSuggestionsAsync(
                    query,
                    GetSearchEngine(),
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
                                    true,
                                    item.Title));
                        }
                    }
                    catch
                    {
                        // Nothing!
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
                                    : _searchSuggestionProvider.CreateSearchUri(
                                        suggestion,
                                        GetSearchEngine());

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
                        // Nothing again!
                    }
                }
            }

            await ApplyRankedSuggestionsAsync(
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

    private void InitializeSuggestionPopupAnimation()
    {
        if (_addressSuggestionPopup is not null)
            return;

        if (VisualTreeHelper.GetChild(AddressBar, 0) is not Grid templateRoot ||
            templateRoot.Children.OfType<Popup>().FirstOrDefault() is not { } popup ||
            popup.Child is not FrameworkElement container)
        {
            return;
        }

        _addressSuggestionPopup = popup;
        _addressSuggestionContainer = container;
        _addressSuggestionList = container.FindDescendant<ListView>();

        popup.Opened += AddressSuggestionPopup_Opened;
        popup.Closed += AddressSuggestionPopup_Closed;

        if (_addressSuggestionList is not null)
        {
            _addressSuggestionList.ContainerContentChanging +=
                AddressSuggestionList_ContainerContentChanging;
        }
    }

    private void AddressSuggestionPopup_Opened(object? sender, object e)
    {
        if (_addressSuggestionContainer is not { } container ||
            container.ActualHeight <= 0)
        {
            return;
        }

        _addressSuggestionHeightStoryboard?.Stop();
        _addressSuggestionHeightStoryboard = null;

        double targetHeight = container.ActualHeight;
        double startHeight = targetHeight * 0.9;

        container.Height = startHeight;

        var animation = new DoubleAnimation
        {
            From = startHeight,
            To = targetHeight,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EnableDependentAnimation = true,
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);

        Storyboard.SetTarget(animation, container);
        Storyboard.SetTargetProperty(animation, "Height");

        _addressSuggestionHeightStoryboard = storyboard;

        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_addressSuggestionHeightStoryboard, storyboard))
                return;

            container.Height = targetHeight;
            _addressSuggestionHeightStoryboard = null;
        };

        storyboard.Begin();

        if (_addressSuggestionList is { } list)
        {
            list.DispatcherQueue.TryEnqueue(() =>
            {
                if (_addressSuggestionPopup?.IsOpen != true)
                    return;

                list.UpdateLayout();

                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (list.ContainerFromIndex(i) is ListViewItem item)
                        AnimateSuggestionItem(item, i);
                }
            });
        }
    }

    private void AddressSuggestionPopup_Closed(object? sender, object e)
    {
        _addressSuggestionHeightStoryboard?.Stop();
        _addressSuggestionHeightStoryboard = null;

        if (_addressSuggestionContainer is { } container)
            container.Height = double.NaN;

        if (_pendingAddressBarFocusWebView is { } webView)
        {
            _pendingAddressBarFocusWebView = null;
            QueueAddressBarSubmissionFocus(webView);
        }
    }

    private void QueueAddressBarSubmissionFocus(WebView2 webView)
    {
        webView.DispatcherQueue.TryEnqueue(() =>
        {
            CancelSuggestionRequest();
            ClearSuggestions(AddressBar);

            _suggestionTargets.Clear();
            _suggestionTrainingIds.Clear();

            if (ReferenceEquals(SelectedWebView, webView))
            {
                webView.Focus(FocusState.Programmatic);
                AnimateAddressBar(false);
            }

            _addressBarSubmitting = false;
        });
    }

    private void AddressSuggestionList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue ||
            args.Phase != 0 ||
            args.ItemContainer is not ListViewItem item)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(item);

        visual.StopAnimation("Opacity");
        visual.Opacity = 0;

        if (_addressSuggestionPopup?.IsOpen != true ||
            _addressSuggestionList is not { } list)
        {
            return;
        }

        int index = args.ItemIndex;

        list.DispatcherQueue.TryEnqueue(() =>
        {
            if (_addressSuggestionPopup?.IsOpen != true ||
                !ReferenceEquals(list.ContainerFromIndex(index), item))
            {
                return;
            }

            AnimateSuggestionItem(item, index);
        });
    }

    private static void AnimateSuggestionItem(ListViewItem item, int index)
    {
        var visual = ElementCompositionPreview.GetElementVisual(item);
        var compositor = visual.Compositor;

        visual.StopAnimation("Opacity");
        visual.Opacity = 0;

        var animation = compositor.CreateScalarKeyFrameAnimation();

        animation.InsertKeyFrame(0f, 0f);

        animation.InsertKeyFrame(
            1f,
            1f,
            compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1f),
                new Vector2(0.3f, 1f)));

        animation.DelayTime =
            TimeSpan.FromMilliseconds(Math.Min(index * 18, 90));

        animation.Duration = TimeSpan.FromMilliseconds(220);

        visual.StartAnimation("Opacity", animation);
    }

    private async Task ApplyRankedSuggestionsAsync(
        AutoSuggestBox sender,
        string query,
        List<SuggestionCandidate> candidates,
        int requestVersion,
        CancellationTokenSource cancellation)
    {
        if (!IsCurrentSuggestionRequest(sender, query, requestVersion, cancellation))
            return;

        var openUris = MainTabView.TabItems
            .OfType<TabViewItem>()
            .Select(tab => tab.Tag as BrowserTab)
            .Where(t => t?.WebView?.Source is Uri)
            .Select(t => t!.WebView!.Source!)
            .ToList();

        var ranked = _suggestionRanker.Rank(candidates, query, openUris);

        if (ranked.Count == 1 &&
            SelectedWebView?.Source is Uri currentUri &&
            Uri.Compare(ranked[0].Target, currentUri,
                UriComponents.AbsoluteUri, UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            ClearSuggestions(sender);
            return;
        }

        _suggestionTargets.Clear();
        _suggestionTrainingIds.Clear();

        string groupId = Guid.NewGuid().ToString("N");

        for (int i = 0; i < ranked.Count; i++)
        {
            var item = ranked[i];

            _suggestionTargets[item.Display] = item.Target;

            SuggestionCandidate candidate =
                candidates.First(c =>
                    string.Equals(
                        c.Display,
                        item.Display,
                        StringComparison.OrdinalIgnoreCase));

            bool isOpenTab = item.IsOpenTab;

            string q = query.Trim().ToLowerInvariant();
            string display = candidate.Display.ToLowerInvariant();
            string host = candidate.Target.Host.ToLowerInvariant();
            string title = (candidate.Title ?? string.Empty).ToLowerInvariant();
            string candidateId = Guid.NewGuid().ToString("N");

            _suggestionTrainingIds[item.Display] = (groupId, candidateId);

            _ = SuggestionTrainingLogger.LogImpressionAsync(
                new SuggestionImpression
                {
                    GroupId = groupId,
                    CandidateId = candidateId,
                    Query = query,
                    Display = candidate.Display,
                    Uri = candidate.Target.AbsoluteUri,
                    BaseScore = (float)candidate.Score,
                    IsHistory = candidate.IsHistory ? 1f : 0f,
                    IsOpenTab = isOpenTab ? 1f : 0f,
                    DisplayLength = candidate.Display.Length,
                    QueryLength = q.Length,
                    ExactMatch = display == q ? 1f : 0f,
                    PrefixMatch =
                        display.StartsWith(q, StringComparison.Ordinal)
                            ? 1f
                            : 0f,
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
                    Position = i,
                    Timestamp = DateTimeOffset.UtcNow
                });
        }

        AddressBarSuggestion[] items = ranked
            .Select(r => new AddressBarSuggestion(
                r.Display,
                r.Target,
                LooksLikeWebsiteSuggestion(r.Display)
                    ? "\uE774"
                    : r.IsHistory
                        ? "\uE81C"
                        : "\uE721",
                r.IsHistory))
            .ToArray();

        sender.ItemsSource = items;

        sender.IsSuggestionListOpen = items.Length > 0;

        foreach (AddressBarSuggestion item in items.Where(
                     item => LooksLikeWebsiteSuggestion(item.Display)))
        {
            ImageSource? favicon = await GetSuggestionFaviconAsync(
                item.Target);

            if (!IsCurrentSuggestionRequest(
                    sender,
                    query,
                    requestVersion,
                    cancellation))
            {
                return;
            }

            item.Favicon = favicon;
        }
    }

    private static bool LooksLikeWebsiteSuggestion(string display) =>
        TryCreateAddressUri(display, out Uri? uri) &&
        uri is not null &&
        IsHttpUri(uri);

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

    private void AddressBar_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not AddressBarSuggestion suggestion)
            return;

        _selectedSuggestionTarget = suggestion.Target;

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
            _suggestionTrainingIds.Clear();
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
            target = _searchSuggestionProvider.CreateSearchUri(
                text,
                GetSearchEngine());
        }

        if (_suggestionWasChosen &&
            args.ChosenSuggestion is AddressBarSuggestion chosenSuggestion &&
            _suggestionTrainingIds.TryGetValue(
                chosenSuggestion.Display,
                out var trainingId))
        {
            _ = SuggestionTrainingLogger.LogClickAsync(
                new SuggestionClick
                {
                    GroupId = trainingId.GroupId,
                    CandidateId = trainingId.CandidateId,
                    Timestamp = DateTimeOffset.UtcNow
                });
        }

        ++_addressBarFocusVersion;

        _addressBarBlurCancellation?.Cancel();
        _addressBarBlurCancellation = null;

        _addressBarSubmitting = true;
        _suggestionWasChosen = false;
        _selectedSuggestionTarget = null;

        WebView2? webViewToFocus =
            (SelectedTab)?.WebView;

        bool waitForSuggestionPopupClose =
            webViewToFocus is not null &&
            _addressSuggestionPopup?.IsOpen == true;

        _pendingAddressBarFocusWebView =
            waitForSuggestionPopupClose ? webViewToFocus : null;

        CancelSuggestionRequest();
        ClearSuggestions(sender);
        _suggestionTargets.Clear();
        _suggestionTrainingIds.Clear();

        if (SelectedTab is not BrowserTab tab)
        {
            _addressBarUserEditing = false;
            _addressBarSubmitting = false;
            return;
        }

        if (tab.WebView is WebView2 webView)
        {
            _addressBarUserEditing = false;

            UpdateAddressBar(target, force: true);
            _pendingAddressBarUri = target;

            Navigate(webView, target);

            if (!waitForSuggestionPopupClose)
                QueueAddressBarSubmissionFocus(webView);
        }
        else
        {
            _addressBarUserEditing = false;
            _addressBarSubmitting = false;

            ConvertInternalTabToWeb(tab, target);
        }
    }

    private void AddressBar_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_addressBarSubmitting)
            return;

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
        if (_addressBarSubmitting)
            return;

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

            if (_addressBarContextFlyoutOpen ||
                AddressBar.FocusState != FocusState.Unfocused ||
                (!_addressBarSubmitting && AddressBar.IsSuggestionListOpen))
            {
                return;
            }

            _addressBarUserEditing = false;

            CancelSuggestionRequest();
            ClearSuggestions(AddressBar);

            if (SelectedWebView is WebView2 webView)
            {
                if (_pendingAddressBarUri is not null)
                    UpdateAddressBar(_pendingAddressBarUri);
                else
                    UpdateAddressBar(webView);
            }

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

        _addressTextBox = AddressBar.FindDescendant<TextBox>();

        if (_addressTextBox is null)
            return;

        if (_addressTextBox.ContextFlyout is { } contextFlyout)
        {
            contextFlyout.Opening += (_, _) =>
                _addressBarContextFlyoutOpen = true;

            contextFlyout.Closed += (_, _) =>
                _addressBarContextFlyoutOpen = false;
        }

        _addressTextBox.TextAlignment = TextAlignment.Left;

        _addressTextHost = _addressTextBox.FindDescendant<ScrollViewer>();

        if (_addressTextHost is null)
            return;

        var textBox = _addressTextBox;
        var menu = new MenuFlyout();

        var cutItem = Helpers.CreateContextMenuItem(
            LocalizerExtensions.GetLocalizedString("Cut"),
            "\uE8C6",
            (_, _) => textBox.CutSelectionToClipboard(),
            "Ctrl+X");

        var copyItem = Helpers.CreateContextMenuItem(
            LocalizerExtensions.GetLocalizedString("Copy"),
            "\uE8C8",
            (_, _) => textBox.CopySelectionToClipboard(),
            "Ctrl+C");

        var pasteItem = Helpers.CreateContextMenuItem(
            LocalizerExtensions.GetLocalizedString("Paste"),
            "\uE77F",
            (_, _) => textBox.PasteFromClipboard(),
            "Ctrl+V");

        var selectAllItem = Helpers.CreateContextMenuItem(
            LocalizerExtensions.GetLocalizedString("SelectAll"),
            "",
            (_, _) => textBox.SelectAll(),
            "Ctrl+A");

        menu.Items.Add(cutItem);
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(selectAllItem);

        menu.Opening += (_, _) =>
        {
            cutItem.Text = LocalizerExtensions.GetLocalizedString("Cut");
            copyItem.Text = LocalizerExtensions.GetLocalizedString("Copy");
            pasteItem.Text = LocalizerExtensions.GetLocalizedString("Paste");
            selectAllItem.Text = LocalizerExtensions.GetLocalizedString("SelectAll");

            cutItem.IsEnabled = textBox.SelectionLength > 0;
            copyItem.IsEnabled = textBox.SelectionLength > 0;
            selectAllItem.IsEnabled = textBox.Text.Length > 0;

            _addressBarContextFlyoutOpen = true;
        };

        menu.Closed += (_, _) =>
        {
            _addressBarContextFlyoutOpen = false;
        };

        textBox.ContextFlyout = menu;

        ElementCompositionPreview.SetIsTranslationEnabled(
            _addressTextHost,
            true);

        _addressTextHost.IsHitTestVisible = false;

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
        _suggestionTrainingIds.Clear();
        _addressBarUserEditing = false;

        Navigate(webView, target);
    }

    private void AnimateAddressBar(bool focused)
    {
        FindAddressBarTextHost();

        if (_addressBarFocused == focused)
            return;

        if (_addressTextBox is null || _addressTextHost is null)
            return;

        _addressBarFocused = focused;
        _addressBarAnimating = true;

        _addressTextHost.IsHitTestVisible = focused;

        var visual = ElementCompositionPreview.GetElementVisual(
            _addressTextHost);

        var compositor = visual.Compositor;

        int version = ++_addressBarAnimationVersion;

        AddressBar.UpdateLayout();
        _addressTextBox.UpdateLayout();
        _addressTextHost.UpdateLayout();

        double available =
            _addressTextBox.ActualWidth -
            _addressTextBoxBasePadding.Left -
            _addressTextBoxBasePadding.Right;

        double textWidth = MeasureAddressTextWidth();

        if (available <= 0 || textWidth >= available - 8)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);

            _addressTextHost.ChangeView(0, null, null, true);
            _addressTextHost.IsHitTestVisible = focused;
            _addressBarAnimating = false;

            return;
        }

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

            _addressTextHost.IsHitTestVisible = focused;
            _addressBarAnimating = false;

            if (!focused)
            {
                _addressTextHost.ChangeView(0, null, null, true);
                UpdateUnfocusedTranslation();
            }
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

        var visual = ElementCompositionPreview.GetElementVisual(
            _addressTextHost);

        visual.StopAnimation("Translation");

        visual.Properties.InsertVector3(
            "Translation",
            new Vector3((float)CalculateCenterOffset(), 0, 0));

        _addressTextHost.IsHitTestVisible = false;
    }

    private double CalculateCenterOffset()
    {
        if (_addressTextBox is null)
            return 0;

        double textWidth = MeasureAddressTextWidth();

        return Math.Max(
            0,
            (_addressTextBox.ActualWidth - textWidth) / 2 -
            _addressTextBoxBasePadding.Left);
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

    private void UpdateAddressBar(WebView2 webView, bool force = false)
    {
        if (webView.Source is Uri uri)
            UpdateAddressBar(uri, force);
    }

    private void UpdateAddressBar(Uri uri, bool force = false)
    {
        bool addressBarFocused =
            _addressBarUserEditing ||
            AddressBar.FocusState != FocusState.Unfocused;

        if (!force && addressBarFocused)
            return;

        if (force || _pendingAddressBarUri is not null)
            _pendingAddressBarUri = null;

        AddressBar.Text = addressBarFocused
            ? uri.AbsoluteUri
            : GetAddressName(uri);
    }

    private string GetAddressName(Uri uri)
    {
        bool fullWebAddress =
            _settings.Values["FullWebAddress"] as bool? ?? false;

        if (fullWebAddress || !IsHttpUri(uri))
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

    private static bool IsSupportedAddressUri(Uri uri) =>
        uri.IsAbsoluteUri;

    private static bool TryCreateAddressUri(string text, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();

        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute) &&
            absolute is not null &&
            IsSupportedAddressUri(absolute))
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

    private void SetAddressBarTrailingButtonVisible(Button button, bool visible)
    {
        var items = AddressBarTrailingButtons.Items;
        bool inList = items.Contains(button);

        if (visible)
        {
            if (inList)
                return;

            int refreshIndex = items.IndexOf(RefreshButton);
            if (refreshIndex >= 0)
                items.Insert(refreshIndex, button);
            else
                items.Add(button);
        }
        else if (inList)
        {
            items.Remove(button);
        }

        UpdateAddressBarTextPadding();
    }

    private void UpdateAddressBarTextPadding()
    {
        if (_addressTextBox is null)
            return;

        Thickness padding = _addressTextBoxBasePadding;

        double rightPadding =
            padding.Right + AddressBarTrailingButtons.Items.Count * 32;

        _addressTextBox.Padding = new Thickness(
            padding.Left,
            padding.Top,
            rightPadding,
            padding.Bottom);
    }
}