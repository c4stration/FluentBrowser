namespace FluentBrowser.Shared;

public static class Helpers
{
    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.##} KB";

        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024):0.##} MB";

        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }

    public static string FormatFileSize(ulong bytes) =>
        FormatFileSize((long)bytes);
}