using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Storage;
using WinUI3Localizer;

namespace FluentBrowser;

public sealed class DownloadItem : INotifyPropertyChanged
{
    public CoreWebView2DownloadOperation? Operation { get; }

    private string _fileName;
    private string _filePath;
    private double _progress;
    private bool _isIndeterminate = true;

    private string _statusKey =
        "DownloadStatusDownloading";

    private ImageSource? _icon;

    private CoreWebView2DownloadState _state;

    private long _bytesReceived;
    private long _totalBytesToReceive;

    public CoreWebView2DownloadState State =>
        Operation is null
            ? _state
            : _state == CoreWebView2DownloadState.Completed
                ? CoreWebView2DownloadState.Completed
                : Operation.State;

    public long BytesReceived =>
        Operation?.BytesReceived ?? _bytesReceived;

    public long TotalBytesToReceive =>
        Operation?.TotalBytesToReceive ?? _totalBytesToReceive;

    public string FilePath
    {
        get => Operation?.ResultFilePath ?? _filePath;

        set
        {
            if (_filePath == value)
                return;

            _filePath = value;
            OnPropertyChanged();
        }
    }

    public string FileName
    {
        get => _fileName;

        set
        {
            if (_fileName == value)
                return;

            _fileName = value;
            OnPropertyChanged();
        }
    }

    public double Progress
    {
        get => _progress;

        private set
        {
            if (_progress == value)
                return;

            _progress = value;
            OnPropertyChanged();
        }
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;

        private set
        {
            if (_isIndeterminate == value)
                return;

            _isIndeterminate = value;
            OnPropertyChanged();
        }
    }

    public string MainButtonGlyph =>
        State == CoreWebView2DownloadState.Completed
            ? "\uE74D"
            : "\uE894";

    public string Status =>
        Localizer.Get().GetLocalizedString(_statusKey);

    public string StatusKey =>
        _statusKey;

    public void SetStatus(string statusKey)
    {
        if (_statusKey == statusKey)
            return;

        _statusKey = statusKey;

        OnPropertyChanged(nameof(Status));
    }

    public void UpdateLocalizedStatus()
    {
        OnPropertyChanged(nameof(Status));
    }

    public ImageSource? Icon
    {
        get => _icon;

        private set
        {
            if (_icon == value)
                return;

            _icon = value;
            OnPropertyChanged();
        }
    }

    public DownloadItem(
        CoreWebView2DownloadOperation? operation,
        string fileName,
        string filePath,
        CoreWebView2DownloadState state =
            CoreWebView2DownloadState.InProgress,
        long bytesReceived = 0,
        long totalBytesToReceive = 0,
        string statusKey =
            "DownloadStatusDownloading")
    {
        Operation = operation;

        _fileName = fileName;
        _filePath = filePath;

        _state = state;

        _bytesReceived = bytesReceived;
        _totalBytesToReceive = totalBytesToReceive;

        _statusKey = statusKey;

        if (state == CoreWebView2DownloadState.Completed)
        {
            _progress = 100;
            _isIndeterminate = false;
        }
        else if (totalBytesToReceive > 0)
        {
            _progress =
                (double)bytesReceived /
                totalBytesToReceive *
                100;

            _isIndeterminate = false;
        }
        else
        {
            _isIndeterminate = false;
        }
    }

    public void UpdateProgress()
    {
        if (Operation is null)
            return;

        _bytesReceived = Operation.BytesReceived;
        _totalBytesToReceive =
            Operation.TotalBytesToReceive;

        if (Operation.TotalBytesToReceive > 0)
        {
            IsIndeterminate = false;

            Progress =
                (double)Operation.BytesReceived /
                Operation.TotalBytesToReceive *
                100;
        }
        else
        {
            IsIndeterminate = true;
        }
    }

    public void UpdateState()
    {
        if (Operation is null)
            return;

        _state = Operation.State;

        _bytesReceived = Operation.BytesReceived;
        _totalBytesToReceive =
            Operation.TotalBytesToReceive;

        switch (Operation.State)
        {
            case CoreWebView2DownloadState.InProgress:
                SetStatus(
                    "DownloadStatusDownloading");
                break;

            case CoreWebView2DownloadState.Completed:
                Progress = 100;
                IsIndeterminate = false;

                SetStatus(
                    "DownloadStatusCompleted");
                break;

            case CoreWebView2DownloadState.Interrupted:
                IsIndeterminate = false;

                if (StatusKey != "DownloadStatusCanceled")
                {
                    SetStatus(
                        "DownloadStatusInterrupted");
                }

                break;
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(MainButtonGlyph));
    }

    public void MarkCompleted()
    {
        _state =
            CoreWebView2DownloadState.Completed;

        _bytesReceived =
            Operation?.BytesReceived ??
            _bytesReceived;

        _totalBytesToReceive =
            Operation?.TotalBytesToReceive ??
            _totalBytesToReceive;

        Progress = 100;
        IsIndeterminate = false;

        SetStatus(
            "DownloadStatusCompleted");

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(MainButtonGlyph));
    }

    public void MarkCanceled()
    {
        _state =
            CoreWebView2DownloadState.Interrupted;

        SetStatus(
            "DownloadStatusCanceled");

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(MainButtonGlyph));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }

    public async Task LoadIconAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            var file =
                await StorageFile.GetFileFromPathAsync(
                    filePath);

            using var stream =
                await file.GetThumbnailAsync(
                    Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                    32,
                    Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);

            if (stream is null)
                return;

            var bitmap = new BitmapImage();

            await bitmap.SetSourceAsync(stream);

            Icon = bitmap;
        }
        catch
        {
        }
    }
}
