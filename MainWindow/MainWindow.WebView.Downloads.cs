using FluentBrowser.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Resolvers;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Path = System.IO.Path;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    public ObservableCollection<DownloadItem> Downloads { get; } = [];

    private readonly string _downloadHistoryFile = Path.Combine(
        ApplicationData.Current.LocalFolder.Path,
        "DownloadHistory.msgpack");

    private readonly SemaphoreSlim _downloadHistoryLock = new(1, 1);

    private static readonly MessagePackSerializerOptions _downloadHistoryOptions =
        ContractlessStandardResolver.Options.WithSecurity(
            MessagePackSecurity.UntrustedData);
    private bool _downloadIconActivated;

    private void ConfigureDownloadHandlers(
        CoreWebView2 core,
        WebView2 webView)
    {
        core.SaveFileSecurityCheckStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();

            try
            {
                string fileName = Path.GetFileName(args.FilePath);

                DownloadItem? item = Downloads.FirstOrDefault(d =>
                    string.Equals(
                        d.FileName,
                        fileName,
                        StringComparison.OrdinalIgnoreCase));

                string sizeText = item is { TotalBytesToReceive: > 0 }
                    ? $" ({Helpers.FormatFileSize(item.TotalBytesToReceive)})"
                    : string.Empty;

                var dialog = new ContentDialog
                {
                    Title = "Potentially risky download",

                    Content = new TextBlock
                    {
                        Text =
            $"WebView2 flagged the file type for {fileName}" +
            $"{sizeText}. Only keep it if you trust the " +
            "source and expected this download.",
                        TextWrapping = TextWrapping.Wrap
                    },

                    PrimaryButtonText = "Keep",
                    CloseButtonText = "Discard",

                    DefaultButton = ContentDialogButton.Primary,

                    XamlRoot = webView.XamlRoot,

                    Style = (Style)Application.Current.Resources["FixedContentDialogStyle"],
                };

                ContentDialogResult result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    // The user has explicitly accepted this file, so replace
                    // WebView2's hidden default warning with this decision
                    args.SuppressDefaultPolicy = true;
                    args.CancelSave = false;
                }
                else
                {
                    args.CancelSave = true;

                    if (item is not null)
                    {
                        Downloads.Remove(item);
                        UpdateDownloadIconVisibility();
                        await SaveDownloadHistoryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Save-file security decision failed: {ex}");
                args.CancelSave = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        core.DownloadStarting += async (_, args) =>
        {
            if (_downloadNavigations.TryGetValue(
                    webView,
                    out DownloadNavigation? navigation))
            {
                navigation.DownloadStarted = true;
            }

            args.Handled = true;

            var download = args.DownloadOperation;

            string fileName =
                Path.GetFileName(args.ResultFilePath);

            var item = new DownloadItem(
                download,
                fileName,
                args.ResultFilePath,
                download.State,
                download.BytesReceived,
                download.TotalBytesToReceive);

            _downloadIconActivated = true;

            Downloads.Insert(0, item);
            UpdateDownloadIconVisibility();

            await SaveDownloadHistoryAsync();

            await item.LoadIconAsync(download.ResultFilePath);

            download.BytesReceivedChanged += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    item.UpdateProgress();
                    UpdateTaskbarDownloadProgress();
                    UpdateDownloadProgressRing();

                    if (download.TotalBytesToReceive > 0 &&
                        download.BytesReceived >=
                            download.TotalBytesToReceive &&
                        item.State ==
                            CoreWebView2DownloadState.InProgress)
                    {
                        item.MarkCompleted();

                        await item.LoadIconAsync(
                            download.ResultFilePath);

                        UpdateDownloadIconVisibility();
                        await SaveDownloadHistoryAsync();
                    }
                });
            };

            download.StateChanged += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    item.UpdateState();
                    UpdateTaskbarDownloadProgress();
                    UpdateDownloadProgressRing();

                    switch (download.State)
                    {
                        case CoreWebView2DownloadState.Completed:
                            await item.LoadIconAsync(
                                download.ResultFilePath);

                            UpdateDownloadIconVisibility();
                            break;

                        case CoreWebView2DownloadState.Interrupted:
                            UpdateDownloadIconVisibility();
                            break;
                    }

                    await SaveDownloadHistoryAsync();
                });
            };

            var settings =
                ApplicationData.Current.LocalSettings;

            bool askEveryDownload =
                settings.Values["AskEveryDownload"] as bool?
                ?? false;

            if (!askEveryDownload)
                return;

            var deferral = args.GetDeferral();

            try
            {
                string suggestedFileName =
                    Path.GetFileName(args.ResultFilePath);

                string extension =
                    Path.GetExtension(suggestedFileName);

                if (string.IsNullOrWhiteSpace(extension))
                    extension = ".download";

                var picker = new FileSavePicker();

                picker.FileTypeChoices.Add(
                    extension.ToUpperInvariant(),
                    new List<string> { extension });

                picker.SuggestedFileName =
                    suggestedFileName;

                InitializeWithWindow.Initialize(
                    picker,
                    GetWindowHandle());

                StorageFile? file =
                    await picker.PickSaveFileAsync();

                if (file is null)
                {
                    args.Cancel = true;
                    Downloads.Remove(item);
                    UpdateDownloadIconVisibility();
                    return;
                }

                File.Delete(file.Path);

                args.ResultFilePath = file.Path;
                args.Handled = true;

                item.FileName = file.Name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Download picker failed: {ex}");

                args.Cancel = true;
                Downloads.Remove(item);
                UpdateDownloadIconVisibility();
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    private void ApplyDownloadSettings(
        CoreWebView2Profile profile)
    {
        var settings =
            ApplicationData.Current.LocalSettings;

        string? path =
            settings.Values["DownloadFolder"] as string;

        if (!string.IsNullOrWhiteSpace(path))
        {
            profile.DefaultDownloadFolderPath = path;
        }
        else
        {
            settings.Values["DownloadFolder"] =
                profile.DefaultDownloadFolderPath;
        }
    }

    private async Task LoadDownloadHistoryAsync()
    {
        if (!File.Exists(_downloadHistoryFile))
            return;

        try
        {
            byte[] data =
                await File.ReadAllBytesAsync(_downloadHistoryFile);

            List<DownloadHistoryEntry>? entries;
            bool changed = false;

            try
            {
                entries =
                    MessagePackSerializer.Deserialize<
                        List<DownloadHistoryEntry>>(
                            data,
                            _downloadHistoryOptions);
            }
            catch (MessagePackSerializationException)
            {
                List<LegacyDownloadHistoryEntry>? legacyEntries =
                    MessagePackSerializer.Deserialize<
                        List<LegacyDownloadHistoryEntry>>(
                            data,
                            _downloadHistoryOptions);

                entries = legacyEntries?
                    .Select(entry => new DownloadHistoryEntry
                    {
                        FilePath = entry.FilePath,
                        State = entry.State,
                        BytesReceived = entry.BytesReceived,
                        TotalBytesToReceive =
                            entry.TotalBytesToReceive,
                        StatusKey = entry.StatusKey
                    })
                    .ToList();

                changed = true;
            }

            if (entries is null)
                return;

            foreach (DownloadHistoryEntry entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.FilePath))
                    continue;

                CoreWebView2DownloadState state = entry.State;

                if (state ==
                    CoreWebView2DownloadState.InProgress)
                {
                    state =
                        CoreWebView2DownloadState.Interrupted;

                    if (entry.StatusKey ==
                        "DownloadStatusDownloading")
                    {
                        entry.StatusKey =
                            "DownloadStatusInterrupted";
                    }

                    changed = true;
                }

                var item = new DownloadItem(
                    null,
                    Path.GetFileName(entry.FilePath),
                    entry.FilePath,
                    state,
                    entry.BytesReceived,
                    entry.TotalBytesToReceive,
                    entry.StatusKey);

                Downloads.Add(item);

                _ = item.LoadIconAsync(entry.FilePath);
            }

            DownloadsButton.Visibility = Visibility.Visible;

            DownloadIcon.Visibility = Visibility.Visible;
            DownloadingIcon.Visibility = Visibility.Collapsed;
            DownloadComplete.Visibility = Visibility.Collapsed;

            UpdateDownloadIconVisibility();

            if (changed)
                await SaveDownloadHistoryAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to load download history: {ex}");
        }
    }

    private async Task SaveDownloadHistoryAsync()
    {
        await _downloadHistoryLock.WaitAsync();

        try
        {
            if (Downloads.Count == 0)
            {
                if (File.Exists(_downloadHistoryFile))
                    File.Delete(_downloadHistoryFile);

                return;
            }

            DownloadHistoryEntry[] entries =
                Downloads.Select(item => new DownloadHistoryEntry
                {
                    FilePath = item.FilePath,
                    State = item.State,
                    BytesReceived = item.BytesReceived,
                    TotalBytesToReceive = item.TotalBytesToReceive,
                    StatusKey = item.StatusKey
                }).ToArray();

            byte[] data =
                MessagePackSerializer.Serialize(
                    entries,
                    _downloadHistoryOptions);

            string temporaryFile =
                _downloadHistoryFile + ".tmp";

            await File.WriteAllBytesAsync(
                temporaryFile,
                data);

            File.Move(
                temporaryFile,
                _downloadHistoryFile,
                true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to save download history: {ex}");
        }
        finally
        {
            _downloadHistoryLock.Release();
        }
    }

    private async void MainButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not DownloadItem item)
        {
            return;
        }

        if (item.State == CoreWebView2DownloadState.InProgress &&
            item.Operation is not null)
        {
            item.Operation.Cancel();
            item.MarkCanceled();
        }
        else
        {
            Downloads.Remove(item);

            UpdateDownloadIconVisibility();

            await SaveDownloadHistoryAsync();

            if (item.State ==
                CoreWebView2DownloadState.Completed)
            {
                string filePath = item.FilePath;

                _ = Task.Run(() =>
                {
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"Failed to delete downloaded file: {ex}");
                    }
                });
            }

            return;
        }

        UpdateDownloadIconVisibility();

        await SaveDownloadHistoryAsync();
    }

    private void UpdateDownloadIconVisibility()
    {
        UpdateTaskbarDownloadProgress();
        UpdateDownloadProgressRing();

        DownloadsButton.Visibility = Visibility.Visible;

        ClearCompletedButton.IsEnabled =
            Downloads.Any(item =>
                item.State != CoreWebView2DownloadState.InProgress);

        if (!_downloadIconActivated)
        {
            DownloadIcon.Visibility = Visibility.Visible;
            DownloadingIcon.Visibility = Visibility.Collapsed;
            DownloadComplete.Visibility = Visibility.Collapsed;
            return;
        }

        bool hasDownloads = Downloads.Count > 0;

        DownloadIcon.Visibility =
            hasDownloads
                ? Visibility.Collapsed
                : Visibility.Visible;

        NoDownloadsText.Visibility =
            hasDownloads
                ? Visibility.Collapsed
                : Visibility.Visible;

        if (!hasDownloads)
        {
            DownloadingIcon.Visibility = Visibility.Collapsed;
            DownloadComplete.Visibility = Visibility.Collapsed;
            return;
        }

        bool anyInProgress = Downloads.Any(d =>
            d.State == CoreWebView2DownloadState.InProgress);

        bool anyCompleted = Downloads.Any(d =>
            d.State == CoreWebView2DownloadState.Completed);

        if (anyInProgress)
        {
            DownloadingIcon.Visibility = Visibility.Visible;
            DownloadComplete.Visibility = Visibility.Collapsed;
        }
        else if (anyCompleted)
        {
            DownloadingIcon.Visibility = Visibility.Collapsed;
            DownloadComplete.Visibility = Visibility.Visible;
        }
        else
        {
            DownloadIcon.Visibility = Visibility.Visible;
            DownloadingIcon.Visibility = Visibility.Collapsed;
            DownloadComplete.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateDownloadProgressRing()
    {
        DownloadItem[] activeDownloads = Downloads
            .Where(item => item.State == CoreWebView2DownloadState.InProgress)
            .ToArray();

        if (activeDownloads.Length == 0)
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            return;
        }

        // A percentage is only honest when every active download has a known
        // size. Do not alternate between individual items while waiting for a
        // response that does not provide Content-Length.
        if (activeDownloads.Any(item => item.TotalBytesToReceive <= 0))
        {
            DownloadProgress.IsIndeterminate = true;
            return;
        }

        ulong bytesReceived = 0;
        ulong totalBytes = 0;

        foreach (DownloadItem item in activeDownloads)
        {
            ulong total = (ulong)item.TotalBytesToReceive;
            ulong received = (ulong)Math.Clamp(
                item.BytesReceived,
                0,
                item.TotalBytesToReceive);

            bytesReceived += received;
            totalBytes += total;
        }

        DownloadProgress.IsIndeterminate = totalBytes == 0;

        if (totalBytes > 0)
        {
            DownloadProgress.Value = Math.Clamp(
                (double)bytesReceived / totalBytes * 100,
                0,
                100);
        }
    }

    private async void RemoveFromList_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem &&
            menuItem.DataContext is DownloadItem item)
        {
            Downloads.Remove(item);
            UpdateDownloadIconVisibility();

            await SaveDownloadHistoryAsync();
        }
    }

    private async void ClearCompletedButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        List<DownloadItem> downloadsToClear =
            Downloads
                .Where(item =>
                    item.State !=
                    CoreWebView2DownloadState.InProgress)
                .ToList();

        foreach (DownloadItem item in downloadsToClear)
            Downloads.Remove(item);

        UpdateDownloadIconVisibility();

        await SaveDownloadHistoryAsync();
    }
}