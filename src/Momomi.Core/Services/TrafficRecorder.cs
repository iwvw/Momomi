using Momomi.Core.Models;
using Momomi.Data;

namespace Momomi.Core.Services;

public sealed class TrafficRecorder : IDisposable
{
    private readonly TrafficRepository _repo;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private long _upTotal;
    private long _downTotal;
    private long _memory;
    private int _connections;
    private bool _dirty;
    private DateTime _lastPrune = DateTime.MinValue;
    private Task _pending = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// 历史保留天数。这张表每分钟一行（1440 行/天，约 43k 行/月），
    /// 之前只有用户手动「清空历史」才会删除，等于无界增长。
    /// 保留 30 天足够覆盖「6 小时 / 1 天 / 7 天 / 30 天」四档视图。
    /// </summary>
    private const int RetentionDays = 30;

    public TrafficRecorder(TrafficRepository repo)
    {
        _repo = repo;
        _timer = new Timer(Flush, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Record(TrafficSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _upTotal = snapshot.UpTotal;
            _downTotal = snapshot.DownTotal;
            _dirty = true;
        }
    }

    public void RecordConnections(ConnectionsSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _memory = snapshot.Memory;
            _connections = snapshot.Connections.Count;
            // 连接数/内存也要能被独立落盘:某些分钟没有流量快照时,否则这一段不会写入历史。
            _dirty = true;
        }
    }

    private void Flush(object? state)
    {
        long up, down, memory;
        int conns;
        bool prune;
        lock (_gate)
        {
            if (!_dirty || _disposed) return;
            _dirty = false;
            up = _upTotal;
            down = _downTotal;
            memory = _memory;
            conns = _connections;
            prune = (DateTime.UtcNow - _lastPrune).TotalHours >= 24;
        }

        _pending = Task.Run(async () =>
        {
            try
            {
                var bucket = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm");
                await _repo.InsertAsync(bucket, up, down, memory, conns).ConfigureAwait(false);

                // 每天清理一次过期历史（首次 flush 也会执行一次，顺手收掉历史遗留数据）。
                if (prune)
                {
                    await _repo.PruneAsync(DateTimeOffset.Now.AddDays(-RetentionDays)).ConfigureAwait(false);
                    lock (_gate) _lastPrune = DateTime.UtcNow;
                }
            }
            catch
            {
                // 写库失败:把脏标记置回,下一次 flush 继续落盘(累计值不会丢,最多丢一个曲线点)。
                lock (_gate) _dirty = true;
            }
        });
    }

    public void Dispose()
    {
        _timer.Dispose();
        // 退出前把尚未落盘的一分钟补上,并等在途写入完成,避免丢历史。
        Flush(null);
        Task pending;
        lock (_gate)
        {
            _disposed = true;
            pending = _pending;
        }
        try { pending.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
