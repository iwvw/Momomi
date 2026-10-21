namespace Momomi.App.ViewModels;

public sealed record SubscriptionUsage(long Upload, long Download, long Total, long Expire)
{
    public long Used => Upload + Download;

    /// <summary>
    /// <see cref="DateTimeOffset.FromUnixTimeSeconds"/> 允许的最大值（9999-12-31）。
    /// 订阅头里的 expire 是外部数据，越界会抛 ArgumentOutOfRangeException。
    /// </summary>
    private const long MaxUnixSeconds = 253402300799;

    /// <summary>把 expire 转成本地日期文本。越界/非法值一律按「长期有效」处理，绝不抛出。</summary>
    public static string FormatExpire(long expire)
    {
        if (expire <= 0) return "长期有效";
        if (expire > MaxUnixSeconds) return "长期有效";

        try
        {
            return $"到期 {DateTimeOffset.FromUnixTimeSeconds(expire).LocalDateTime:yyyy-MM-dd}";
        }
        catch
        {
            return "长期有效";
        }
    }

    public static SubscriptionUsage? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && long.TryParse(kv[1].Trim(), out var v))
                values[kv[0].Trim()] = v;
        }
        if (values.Count == 0) return null;

        values.TryGetValue("upload", out var up);
        values.TryGetValue("download", out var down);
        values.TryGetValue("total", out var total);
        values.TryGetValue("expire", out var expire);
        return new SubscriptionUsage(up, down, total, expire);
    }
}
