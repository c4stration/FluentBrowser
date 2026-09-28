using MessagePack;
using Microsoft.Web.WebView2.Core;

namespace FluentBrowser;

[MessagePackObject]
public sealed class DownloadHistoryEntry
{
    [Key(0)]
    public string FilePath { get; set; } = string.Empty;

    [Key(1)]
    public CoreWebView2DownloadState State { get; set; }

    [Key(2)]
    public long BytesReceived { get; set; }

    [Key(3)]
    public long TotalBytesToReceive { get; set; }

    [Key(4)]
    public string StatusKey { get; set; } =
        "DownloadStatusInterrupted";
}

// Used only to read download-history files created before
// the format was changed to indexed MessagePack arrays.
internal sealed class LegacyDownloadHistoryEntry
{
    public string FilePath { get; set; } = string.Empty;

    public CoreWebView2DownloadState State { get; set; }

    public long BytesReceived { get; set; }

    public long TotalBytesToReceive { get; set; }

    public string StatusKey { get; set; } =
        "DownloadStatusInterrupted";
}