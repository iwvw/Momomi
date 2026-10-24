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

    // 对齐 clash-party：每个节点只测一次，省下的时间用来拉高并发。
    // 50 对内核来说不算多（mihomo 自己测整组时是不限并发的），
    // 过低会变成排队等结果，反而比节点本身的延迟更耗时。
    public const int DefaultConcurrency = 50;

    /// <summary>并发上限，防止手滑填出离谱的值把内核/网络打满。</summary>
    public const int MaxConcurrency = 128;

    /// <summary>默认只测 TCP 关闭：默认仍走内核的 URL 测速，读到的是实际可用延迟。</summary>
    public const bool DefaultTcpOnly = false;

    public static async Task<(string Url, int TimeoutMs, int Concurrency, bool TcpOnly)> ReadAsync(
        ISettingsService settings)
    {
        var url = await settings.GetAsync("core.delayTestUrl").ConfigureAwait(false);
        var timeout = await settings.GetIntAsync("core.delayTestTimeout", DefaultTimeoutMs).ConfigureAwait(false);
        var concurrency = await settings.GetIntAsync("core.delayTestConcurrency", DefaultConcurrency).ConfigureAwait(false);
        var tcpOnly = await settings.GetBoolAsync("core.delayTestTcpOnly", DefaultTcpOnly).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(url)) url = DefaultUrl;
        if (timeout <= 0) timeout = DefaultTimeoutMs;
        if (concurrency <= 0) concurrency = DefaultConcurrency;
        if (concurrency > MaxConcurrency) concurrency = MaxConcurrency;

        return (url!, timeout, concurrency, tcpOnly);
    }
}
