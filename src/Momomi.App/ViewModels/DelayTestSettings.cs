using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

/// <summary>
/// 延迟测试参数的统一读取入口。代理页与迷你面板共用同一份设置，
/// 避免各处硬编码 URL / 超时 / 并发。
/// </summary>
public static class DelayTestSettings
{
    public const string DefaultUrl = "https://www.gstatic.com/generate_204";
    public const int DefaultTimeoutMs = 3000;
    // 并发过高时节点间会互相争抢带宽/连接，批量测速读数被抬高；默认降到 8 更接近单点测速。
    public const int DefaultConcurrency = 8;

    public static async Task<(string Url, int TimeoutMs, int Concurrency)> ReadAsync(ISettingsService settings)
    {
        var url = await settings.GetAsync("core.delayTestUrl").ConfigureAwait(false);
        var timeout = await settings.GetIntAsync("core.delayTestTimeout", DefaultTimeoutMs).ConfigureAwait(false);
        var concurrency = await settings.GetIntAsync("core.delayTestConcurrency", DefaultConcurrency).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(url)) url = DefaultUrl;
        if (timeout <= 0) timeout = DefaultTimeoutMs;
        if (concurrency <= 0) concurrency = DefaultConcurrency;

        return (url!, timeout, concurrency);
    }
}
