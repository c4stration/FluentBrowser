using FluentBrowser.Shared;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Storage;
using WinUI3Localizer;

namespace FluentBrowser;

public sealed class DownloadItem : INotifyPropertyChanged
{
    public CoreWebView2DownloadOperation? Operation { get; }

    private const string CompletedStatusKey = "DownloadStatusCompleted";
    private const string InterruptedStatusKey = "DownloadStatusInterrupted";
    private const string CanceledStatusKey = "DownloadStatusCanceled";
    private const string LegacyDownloadingStatusKey = "DownloadStatusDownloading";

    private const double SpeedSampleSeconds = 0.5;
    private const double MaxSpeedSampleIntervalSeconds = 3;
    private const double SpeedSmoothing = 0.3;
    private const int SecondsPerMinute = 60;
    private const int SecondsPerHour = 60 * SecondsPerMinute;
    private const int SecondsBeforeRoundingToHours = SecondsPerHour - SecondsPerMinute / 2;
    private const double MaxEstimatedSeconds = 3650d * 24 * 60 * 60;

    public const string DownloadingStatusKey = "DownloadStatusFormat";

    private string _fileName;
    private string _filePath;
    private string _statusKey = DownloadingStatusKey;
    private double _progress;
    private bool _isIndeterminate = true;
    private long _speedSampleTimestamp;
    private long _speedSampleBytes;
    private double _bytesPerSecond;
    private long _bytesReceived;
    private long _totalBytesToReceive;
    private ImageSource? _icon;
    private CoreWebView2DownloadState _state;

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

    public string Status
    {
        get
        {
            if (_statusKey == CompletedStatusKey)
            {
                long size = TotalBytesToReceive > 0
                    ? TotalBytesToReceive
                    : BytesReceived;

                return size > 0
                    ? Helpers.FormatFileSize(size)
                    : LocalizerExtensions.GetLocalizedString(_statusKey);
            }

            if (_statusKey != DownloadingStatusKey)
            {
                return LocalizerExtensions.GetLocalizedString(_statusKey);
            }

            return TryBuildDownloadingStatus(out string text)
                ? text
                : LocalizerExtensions.GetLocalizedString(LegacyDownloadingStatusKey);
        }
    }

    public string StatusKey => _statusKey;

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
        CoreWebView2DownloadState state = CoreWebView2DownloadState.InProgress,
        long bytesReceived = 0,
        long totalBytesToReceive = 0,
        string statusKey = DownloadingStatusKey)
    {
        Operation = operation;

        _fileName = fileName;
        _filePath = filePath;
        _state = state;
        _bytesReceived = Math.Max(0, bytesReceived);
        _totalBytesToReceive = Math.Max(0, totalBytesToReceive);
        _statusKey = statusKey;

        if (state == CoreWebView2DownloadState.Completed)
        {
            _progress = 100;
            _isIndeterminate = false;
        }
        else if (_totalBytesToReceive > 0)
        {
            _progress = CalculateProgress(_bytesReceived, _totalBytesToReceive);
            _isIndeterminate = false;
        }
        else
        {
            _progress = 0;
            _isIndeterminate = state == CoreWebView2DownloadState.InProgress;
        }
    }

    public void SetStatus(string statusKey)
    {
        if (_statusKey == statusKey)
            return;

        _statusKey = statusKey;
        ResetSpeedTracking();

        OnPropertyChanged(nameof(Status));
    }

    public void UpdateLocalizedStatus()
    {
        OnPropertyChanged(nameof(Status));
    }

    public void UpdateProgress()
    {
        if (Operation is null)
            return;

        long bytesReceived = Math.Max(0, Operation.BytesReceived);
        long totalBytesToReceive = Math.Max(0, Operation.TotalBytesToReceive);

        _bytesReceived = bytesReceived;
        _totalBytesToReceive = totalBytesToReceive;

        if (totalBytesToReceive > 0)
        {
            IsIndeterminate = false;
            Progress = CalculateProgress(bytesReceived, totalBytesToReceive);
        }
        else
        {
            IsIndeterminate = true;
        }

        UpdateSpeed(bytesReceived);
    }

    private static double CalculateProgress(long bytesReceived, long totalBytesToReceive)
    {
        if (totalBytesToReceive <= 0)
            return 0;

        long received = Math.Clamp(bytesReceived, 0, totalBytesToReceive);
        return (double)received / totalBytesToReceive * 100;
    }

    private void UpdateSpeed(long bytesReceived)
    {
        if (_statusKey != DownloadingStatusKey)
            return;

        long now = Stopwatch.GetTimestamp();

        if (_speedSampleTimestamp == 0)
        {
            SetSpeedSample(now, bytesReceived);
            return;
        }

        double elapsed = Stopwatch.GetElapsedTime(
            _speedSampleTimestamp,
            now).TotalSeconds;

        if (bytesReceived < _speedSampleBytes)
        {
            SetSpeedSample(now, bytesReceived);
            ClearSpeedEstimate();
            return;
        }

        if (elapsed < SpeedSampleSeconds)
            return;

        long bytesTransferred = bytesReceived - _speedSampleBytes;

        if (elapsed > MaxSpeedSampleIntervalSeconds || bytesTransferred == 0)
        {
            SetSpeedSample(now, bytesReceived);
            ClearSpeedEstimate();
            return;
        }

        double instantaneousSpeed = bytesTransferred / elapsed;

        if (!double.IsFinite(instantaneousSpeed) || instantaneousSpeed <= 0)
        {
            SetSpeedSample(now, bytesReceived);
            ClearSpeedEstimate();
            return;
        }

        _bytesPerSecond = _bytesPerSecond <= 0
            ? instantaneousSpeed
            : _bytesPerSecond * (1 - SpeedSmoothing) +
              instantaneousSpeed * SpeedSmoothing;

        SetSpeedSample(now, bytesReceived);

        OnPropertyChanged(nameof(Status));
    }

    private void SetSpeedSample(long timestamp, long bytesReceived)
    {
        _speedSampleTimestamp = timestamp;
        _speedSampleBytes = bytesReceived;
    }

    private void ClearSpeedEstimate()
    {
        if (_bytesPerSecond <= 0)
            return;

        _bytesPerSecond = 0;
        OnPropertyChanged(nameof(Status));
    }

    private void ResetSpeedTracking()
    {
        _speedSampleTimestamp = 0;
        _speedSampleBytes = 0;
        _bytesPerSecond = 0;
    }

    private bool TryBuildDownloadingStatus(out string text)
    {
        text = string.Empty;

        long total = TotalBytesToReceive;

        if (total <= 0 || _bytesPerSecond <= 0 || !double.IsFinite(_bytesPerSecond))
            return false;

        long received = Math.Clamp(BytesReceived, 0, total);
        long remainingBytes = total - received;

        double estimatedSeconds = remainingBytes / _bytesPerSecond;

        if (!double.IsFinite(estimatedSeconds))
            estimatedSeconds = MaxEstimatedSeconds;

        int remainingSeconds = (int)Math.Clamp(
            Math.Ceiling(estimatedSeconds),
            0,
            MaxEstimatedSeconds);

        int count;
        Enums.TimeUnit unit;

        if (remainingSeconds >= SecondsBeforeRoundingToHours)
        {
            count = Math.Max(
                1,
                (int)Math.Round(remainingSeconds / (double)SecondsPerHour, MidpointRounding.AwayFromZero));
            unit = Enums.TimeUnit.Hour;
        }
        else if (remainingSeconds >= SecondsPerMinute)
        {
            count = Math.Max(
                1,
                (int)Math.Round(remainingSeconds / (double)SecondsPerMinute, MidpointRounding.AwayFromZero));
            unit = Enums.TimeUnit.Minute;
        }
        else
        {
            count = remainingSeconds;
            unit = Enums.TimeUnit.Second;
        }

        string timeRemaining = Helpers.FormatTimeRemaining(count, unit);

        long displaySpeed = _bytesPerSecond >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1, (long)Math.Round(
                _bytesPerSecond,
                MidpointRounding.AwayFromZero));

        text = string.Format(
            CultureInfo.CurrentCulture,
            LocalizerExtensions.GetLocalizedString(DownloadingStatusKey),
            Helpers.FormatFileSize(received),
            Helpers.FormatFileSize(total),
            Helpers.FormatFileSize(displaySpeed),
            timeRemaining);

        return true;
    }

    public void UpdateState()
    {
        if (Operation is null)
            return;

        _state = Operation.State;
        _bytesReceived = Math.Max(0, Operation.BytesReceived);
        _totalBytesToReceive = Math.Max(0, Operation.TotalBytesToReceive);

        switch (_state)
        {
            case CoreWebView2DownloadState.InProgress:
                if (_totalBytesToReceive > 0)
                {
                    IsIndeterminate = false;
                    Progress = CalculateProgress(_bytesReceived, _totalBytesToReceive);
                }
                else
                {
                    IsIndeterminate = true;
                }

                SetStatus(DownloadingStatusKey);
                break;

            case CoreWebView2DownloadState.Completed:
                Progress = 100;
                IsIndeterminate = false;
                SetStatus(CompletedStatusKey);
                break;

            case CoreWebView2DownloadState.Interrupted:
                IsIndeterminate = false;

                if (StatusKey != CanceledStatusKey)
                    SetStatus(InterruptedStatusKey);

                break;
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(MainButtonGlyph));
    }

    public void MarkCompleted()
    {
        _state = CoreWebView2DownloadState.Completed;
        _bytesReceived = Math.Max(0, Operation?.BytesReceived ?? _bytesReceived);
        _totalBytesToReceive = Math.Max(0, Operation?.TotalBytesToReceive ?? _totalBytesToReceive);

        Progress = 100;
        IsIndeterminate = false;
        SetStatus(CompletedStatusKey);

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(MainButtonGlyph));
    }

    public void MarkCanceled()
    {
        _state = CoreWebView2DownloadState.Interrupted;
        IsIndeterminate = false;
        SetStatus(CanceledStatusKey);

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

            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);

            using var stream = await file.GetThumbnailAsync(
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