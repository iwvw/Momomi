namespace Momomi.App.ViewModels;

public static class Format
{
    public static string Rate(long bytesPerSecond)
    {
        var v = (double)bytesPerSecond;
        if (v < 1024) return $"{v:0} B/s";
        if (v < 1024 * 1024) return $"{v / 1024:0.0} KB/s";
        if (v < 1024L * 1024 * 1024) return $"{v / 1024 / 1024:0.00} MB/s";
        return $"{v / 1024 / 1024 / 1024:0.00} GB/s";
    }

    /// <summary>固定用 MB/s、保留一位小数：首页实时速率卡片需要稳定的单位与位数，避免数字宽度跳动。</summary>
    public static string RateMb(double bytesPerSecond)
        => $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";

    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.00} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:0.00} GB";
    }

    public static string Delay(int? ms)
    {
        if (ms is null or < 0) return "—";
        if (ms == 0) return "超时";
        return $"{ms} ms";
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }
}
