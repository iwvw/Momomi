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

    /// <summary>历史列表每页条数。</summary>
    private const int PageSize = 120;

    private readonly List<TrafficMinute> _allRecords = new();

    public ObservableCollection<TrafficBucketViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial IReadOnlyList<double> UpPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial IReadOnlyList<double> DownPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial IReadOnlyList<double> MemoryPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial IReadOnlyList<string> RateLabels { get; set; } = Array.Empty<string>();

    [ObservableProperty]
    public partial IReadOnlyList<string> MemoryLabels { get; set; } = Array.Empty<string>();

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

    [ObservableProperty]
    public partial int CurrentPage { get; set; } = 1;

    [ObservableProperty]
    public partial int PageCount { get; set; } = 1;

    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < PageCount;
    public string PageText => $"第 {CurrentPage} / {PageCount} 页";

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

            var records = (await _host.Traffic.QueryAsync(from, limit)).ToList();

            var computed = await Task.Run(() => Compute(records));

            _dispatcher.TryEnqueue(() =>
            {
                // 图表与汇总基于全量数据（时间正序：旧→新）。
                _allRecords.Clear();
                _allRecords.AddRange(records);

                UpPoints = computed.UpPoints;
                DownPoints = computed.DownPoints;
                MemoryPoints = computed.MemoryPoints;
                RateLabels = computed.RateLabels;
                MemoryLabels = computed.RateLabels;

                MaxRate = computed.MaxRate;
                MaxMemory = computed.MaxMemory;

                TotalUpText = Format.Bytes(computed.SumUp);
                TotalDownText = Format.Bytes(computed.SumDown);
                PeakText = $"{Format.Rate((long)computed.MaxRate)}";

                PageCount = Math.Max(1, (int)Math.Ceiling(records.Count / (double)PageSize));
                CurrentPage = 1;

                StatusText = records.Count == 0
                    ? "暂无历史数据（内核运行后每分钟记录一次）"
                    : $"共 {records.Count} 条分钟记录";

                ApplyPage();
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

    partial void OnCurrentPageChanged(int value) => ApplyPage();

    /// <summary>把当前页的数据填充进 Items（新→旧，第 1 页为最近数据）。</summary>
    private void ApplyPage()
    {
        Items.Clear();
        var start = (CurrentPage - 1) * PageSize;
        foreach (var r in _allRecords.Skip(start).Take(PageSize))
            Items.Add(new TrafficBucketViewModel(r));

        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageText));
    }

    private static TrafficComputation Compute(IReadOnlyList<TrafficMinute> records)
    {
        var upSeries = new List<double>(records.Count);
        var downSeries = new List<double>(records.Count);
        var memorySeries = new List<double>(records.Count);
        var times = new List<DateTimeOffset>(records.Count);

        long prevUp = 0, prevDown = 0;
        var first = true;
        long sumUp = 0, sumDown = 0;

        for (var i = records.Count - 1; i >= 0; i--)
        {
            var r = records[i];
            var upDelta = first ? 0 : Math.Max(0, r.Up - prevUp);
            var downDelta = first ? 0 : Math.Max(0, r.Down - prevDown);
            prevUp = r.Up;
            prevDown = r.Down;
            first = false;

            sumUp += upDelta;
            sumDown += downDelta;
            upSeries.Add(upDelta);
            downSeries.Add(downDelta);
            memorySeries.Add(r.Memory);
            times.Add(DateTimeOffset.TryParse(r.Bucket, out var ts) ? ts : DateTimeOffset.Now);
        }

        var seriesPoints = Math.Min(upSeries.Count, 180);
        var upPoints = Downsample(upSeries, seriesPoints);
        var downPoints = Downsample(downSeries, seriesPoints);
        var memoryPoints = Downsample(memorySeries, seriesPoints);
        var rateLabels = DownsampleLabels(times, seriesPoints);

        var maxRate = Math.Max(upPoints.DefaultIfEmpty(0).Max(), downPoints.DefaultIfEmpty(0).Max());
        var maxMemory = memoryPoints.DefaultIfEmpty(0).Max();

        return new TrafficComputation(
            upPoints, downPoints, memoryPoints, rateLabels, maxRate, maxMemory, sumUp, sumDown);
    }

    private sealed record TrafficComputation(
        IReadOnlyList<double> UpPoints,
        IReadOnlyList<double> DownPoints,
        IReadOnlyList<double> MemoryPoints,
        IReadOnlyList<string> RateLabels,
        double MaxRate,
        double MaxMemory,
        long SumUp,
        long SumDown);

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage < PageCount) CurrentPage++;
    }

    [RelayCommand]
    private void PrevPage()
    {
        if (CurrentPage > 1) CurrentPage--;
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

    private static IReadOnlyList<string> DownsampleLabels(IReadOnlyList<DateTimeOffset> times, int target)
    {
        if (times.Count == 0) return Array.Empty<string>();

        var labels = new List<string>(Math.Min(times.Count, target));
        if (times.Count <= target)
        {
            foreach (var t in times) labels.Add(FormatAxisTime(t));
            return labels;
        }

        var step = (double)times.Count / target;
        for (var i = 0; i < target; i++)
        {
            var index = Math.Min(times.Count - 1, (int)(i * step));
            labels.Add(FormatAxisTime(times[index]));
        }
        return labels;
    }

    private static string FormatAxisTime(DateTimeOffset time)
    {
        var local = time.LocalDateTime;
        return local.Date == DateTime.Now.Date ? local.ToString("HH:mm") : local.ToString("MM-dd HH:mm");
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
