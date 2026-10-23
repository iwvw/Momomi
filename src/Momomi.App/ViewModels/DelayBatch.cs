using Microsoft.UI.Dispatching;

namespace Momomi.App.ViewModels;

/// <summary>
/// 批量测速结果的合并写入器：后台每 200ms 把已完成的结果合成一次 UI 更新。
///
/// 逐个结果直接 Dispatcher.TryEnqueue 在大列表上是 N 次跨线程调度 + N 次重排/重算，
/// 界面反而被这些更新拖住，看起来比实际更慢。clash-party 的组测速也是攒够一批再刷。
/// 单点测速不走这里——用户点一个节点时期望立刻看到结果。
/// </summary>
internal sealed class DelayBatch
{
    private const int FlushIntervalMs = 200;

    private readonly DispatcherQueue _dispatcher;
    private readonly Action<IReadOnlyDictionary<string, int>> _apply;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);
    private bool _flushScheduled;
    private bool _closed;

    public DelayBatch(DispatcherQueue dispatcher, Action<IReadOnlyDictionary<string, int>> apply)
    {
        _dispatcher = dispatcher;
        _apply = apply;
    }

    /// <summary>记录一个节点的结果；并发调用安全。</summary>
    public void Report(string name, int delay)
    {
        lock (_gate)
        {
            if (_closed) return;
            _pending[name] = delay;
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(FlushIntervalMs).ConfigureAwait(false);
        Flush();
    }

    /// <summary>把已积累的结果立刻写到界面上（可重复调用，空批次不触发更新）。</summary>
    public void Flush()
    {
        Dictionary<string, int> batch;
        lock (_gate)
        {
            _flushScheduled = false;
            if (_pending.Count == 0) return;
            batch = new Dictionary<string, int>(_pending, StringComparer.Ordinal);
            _pending.Clear();
        }
        _dispatcher.TryEnqueue(() => _apply(batch));
    }

    /// <summary>
    /// 一批测速结束：把最后一批结果送出去，并拒绝之后的结果。
    /// 取批次和关闭在同一把锁里完成，避免残留结果落在没人再刷的空档。
    /// </summary>
    public void Close()
    {
        Dictionary<string, int> batch;
        lock (_gate)
        {
            _closed = true;
            _flushScheduled = false;
            if (_pending.Count == 0) return;
            batch = new Dictionary<string, int>(_pending, StringComparer.Ordinal);
            _pending.Clear();
        }
        _dispatcher.TryEnqueue(() => _apply(batch));
    }
}
