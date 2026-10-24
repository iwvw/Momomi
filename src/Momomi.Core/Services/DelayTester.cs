namespace Momomi.Core.Services;

using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

/// <summary>
/// 统一的延迟测量：并发受控、每目标一次（失败再补一次）。
///
/// 早期实现是「每目标固定测 3 次取最小值」，理由是并发会让单次读数偏大。但实测下来
/// 这点精度提升远不抵代价：整批测速的耗时直接乘 3——100 个节点、并发 8、单次 300ms
/// 时用户要等十几秒。现在对齐 clash-party 的做法：每个节点只发一次请求，把省下的
/// 时间换成更高的并发；单个节点的读数不满意可以直接点它重测，不影响
/// 「先快速看一遍谁快」的主流程。只有「快速失败」（比如内核刚重载完连接被拒）才补测一次，
/// 避免把瞬时错误报成超时。
///
/// 节点测速（走内核 API）与网络目标测速（走 HTTP）共用同一套策略，保证行为一致。
///
/// 另有一条独立的「只测 TCP」路径（<see cref="MeasureTcpAsync"/>）：直接对本机到
/// 节点 server:port 建一次 TCP 连接，只量握手耗时，不经过内核、不发任何应用层数据。
/// 它衡量的是「线路通不通、握手快不快」，与走代理发 HTTP 请求的「可用延迟」不是同一个量，
/// 所以是设置项里的一个独立开关，而不是替换默认行为。
/// </summary>
public static class DelayTester
{
    /// <summary>
    /// 单个节点最多测 <see cref="MaxAttempts"/> 次，但只在「很快就失败」时重试。
    ///
    /// 只测一次是为了快（3 次取最小会把整批耗时直接乘 3）。但只测一次又容易把
    /// 瞬时连接失败（内核刚重载、socket 抢不到）误报成「超时」，所以失败后补一次：
    /// 快速失败通常是可重试的连接问题，慢速失败则是节点本身挂了，重试也是白等。
    /// </summary>
    private const int MaxAttempts = 2;

    /// <summary>失败耗时低于该阈值才认为是「快速失败」，值得再试一次。</summary>
    private const int FastFailureMs = 500;

    /// <summary>
    /// 测单个节点，返回延迟毫秒；0 表示失败或超时（界面上显示「超时」），
    /// 与内核返回的 delay=0 语义一致。
    /// 除了把 timeoutMs 传给内核，这里还额外兜一个墙钟上限：内核在极端情况下
    /// （如整机 socket 耗尽）可能迟迟不返回，不设上限就会一直占着并发名额。
    /// </summary>
    public static async Task<int> MeasureAsync(
        IMihomoApiClient api, string name, string url, int timeoutMs, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs + 1000);
                var delay = await api.ProxyDelayAsync(name, url, timeoutMs, cts.Token).ConfigureAwait(false);
                if (delay > 0) return delay;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方取消了整批测速：不要把「被取消」记成「超时」污染结果。
                throw;
            }
            catch
            {
            }

            sw.Stop();
            if (attempt + 1 >= MaxAttempts || sw.ElapsedMilliseconds >= FastFailureMs) return 0;
        }
    }

    /// <summary>
    /// 只测 TCP：对 server:port 建一次 TCP 连接，返回握手耗时毫秒；0 表示失败或超时。
    ///
    /// 与 <see cref="MeasureAsync"/> 的区别：
    /// - 不经过内核、不走代理协议，纯粹是本机到节点入口的 TCP 三次握手
    /// - 不发送任何应用层数据，因此不受节点协议（VMess/Trojan/SS）差异影响
    /// - 量不到 TLS 握手、也量不到节点到目标站的链路
    ///
    /// 用途：快速判断「线路通不通」。它比 URL 测速稳定得多（不依赖测试地址可达），
    /// 但也不能替代 URL 测速——节点入口握手很快、实际转发很慢的情况完全存在。
    /// </summary>
    public static async Task<int> MeasureTcpAsync(
        string server, int port, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server) || port is <= 0 or > 65535) return 0;

        for (var attempt = 0; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                using var client = new TcpClient();
                await client.ConnectAsync(server, port, cts.Token).ConfigureAwait(false);
                sw.Stop();

                // 连接成功即断开：这里只要握手耗时，不做任何读写。
                try { client.Close(); } catch { }

                return (int)sw.ElapsedMilliseconds;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方取消了整批测速：不要把「被取消」记成「超时」。
                throw;
            }
            catch
            {
            }

            sw.Stop();
            if (attempt + 1 >= MaxAttempts || sw.ElapsedMilliseconds >= FastFailureMs) return 0;
        }
    }

    /// <summary>
    /// 并发测量一组节点的 TCP 握手延迟。
    /// 没有可直连地址的节点（如 QUIC 类协议）不会出现在 endpoints 里，
    /// 调用方应自行回退到内核测速。
    /// </summary>
    public static async Task MeasureAllTcpAsync(
        IReadOnlyCollection<string> names,
        IReadOnlyDictionary<string, ProxyEndpoint> endpoints,
        int timeoutMs,
        int concurrency,
        Func<string, int, Task> onMeasured,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = names.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = endpoints.TryGetValue(name, out var ep)
                    ? await MeasureTcpAsync(ep.Server, ep.Port, timeoutMs, ct).ConfigureAwait(false)
                    : 0;
                await onMeasured(name, delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 测单个 HTTP 目标，返回延迟毫秒；null 表示失败或超时。
    /// 先建连、请求头即返回，只量到首字节的耗时。
    /// 与节点测速同策略：失败且失败得很快时补测一次，慢失败不重试。
    /// 注意：client 由调用方持有并复用（不在此 Dispose），避免每次测量新建 HttpClient 造成 socket 压力。
    /// </summary>
    public static async Task<long?> MeasureHttpAsync(
        Func<HttpClient> clientFactory, string url, int timeoutMs = 6000, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var client = clientFactory();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
            }

            sw.Stop();
            if (attempt + 1 >= MaxAttempts || sw.ElapsedMilliseconds >= FastFailureMs) return null;
        }
    }

    /// <summary>
    /// 并发测量一组节点，每个完成即回调 onMeasured(name, delay)。
    /// 并发上限由 concurrency 控制，回调在调用方所在上下文（通常为线程池）执行。
    /// </summary>
    public static async Task MeasureAllAsync(
        IMihomoApiClient api,
        IReadOnlyCollection<string> names,
        string url,
        int timeoutMs,
        int concurrency,
        Func<string, int, Task> onMeasured,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = names.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = await MeasureAsync(api, name, url, timeoutMs, ct).ConfigureAwait(false);
                await onMeasured(name, delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 并发测量一组 HTTP 目标，每个完成即回调 onMeasured(target, delayMs)。
    /// 与节点测速同策略：并发受控 + 每目标单次测量（快失败才补测）。
    /// </summary>
    public static async Task MeasureAllHttpAsync<T>(
        IReadOnlyCollection<T> targets,
        Func<T, string> urlSelector,
        Func<HttpClient> clientFactory,
        int concurrency,
        int timeoutMs,
        Func<T, long?, Task> onMeasured,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = await MeasureHttpAsync(clientFactory, urlSelector(target), timeoutMs, ct).ConfigureAwait(false);
                await onMeasured(target, delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
