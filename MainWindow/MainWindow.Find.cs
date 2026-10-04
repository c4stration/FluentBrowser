using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;
using Windows.System;
using FluentBrowser.Shared;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private string _findTerm = string.Empty;
    private bool _findCaseSensitive;
    private bool _findMatchWord;
    private bool _findEventsHooked;
    private int _findMatchCount;
    private int _findActiveIndex;

    private void FindMenuItem_Click(object sender, RoutedEventArgs e) =>
        ShowFindOnPage();

    private void ShowFindOnPage()
    {
        FindOnPageTeachingTip.IsOpen = true;

        EnsureFindEventsHooked();

        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            FindOnPageBox.Focus(FocusState.Programmatic);

            if (FindOnPageBox.FindDescendant<TextBox>() is TextBox textBox)
            {
                textBox.Focus(FocusState.Programmatic);
                textBox.SelectAll();
            }

            await StartOrUpdateFindAsync();
        });
    }

    private void CloseFindTeachingTip_Click(object sender, RoutedEventArgs e) =>
        CloseFindOnPage();

    private void CloseFindOnPage()
    {
        FindOnPageTeachingTip.IsOpen = false;
        _ = StopFindAsync();
    }

    private void FindOnPageTeachingTip_Closed(
        TeachingTip sender,
        TeachingTipClosedEventArgs args) =>
        _ = StopFindAsync();

    private void EnsureFindEventsHooked()
    {
        if (_findEventsHooked)
            return;

        if (SelectedWebView?.CoreWebView2 is not { } core)
            return;

        core.Find.MatchCountChanged += Find_MatchCountChanged;
        core.Find.ActiveMatchIndexChanged += Find_ActiveMatchIndexChanged;
        _findEventsHooked = true;
    }

    private void Find_MatchCountChanged(CoreWebView2Find sender, object args)
    {
        _findMatchCount = sender.MatchCount;
        UpdateFindCount();
        UpdateFindNavigationButtons();
    }

    private void Find_ActiveMatchIndexChanged(CoreWebView2Find sender, object args)
    {
        _findActiveIndex = sender.ActiveMatchIndex;
        UpdateFindCount();
        UpdateFindNavigationButtons();
    }

    private void UpdateFindCount()
    {
        FindCountTextBlock.Text = string.IsNullOrEmpty(_findTerm) ||
                                  _findMatchCount == 0 ||
                                  _findActiveIndex < 1
            ? "0/0"
            : $"{_findActiveIndex}/{_findMatchCount}";
    }

    private void UpdateFindNavigationButtons()
    {
        bool hasMatches = _findMatchCount > 0;
        FindPreviousButton.IsEnabled = hasMatches;
        FindNextButton.IsEnabled = hasMatches;
    }

    private async Task StopFindAsync()
    {
        try
        {
            if (SelectedWebView?.CoreWebView2 is { } core)
                core.Find.Stop();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StopFind failed: {ex}");
        }

        _findMatchCount = 0;
        _findActiveIndex = 0;
        UpdateFindNavigationButtons();
        await Task.CompletedTask;
    }

    private async Task StartOrUpdateFindAsync()
    {
        if (SelectedWebView?.CoreWebView2 is not { } core)
            return;

        EnsureFindEventsHooked();

        if (string.IsNullOrEmpty(_findTerm))
        {
            await StopFindAsync();
            return;
        }

        try
        {
            CoreWebView2FindOptions options =
                core.Environment.CreateFindOptions();

            options.FindTerm = _findTerm;
            options.IsCaseSensitive = _findCaseSensitive;
            options.ShouldMatchWord = _findMatchWord;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;

            await core.Find.StartAsync(options);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartFind failed: {ex}");
        }
    }

    private async void FindOnPageBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput &&
            args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
        {
            return;
        }

        _findTerm = sender.Text ?? string.Empty;

        UpdateFindCount();

        await StartOrUpdateFindAsync();
    }

    private void FindOnPageBox_TextPropertyChanged(
        DependencyObject sender,
        DependencyProperty dp)
    {
        if (sender is AutoSuggestBox box)
        {
            FindCountTextBlock.Visibility =
                string.IsNullOrEmpty(box.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }
    }

    private async void FindOnPageBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _findTerm = args.QueryText ?? sender.Text ?? string.Empty;
        await StartOrUpdateFindAsync();
    }

    private void FindOnPageBox_GotFocus(object sender, RoutedEventArgs e)
    {
        FindCountTextBlock.Margin = new Thickness(0, 0, 84, 0);
    }

    private void FindOnPageBox_LostFocus(object sender, RoutedEventArgs e)
    {
        FindCountTextBlock.Margin = new Thickness(0, 0, 48, 0);
    }

    private void FindOnPageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool shift =
                (GetKeyState(VirtualKey.Shift) & 0x8000) != 0;

            if (SelectedWebView?.CoreWebView2 is { } core &&
                !string.IsNullOrEmpty(_findTerm))
            {
                if (shift)
                    core.Find.FindPrevious();
                else
                    core.Find.FindNext();
            }

            e.Handled = true;
        }
    }

    private void FindOnPageBox_KeyDownHandled(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CloseFindOnPage();
            e.Handled = true;
        }
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWebView?.CoreWebView2 is { } core &&
            !string.IsNullOrEmpty(_findTerm))
        {
            core.Find.FindNext();
        }
    }

    private void FindPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWebView?.CoreWebView2 is { } core &&
            !string.IsNullOrEmpty(_findTerm))
        {
            core.Find.FindPrevious();
        }
    }

    private async void FindMatchCase_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item)
            _findCaseSensitive = item.IsChecked;

        await StartOrUpdateFindAsync();
    }

    private async void FindMatchWord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item)
            _findMatchWord = item.IsChecked;

        await StartOrUpdateFindAsync();
    }
}