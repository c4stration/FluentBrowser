using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Windows.System;

using FluentBrowser.Shared;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private int _audioButtonPressVersion;
    private bool _audioButtonLongPressTriggered;

    private readonly Dictionary<WebView2, CancellationTokenSource>
        _tabAudioCancellation = new();

    private static void ConfigureTabAudioButton(
        WebView2 webView,
        Button audioButton)
    {
        audioButton.Click += (_, _) =>
        {
            if (webView.CoreWebView2 is not { } core)
                return;

            core.IsMuted = !core.IsMuted;
            UpdateTabAudioButton(audioButton, core);
        };
    }

    private void CancelTabAudio(WebView2 webView)
    {
        if (!_tabAudioCancellation.Remove(webView, out var cancellation))
            return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void HandleAudioStateChanged(
        WebView2 webView,
        Button audioButton)
    {
        ReplaceAudioCancellation(webView);

        if (webView.CoreWebView2 is not { } core ||
            !core.IsDocumentPlayingAudio)
        {
            return;
        }

        UpdateTabAudioButton(audioButton, core);
        UpdateAudioButton();
    }

    private void ReplaceAudioCancellation(WebView2 webView)
    {
        CancelTabAudio(webView);
        _tabAudioCancellation[webView] = new CancellationTokenSource();
    }

    private async Task DelayBeforeHidingAudioButtonAsync(
        WebView2 webView,
        Button audioButton,
        CoreWebView2 core)
    {
        if (!_tabAudioCancellation.TryGetValue(webView, out var cancellation))
            return;

        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(1),
                cancellation.Token);

            if (webView.CoreWebView2 is not { } currentCore ||
                !ReferenceEquals(currentCore, core))
            {
                return;
            }

            UpdateTabAudioButton(audioButton, core);

            if (ReferenceEquals(SelectedWebView, webView))
                UpdateAudioButton();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AudioToggleButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_audioButtonLongPressTriggered)
        {
            _audioButtonLongPressTriggered = false;
            return;
        }

        if (SelectedWebView?.CoreWebView2 is not { } currentCore)
            return;

        bool altPressed =
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) &
             Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (altPressed)
        {
            MuteOtherPlayingTabs(currentCore);
            UpdateAudioButton();
            return;
        }

        if (currentCore.IsDocumentPlayingAudio || currentCore.IsMuted)
            currentCore.IsMuted = !currentCore.IsMuted;
        else
            MuteAllPlayingTabs();

        UpdateAudioButton();
    }

    private void AudioToggleButton_PointerPressed(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(AudioToggleButton)
                .Properties
                .PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        int version = ++_audioButtonPressVersion;
        _audioButtonLongPressTriggered = false;

        _ = HandleAudioButtonHoldAsync(version);
    }

    private void AudioToggleButton_PointerReleased(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        ++_audioButtonPressVersion;

    private void AudioToggleButton_PointerCanceled(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        ++_audioButtonPressVersion;

    private void AudioToggleButton_PointerCaptureLost(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        ++_audioButtonPressVersion;

    private async Task HandleAudioButtonHoldAsync(int version)
    {
        await Task.Delay(500);

        if (version != _audioButtonPressVersion ||
            SelectedWebView?.CoreWebView2 is null)
        {
            return;
        }

        _audioButtonLongPressTriggered = true;
        ShowAudioMenu();
    }

    private void ShowAudioMenu()
    {
        if (SelectedWebView?.CoreWebView2 is not { } currentCore)
            return;

        var menu = new MenuFlyout();
        bool currentPlaying = currentCore.IsDocumentPlayingAudio;

        var playingTabs = MainTabView.TabItems
            .OfType<TabViewItem>()
            .Where(tab =>
                tab.Tag is BrowserTab browserTab &&
                browserTab.WebView is WebView2 webView &&
                webView.CoreWebView2?.IsDocumentPlayingAudio == true)
            .ToList();

        foreach (var tab in playingTabs)
        {
            if (tab.Tag is not BrowserTab browserTab ||
                browserTab.WebView is not WebView2 webView ||
                webView.CoreWebView2 is not { } core)
            {
                continue;
            }

            var item = new MenuFlyoutItem
            {
                Text = GetTabAudioTitle(tab, core),
                Icon = new FontIcon { Glyph = "\uE767" }
            };

            item.Click += (_, _) =>
            {
                MainTabView.SelectedItem = tab;
                UpdateAudioButton();
            };

            menu.Items.Add(item);
        }

        if (playingTabs.Count > 0)
            menu.Items.Add(new MenuFlyoutSeparator());

        if (currentPlaying)
        {
            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    "Mute This Tab",
                    "\uE74F",
                    (_, _) =>
                    {
                        currentCore.IsMuted = true;
                        UpdateAudioButton();
                    }));

            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    "Mute Other Tabs",
                    "\uE74F",
                    (_, _) =>
                    {
                        MuteOtherPlayingTabs(currentCore);
                        UpdateAudioButton();
                    }));
        }
        else if (playingTabs.Count > 0)
        {
            menu.Items.Add(
                Helpers.CreateContextMenuItem(
                    "Mute All Tabs",
                    "\uE74F",
                    (_, _) =>
                    {
                        MuteAllPlayingTabs();
                        UpdateAudioButton();
                    }));
        }

        if (menu.Items.Count > 0)
            menu.ShowAt(AudioToggleButton);
    }

    private IEnumerable<(WebView2 WebView, CoreWebView2 Core)> GetTabCores()
    {
        foreach (var tab in MainTabView.TabItems.OfType<TabViewItem>())
        {
            if (tab.Tag is BrowserTab browserTab &&
                browserTab.WebView is WebView2 webView &&
                webView.CoreWebView2 is { } core)
            {
                yield return (webView, core);
            }
        }
    }

    private void MuteAllPlayingTabs()
    {
        foreach (var (_, core) in GetTabCores())
        {
            if (core.IsDocumentPlayingAudio)
                core.IsMuted = true;
        }
    }

    private void MuteOtherPlayingTabs(CoreWebView2 currentCore)
    {
        foreach (var (_, core) in GetTabCores())
        {
            if (!ReferenceEquals(core, currentCore) &&
                core.IsDocumentPlayingAudio)
            {
                core.IsMuted = true;
            }
        }
    }

    private static string GetTabAudioTitle(
        TabViewItem tab,
        CoreWebView2 core)
    {
        if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
            return core.DocumentTitle;

        return Uri.TryCreate(
                core.Source,
                UriKind.Absolute,
                out Uri? uri)
            ? uri.Host
            : "Untitled Tab";
    }

    private void UpdateAudioButton()
    {
        if (SelectedWebView?.CoreWebView2 is not { } currentCore)
        {
            AudioToggleButton.Opacity = 0;
            return;
        }

        bool anyPlaying = GetTabCores()
            .Any(x => x.Core.IsDocumentPlayingAudio);

        bool muted = currentCore.IsMuted;

        AudioToggleButton.Opacity =
            anyPlaying || muted ? 1 : 0;

        AudioToggleButton.Content =
            muted ? "\uE74F" : "\uE767";

        AudioToggleButton.Foreground =
            (Brush)Application.Current.Resources[
                muted
                    ? "TextControlButtonForeground"
                    : "AccentTextFillColorPrimaryBrush"];
    }

    private static void UpdateTabAudioButton(
        Button button,
        CoreWebView2 core)
    {
        bool visible = core.IsDocumentPlayingAudio || core.IsMuted;

        button.Opacity = visible ? 1 : 0;
        button.IsHitTestVisible = visible;

        if (button.Content is FontIcon icon)
            icon.Glyph = core.IsMuted ? "\uE74F" : "\uE767";

        button.Foreground =
            (Brush)Application.Current.Resources[
                core.IsMuted
                    ? "TextControlButtonForeground"
                    : "AccentTextFillColorPrimaryBrush"];
    }

    private void ConfigureMediaAndPageHandlers(
        BrowserTab tab,
        WebView2 webView,
        CoreWebView2 core,
        Button audioButton,
        Image favicon)
    {
        core.IsDocumentPlayingAudioChanged += async (_, _) =>
        {
            HandleAudioStateChanged(
                webView,
                audioButton);

            if (!core.IsDocumentPlayingAudio)
            {
                await DelayBeforeHidingAudioButtonAsync(
                    webView,
                    audioButton,
                    core);
            }
        };

        core.IsMutedChanged += (_, _) =>
        {
            UpdateTabAudioButton(
                audioButton,
                core);

            if (ReferenceEquals(
                    SelectedWebView,
                    webView))
            {
                UpdateAudioButton();
            }
        };

        core.ContainsFullScreenElementChanged += (_, _) =>
            UpdateFullscreenState(webView);

        core.ContextMenuRequested += (_, args) =>
            ShowWebViewContextMenu(
                webView,
                args);

        core.FaviconChanged += async (_, _) =>
            await UpdateFaviconAsync(tab);

        core.HistoryChanged += (_, _) =>
        {
            if (ReferenceEquals(
                    SelectedWebView,
                    webView))
            {
                UpdateNavigationButtons();
            }
        };

        core.WindowCloseRequested += (_, _) =>
        {
            System.Diagnostics.Debug.WriteLine(
                $"Window close requested for host tab {tab.ExtensionTabId}: " +
                $"{core.Source}");

            if (!tab.IsClosed)
                CloseTab(tab.TabItem);
        };
    }
}
