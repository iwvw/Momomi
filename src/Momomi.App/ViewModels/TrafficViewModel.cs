using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Services;
using Momomi.Data;

namespace Momomi.App.ViewModels;

public sealed partial class TrafficBucketViewModel
{
    public string Label { get; }
    public string Up { get; }
    public string Down { get; }
    public string Memory { get; }

    public TrafficBucketViewModel(TrafficMinute record)
    {
        if (DateTimeOffset.TryParse(record.Bucket, out var ts))
        {
            var local = ts.LocalDateTime;
            var now = DateTime.Now;
            Label = local.Date == now.Date ? local.ToString("HH:mm") : local.ToString("MM-dd HH:mm");
        }
        else
        {
            Label = record.Bucket;
        }
        Up = Format.Bytes(record.Up);
        Down = Format.Bytes(record.Down);
        Memory = Format.Bytes(record.Memory);
    }
}

public sealed partial class TrafficViewModel : ObservableObject
{
    private readonly MomomiHost _host;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<TrafficBucketViewModel> Items { get; } = new();
    public ObservableCollection<double> UpSeries { get; } = new();
    public ObservableCollection<double> DownSeries { get; } = new();
    public ObservableCollection<double> MemorySeries { get; } = new();

    [ObservableProperty]
    public partial IReadOnlyList<double> UpPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial IReadOnlyList<double> DownPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial IReadOnlyList<double> MemoryPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial double MaxRate { get; set; }

    [ObservableProperty]
    public partial double MaxMemory { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string TotalUpText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string TotalDownText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string PeakText { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial int SelectedRangeIndex { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public TrafficViewModel(MomomiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_loaded && !force) return;

        IsBusy = true;
        StatusText = "正在加载…";
        try
        {
            var from = SelectedRangeIndex switch
            {
                1 => DateTimeOffset.Now.AddDays(-1),
                2 => DateTimeOffset.Now.AddDays(-7),
                3 => DateTimeOffset.Now.AddDays(-30),
                _ => DateTimeOffset.Now.AddHours(-6),
            };
            var limit = SelectedRangeIndex switch
            {
                1 => 1440,
                2 => 10080,
                3 => 43200,
                _ => 360,
            };

            var records = (await _host.Traffic.QueryAsync(from, limit)).Reverse().ToList();

            _dispatcher.TryEnqueue(() =>
            {
                Items.Clear();
                UpSeries.Clear();
                DownSeries.Clear();
                MemorySeries.Clear();

                long prevUp = 0, prevDown = 0;
                var first = true;
                long sumUp = 0, sumDown = 0;

                foreach (var r in records)
                {
                    Items.Add(new TrafficBucketViewModel(r));

                    var upDelta = first ? 0 : Math.Max(0, r.Up - prevUp);
                    var downDelta = first ? 0 : Math.Max(0, r.Down - prevDown);
                    prevUp = r.Up;
                    prevDown = r.Down;
                    first = false;

                    sumUp += upDelta;
                    sumDown += downDelta;
                    UpSeries.Add(upDelta);
                    DownSeries.Add(downDelta);
                    MemorySeries.Add(r.Memory);
                }

                var seriesPoints = Math.Min(UpSeries.Count, 180);
                UpPoints = Downsample(UpSeries, seriesPoints);
                DownPoints = Downsample(DownSeries, seriesPoints);
                MemoryPoints = Downsample(MemorySeries, seriesPoints);

                MaxRate = Math.Max(UpPoints.DefaultIfEmpty(0).Max(), DownPoints.DefaultIfEmpty(0).Max());
                MaxMemory = MemoryPoints.DefaultIfEmpty(0).Max();

                TotalUpText = Format.Bytes(sumUp);
                TotalDownText = Format.Bytes(sumDown);
                PeakText = $"{Format.Rate((long)MaxRate)}";

                StatusText = records.Count == 0
                    ? "暂无历史数据（内核运行后每分钟记录一次）"
                    : $"共 {records.Count} 条分钟记录";

                _loaded = true;
            });
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static IReadOnlyList<double> Downsample(IReadOnlyList<double> source, int target)
    {
        if (source.Count == 0) return Array.Empty<double>();
        if (source.Count <= target) return source.ToList();

        var step = (double)source.Count / target;
        var result = new List<double>(target);
        for (var i = 0; i < target; i++)
        {
            var start = (int)(i * step);
            var end = Math.Min(source.Count, (int)((i + 1) * step));
            double peak = 0;
            for (var j = start; j < end; j++) peak = Math.Max(peak, source[j]);
            result.Add(peak);
        }
        return result;
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(force: true);

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _host.Traffic.PruneAsync(DateTimeOffset.Now.AddYears(10));
            StatusText = "历史数据已清空";
            await LoadAsync(force: true);
        }
        catch (Exception ex)
        {
            StatusText = $"清空失败：{ex.Message}";
        }
    }

    private bool _loaded;

    partial void OnSelectedRangeIndexChanged(int value)
    {
        _loaded = false;
        _ = LoadAsync(force: true);
    }
}
