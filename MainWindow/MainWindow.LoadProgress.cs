using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly Dictionary<WebView2, LoadProgressTracker>
        _loadProgressTrackers = [];

    private void ConfigureLoadProgressTracking(
        WebView2 webView,
        CoreWebView2 core)
    {
        if (_loadProgressTrackers.ContainsKey(webView))
            return;

        var tracker = new LoadProgressTracker();
        _loadProgressTrackers.Add(webView, tracker);

        SubscribeToDevToolsEvent(
            webView,
            core,
            tracker,
            "Network.requestWillBeSent",
            tracker.RecordRequestStarted);
        SubscribeToDevToolsEvent(
            webView,
            core,
            tracker,
            "Network.responseReceived",
            tracker.RecordResponseReceived);
        SubscribeToDevToolsEvent(
            webView,
            core,
            tracker,
            "Network.dataReceived",
            tracker.RecordDataReceived);
        SubscribeToDevToolsEvent(
            webView,
            core,
            tracker,
            "Network.loadingFinished",
            tracker.RecordRequestFinished);
        SubscribeToDevToolsEvent(
            webView,
            core,
            tracker,
            "Network.loadingFailed",
            tracker.RecordRequestFailed);

        tracker.EnableNetworkAsync(core);
    }

    private void SubscribeToDevToolsEvent(
        WebView2 webView,
        CoreWebView2 core,
        LoadProgressTracker tracker,
        string eventName,
        Action<string> handleEvent)
    {
        CoreWebView2DevToolsProtocolEventReceiver receiver =
            core.GetDevToolsProtocolEventReceiver(eventName);

        receiver.DevToolsProtocolEventReceived += (_, args) =>
        {
            if (!DispatcherQueue.TryEnqueue(() =>
                ProcessLoadProgressEvent(webView, tracker, handleEvent,
                    args.ParameterObjectAsJson)))
            {
                Debug.WriteLine(
                    $"Unable to process load-progress event: {eventName}.");
            }
        };

        tracker.EventReceivers.Add(receiver);
    }

    private async Task EnableLoadProgressTrackingAsync(WebView2 webView)
    {
        if (_loadProgressTrackers.TryGetValue(webView, out var tracker))
            await tracker.NetworkEnabled;
    }

    private void ProcessLoadProgressEvent(
        WebView2 webView,
        LoadProgressTracker tracker,
        Action<string> handleEvent,
        string eventJson)
    {
        handleEvent(eventJson);

        if (ReferenceEquals(SelectedWebView, webView) &&
            tracker.IsNavigating)
        {
            ShowLoadingProgress(webView);
        }
    }

    private void BeginLoadingProgress(WebView2 webView)
    {
        if (_loadProgressTrackers.TryGetValue(webView, out var tracker))
            tracker.BeginNavigation();
    }

    private async void CompleteLoadingProgress(
        WebView2 webView,
        bool hasError)
    {
        if (!_loadProgressTrackers.TryGetValue(webView, out var tracker))
            return;

        tracker.CompleteNavigation(hasError);

        if (ReferenceEquals(SelectedWebView, webView))
            ShowLoadingProgress(webView);

        if (hasError)
            return;

        int completedNavigationVersion = tracker.NavigationVersion;

        await Task.Delay(150);

        if (tracker.NavigationVersion == completedNavigationVersion &&
            tracker.CompletedNavigationVersion == completedNavigationVersion &&
            !tracker.IsNavigating)
        {
            tracker.HideCompletedProgress();

            if (ReferenceEquals(SelectedWebView, webView))
                HideLoadingProgress();
        }
    }

    private void RemoveLoadProgressTracking(WebView2 webView) =>
        _loadProgressTrackers.Remove(webView);

    private void ShowLoadingProgress(WebView2 webView)
    {
        bool hasTracker = _loadProgressTrackers.TryGetValue(
            webView,
            out var tracker);
        int progress = hasTracker ? tracker!.Progress : 1;

        LoadingProgressBar.IsIndeterminate = false;
        LoadingProgressBar.ShowError = hasTracker && tracker!.HasError;
        LoadingProgressBar.Value = progress;
        LoadingProgressBar.Opacity = 1;
    }

    private async void HideLoadingProgress()
    {
        LoadingProgressBar.ShowError = false;
        LoadingProgressBar.Opacity = 0;

        await Task.Delay(200);

        LoadingProgressBar.Value = 0;
    }

    private sealed class LoadProgressTracker
    {
        private readonly Dictionary<string, NetworkRequest>
            _requests = [];
        private string? _mainFrameLoaderId;
        private int _progress;

        public List<CoreWebView2DevToolsProtocolEventReceiver>
            EventReceivers { get; } = [];

        public bool IsNavigating { get; private set; }
        public bool HasError { get; private set; }
        public bool ShouldShowProgress =>
            IsNavigating || _showCompletedProgress;
        public int Progress => _progress;
        public int NavigationVersion { get; private set; }
        public int CompletedNavigationVersion { get; private set; }
        public Task NetworkEnabled { get; private set; } = Task.CompletedTask;
        private bool _showCompletedProgress;

        public void EnableNetworkAsync(CoreWebView2 core)
        {
            NetworkEnabled = EnableNetworkCoreAsync(core);
        }

        public void BeginNavigation()
        {
            _requests.Clear();
            _mainFrameLoaderId = null;
            _progress = 1;
            HasError = false;
            _showCompletedProgress = false;
            NavigationVersion++;
            IsNavigating = true;
        }

        public void CompleteNavigation(bool hasError)
        {
            _progress = 100;
            HasError = hasError;
            _showCompletedProgress = true;
            CompletedNavigationVersion = NavigationVersion;
            IsNavigating = false;
        }

        public void HideCompletedProgress()
        {
            if (!HasError)
                _showCompletedProgress = false;
        }

        public void RecordRequestStarted(string eventJson)
        {
            if (!IsNavigating ||
                !TryGetRequestDetails(
                    eventJson,
                    out string? requestId,
                    out string? loaderId,
                    out string? resourceType))
            {
                return;
            }

            if (_mainFrameLoaderId is null)
            {
                if (!string.Equals(resourceType, "Document",
                    StringComparison.Ordinal))
                {
                    return;
                }

                _mainFrameLoaderId = loaderId;
            }

            if (!string.Equals(_mainFrameLoaderId, loaderId,
                StringComparison.Ordinal))
            {
                return;
            }

            _requests.TryAdd(requestId, new NetworkRequest());
            UpdateProgress();
        }

        public void RecordResponseReceived(string eventJson)
        {
            if (!TryGetTrackedRequest(eventJson, out var request, out var root))
                return;

            if (root.TryGetProperty("response", out JsonElement response) &&
                response.TryGetProperty("headers", out JsonElement headers) &&
                TryGetContentLength(headers, out long contentLength))
            {
                request.TotalBytes = contentLength;
            }

            UpdateProgress();
        }

        public void RecordDataReceived(string eventJson)
        {
            if (!TryGetTrackedRequest(eventJson, out var request, out var root))
                return;

            if (TryGetInt64(root, "encodedDataLength", out long bytes) &&
                bytes > 0)
            {
                request.ReceivedBytes += bytes;
            }

            UpdateProgress();
        }

        public void RecordRequestFinished(string eventJson)
        {
            if (!TryGetTrackedRequest(eventJson, out var request, out var root))
                return;

            request.IsFinished = true;

            if (TryGetInt64(root, "encodedDataLength", out long bytes) &&
                bytes >= 0)
            {
                request.ReceivedBytes = Math.Max(request.ReceivedBytes, bytes);

                request.TotalBytes ??= request.ReceivedBytes;
            }

            UpdateProgress();
        }

        public void RecordRequestFailed(string eventJson)
        {
            if (!TryGetTrackedRequest(eventJson, out var request, out _))
                return;

            request.IsFinished = true;
            request.TotalBytes = request.ReceivedBytes;
            UpdateProgress();
        }

        private static async Task EnableNetworkCoreAsync(CoreWebView2 core)
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync(
                    "Network.enable",
                    "{}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Unable to enable network load tracking: {ex}");
            }
        }

        private bool TryGetTrackedRequest(
            string eventJson,
            out NetworkRequest request,
            out JsonElement root)
        {
            request = null!;
            root = default;

            if (!IsNavigating ||
                !TryParse(eventJson, out root) ||
                !root.TryGetProperty("requestId", out JsonElement requestIdElement))
            {
                return false;
            }

            if (_requests.TryGetValue(
                    requestIdElement.GetString() ?? string.Empty,
                    out NetworkRequest? trackedRequest))
            {
                request = trackedRequest;
                return true;
            }

            return false;
        }

        private static bool TryGetRequestDetails(
            string eventJson,
            out string requestId,
            out string loaderId,
            out string resourceType)
        {
            requestId = string.Empty;
            loaderId = string.Empty;
            resourceType = string.Empty;

            return TryParse(eventJson, out JsonElement root) &&
                root.TryGetProperty("requestId", out JsonElement requestIdElement) &&
                root.TryGetProperty("loaderId", out JsonElement loaderIdElement) &&
                root.TryGetProperty("type", out JsonElement typeElement) &&
                (requestId = requestIdElement.GetString() ?? string.Empty).Length > 0 &&
                (loaderId = loaderIdElement.GetString() ?? string.Empty).Length > 0 &&
                (resourceType = typeElement.GetString() ?? string.Empty).Length > 0;
        }

        private static bool TryParse(string json, out JsonElement root)
        {
            root = default;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                root = document.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryGetContentLength(
            JsonElement headers,
            out long contentLength)
        {
            contentLength = 0;

            foreach (JsonProperty header in headers.EnumerateObject())
            {
                if (string.Equals(header.Name, "content-length",
                    StringComparison.OrdinalIgnoreCase) &&
                    TryGetInt64(header.Value, out contentLength) &&
                    contentLength >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetInt64(
            JsonElement element,
            string propertyName,
            out long value)
        {
            value = 0;

            return element.TryGetProperty(
                    propertyName,
                    out JsonElement property) &&
                TryGetInt64(property, out value);
        }

        private static bool TryGetInt64(JsonElement element, out long value)
        {
            value = 0;

            return element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out value),
                JsonValueKind.String => long.TryParse(
                    element.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value),
                _ => false
            };
        }

        private void UpdateProgress()
        {
            long totalBytes = 0;
            long receivedBytes = 0;
            bool hasActiveUnknownLengthRequest = false;

            foreach (NetworkRequest request in _requests.Values)
            {
                if (request.TotalBytes is long total)
                {
                    totalBytes += total;
                    receivedBytes += Math.Min(request.ReceivedBytes, total);
                }
                else if (!request.IsFinished)
                {
                    hasActiveUnknownLengthRequest = true;
                }
            }

            if (totalBytes == 0)
                return;

            int maximumWhileLoading =
                hasActiveUnknownLengthRequest ? 85 : 95;
            int calculatedProgress = (int)Math.Floor(
                receivedBytes * 100d / totalBytes);

            _progress = Math.Max(
                _progress,
                Math.Min(calculatedProgress, maximumWhileLoading));
        }

        private sealed class NetworkRequest
        {
            public long? TotalBytes { get; set; }
            public long ReceivedBytes { get; set; }
            public bool IsFinished { get; set; }
        }
    }
}
