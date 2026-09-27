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

    public TrafficRecorder(TrafficRepository repo)
    {
        _repo = repo;
        _timer = new Timer(Flush, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Record(TrafficSnapshot snapshot)
    {
        lock (_gate)
        {
            _upTotal = snapshot.UpTotal;
            _downTotal = snapshot.DownTotal;
            _dirty = true;
        }
    }

    public void RecordConnections(ConnectionsSnapshot snapshot)
    {
        lock (_gate)
        {
            _memory = snapshot.Memory;
            _connections = snapshot.Connections.Count;
        }
    }

    private void Flush(object? state)
    {
        long up, down, memory;
        int conns;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            up = _upTotal;
            down = _downTotal;
            memory = _memory;
            conns = _connections;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var bucket = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm");
                await _repo.InsertAsync(bucket, up, down, memory, conns).ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
