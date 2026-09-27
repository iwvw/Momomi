using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Models;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class ConnectionRowViewModel
{
    public string Id { get; }
    public string Host { get; }
    public string Network { get; }
    public string Rule { get; }
    public string Chain { get; }
    public string Process { get; }
    public string Upload { get; }
    public string Download { get; }
    public string Duration { get; }
    public string Source { get; }
    public string Destination { get; }
    public bool HasRule { get; }
    public bool HasChain { get; }

    public string UpRate { get; private set; } = "";
    public string DownRate { get; private set; } = "";

    public bool HasRate => !string.IsNullOrEmpty(UpRate) || !string.IsNullOrEmpty(DownRate);

    /// <summary>进程大图标，用于列表左侧显示。</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Icon { get; }

    // 排序用的原始数值。
    public long UploadBytes { get; }
    public long DownloadBytes { get; }
    public double DurationSeconds { get; }

    public ConnectionRowViewModel(ConnectionItem item)
    {
        Id = item.Id;
        Host = string.IsNullOrEmpty(item.Metadata.Host)
            ? $"{item.Metadata.DestinationIP}:{item.Metadata.DestinationPort}"
            : item.Metadata.Host;
        Network = string.IsNullOrEmpty(item.Metadata.Network) ? "TCP" : item.Metadata.Network.ToUpperInvariant();
        Rule = string.IsNullOrEmpty(item.RulePayload) ? item.Metadata.Rule : $"{item.Metadata.Rule}({item.RulePayload})";
        HasRule = !string.IsNullOrEmpty(Rule);
        Chain = string.Join(" → ", item.Chains.Reverse());
        HasChain = item.Chains.Count > 0;
        Process = string.IsNullOrEmpty(item.Metadata.Process)
            ? System.IO.Path.GetFileName(item.Metadata.ProcessPath)
            : item.Metadata.Process;
        Upload = Format.Bytes(item.Upload);
        Download = Format.Bytes(item.Download);
        UploadBytes = item.Upload;
        DownloadBytes = item.Download;
        DurationSeconds = Math.Max(0, (DateTimeOffset.Now - item.Start).TotalSeconds);
        Duration = Format.Duration(DateTimeOffset.Now - item.Start);
        Source = $"{item.Metadata.SourceIP}:{item.Metadata.SourcePort}";
        Destination = $"{item.Metadata.DestinationIP}:{item.Metadata.DestinationPort}";
        Icon = ProcessIconProvider.Get(item.Metadata.ProcessPath);
    }

    /// <summary>图标为空时是否显示，控制图标占位可见性。</summary>
    public bool HasIcon => Icon is not null;

    public void SetRate(long upBytes, long downBytes)
    {
        UpRate = upBytes > 0 ? $"↑ {Format.Rate(upBytes)}" : "";
        DownRate = downBytes > 0 ? $"↓ {Format.Rate(downBytes)}" : "";
    }
}

public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<ConnectionRowViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial string UpTotal { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DownTotal { get; set; } = "0 B";

    [ObservableProperty]
    public partial string Memory { get; set; } = "0 B";

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial int FilteredCount { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>排序字段：duration / upload / download / process / host。</summary>
    [ObservableProperty]
    public partial string SortBy { get; set; } = "duration";

    [ObservableProperty]
    public partial bool SortDescending { get; set; } = true;

    public string CountText => $"活动 {Count}";
    public string TotalText => $"↑ {UpTotal}   ↓ {DownTotal}";
    public string MemoryText => $"内存 {Memory}";

    public string RealtimeText => IsPaused ? "已暂停" : "实时";
    public string RealtimeGlyph => IsPaused ? "\uE768" : "\uE769";

    public string SortGlyph => SortDescending ? "\uE74B" : "\uE74A";

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortGlyph));
        RenderNow();
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(RealtimeText));
        OnPropertyChanged(nameof(RealtimeGlyph));
        if (!value) RenderNow();
    }

    private ConnectionsSnapshot? _latest;
    private Dictionary<string, (long Up, long Down, DateTimeOffset At)> _previous = new();
    private DateTimeOffset _lastRender = DateTimeOffset.MinValue;
    private readonly EventHandler<ConnectionsSnapshot> _onConnectionsUpdated;

    public ConnectionsViewModel(ICoreManager core, DispatcherQueue dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        _onConnectionsUpdated = (_, snapshot) => OnConnections(snapshot);
    }

    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _core.ConnectionsUpdated += _onConnectionsUpdated;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _core.ConnectionsUpdated -= _onConnectionsUpdated;
    }

    private void OnConnections(ConnectionsSnapshot snapshot)
    {
        _latest = snapshot;
        if (IsPaused) return;

        var now = DateTimeOffset.Now;
        if ((now - _lastRender).TotalMilliseconds < 1000) return;
        _lastRender = now;

        _dispatcher.TryEnqueue(() => Render(snapshot, now));
    }

    public void RenderNow()
    {
        if (_latest is not null) Render(_latest, DateTimeOffset.Now);
    }

    private void Render(ConnectionsSnapshot snapshot, DateTimeOffset now)
    {
        var keyword = Filter?.Trim() ?? "";
        var rows = snapshot.Connections
            .Where(c => keyword.Length == 0
                || (c.Metadata.Host?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.Metadata.Process?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.Metadata.DestinationIP?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(c => new ConnectionRowViewModel(c))
            .ToList();

        var next = new Dictionary<string, (long Up, long Down, DateTimeOffset At)>(rows.Count);
        foreach (var row in rows)
        {
            var item = snapshot.Connections.First(c => c.Id == row.Id);
            if (_previous.TryGetValue(row.Id, out var prev))
            {
                var seconds = Math.Max(0.001, (now - prev.At).TotalSeconds);
                var upRate = (long)Math.Max(0, (item.Upload - prev.Up) / seconds);
                var downRate = (long)Math.Max(0, (item.Download - prev.Down) / seconds);
                row.SetRate(upRate, downRate);
            }
            next[row.Id] = (item.Upload, item.Download, now);
        }
        _previous = next;

        rows = SortRows(rows);

        Items.Clear();
        foreach (var row in rows) Items.Add(row);

        Count = snapshot.Connections.Count;
        FilteredCount = rows.Count;
        UpTotal = Format.Bytes(snapshot.UploadTotal);
        DownTotal = Format.Bytes(snapshot.DownloadTotal);
        Memory = Format.Bytes(snapshot.Memory);
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(MemoryText));
    }

    private List<ConnectionRowViewModel> SortRows(List<ConnectionRowViewModel> rows)
    {
        IOrderedEnumerable<ConnectionRowViewModel> ordered = SortBy switch
        {
            "upload" => SortDescending
                ? rows.OrderByDescending(r => r.UploadBytes)
                : rows.OrderBy(r => r.UploadBytes),
            "download" => SortDescending
                ? rows.OrderByDescending(r => r.DownloadBytes)
                : rows.OrderBy(r => r.DownloadBytes),
            "process" => SortDescending
                ? rows.OrderByDescending(r => r.Process, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Process, StringComparer.OrdinalIgnoreCase),
            "host" => SortDescending
                ? rows.OrderByDescending(r => r.Host, StringComparer.OrdinalIgnoreCase)
                : rows.OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase),
            _ => SortDescending
                ? rows.OrderByDescending(r => r.DurationSeconds)
                : rows.OrderBy(r => r.DurationSeconds),
        };
        return ordered.ThenBy(r => r.Host, StringComparer.OrdinalIgnoreCase).ToList();
    }

    partial void OnSortByChanged(string value) => RenderNow();

    [RelayCommand]
    private void ToggleSort(string field)
    {
        if (string.Equals(SortBy, field, StringComparison.Ordinal))
            SortDescending = !SortDescending;
        else
        {
            SortBy = field;
            SortDescending = true;
        }
    }

    [RelayCommand]
    private async Task CloseConnectionAsync(ConnectionRowViewModel? row)
    {
        if (row is null || _core.Api is null) return;
        try
        {
            await _core.Api.CloseConnectionAsync(row.Id).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task CloseAllAsync()
    {
        if (_core.Api is null) return;
        try
        {
            await _core.Api.CloseAllConnectionsAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    partial void OnFilterChanged(string value) => RenderNow();
}
