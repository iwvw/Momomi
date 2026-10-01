namespace Momomi.Core.Services;

using System.Diagnostics;
using System.Net.Http;

/// <summary>
/// 统一的延迟测量：并发受控、多次取最小值。
/// 并发竞争只会让单次读数偏大，取最小值最接近无竞争时的真实延迟。
/// 节点测速（走内核 API）与网络目标测速（走 HTTP）共用同一套次数与并发策略，保证行为一致。
/// </summary>
public static class DelayTester
{
    /// <summary>每个目标的测量次数。</summary>
    public const int Attempts = 3;

    /// <summary>首次测得该阈值以下的有效延迟即提前结束，避免不必要的重测。</summary>
    private const int EarlyExitMs = 200;

    /// <summary>测单个节点，返回延迟毫秒；-1 表示全部尝试失败。</summary>
    public static async Task<int> MeasureAsync(
        IMihomoApiClient api, string name, string url, int timeoutMs, CancellationToken ct = default)
    {
        var best = -1;
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                var delay = await api.ProxyDelayAsync(name, url, timeoutMs, ct).ConfigureAwait(false);
                if (delay > 0 && (best < 0 || delay < best)) best = delay;
                if (i == 0 && best > 0 && best < EarlyExitMs) break;
            }
            catch (OperationCanceledException)
            {
                return best;
            }
            catch
            {
            }
        }
        return best;
    }

    /// <summary>
    /// 测单个 HTTP 目标（多次取最小值），返回延迟毫秒；null 表示全部尝试失败。
    /// 与节点测速同策略：先建连、请求头即返回，用同一份早退阈值。
    /// 注意：client 由调用方持有并复用（不在此 Dispose），避免每次测量新建 HttpClient 造成 socket 压力。
    /// </summary>
    public static async Task<long?> MeasureHttpAsync(
        Func<HttpClient> clientFactory, string url, int timeoutMs = 6000, CancellationToken ct = default)
    {
        long? best = null;
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                var client = clientFactory();
                var sw = Stopwatch.StartNew();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                sw.Stop();
                var ms = sw.ElapsedMilliseconds;
                if (best is null || ms < best) best = ms;
                if (i == 0 && ms < EarlyExitMs) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return best;
            }
            catch
            {
            }
        }
        return best;
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
    /// 与节点测速同策略：并发受控 + 每目标多次取最小。
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

