namespace Momomi.App.ViewModels;

public sealed record SubscriptionUsage(long Upload, long Download, long Total, long Expire)
{
    public long Used => Upload + Download;

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
