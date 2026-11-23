using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Models;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class RuleRowViewModel
{
    public int Index { get; }
    public string Type { get; }
    public string Payload { get; }
    public string Proxy { get; }
    public long? HitCount { get; }

    /// <summary>命中计数展示；无计数时显示 "—"。</summary>
    public string Size => HitCount?.ToString("N0") ?? "—";

    /// <summary>是否有命中计数（用于显示 Chip）。</summary>
    public bool HasHit => HitCount is not null;

    /// <summary>最近命中时间（内核 size 为累计次数，不提供时间；此字段为将来扩展保留）。</summary>
    public bool IsDisabled { get; set; }

    public RuleRowViewModel(RuleItem item)
    {
        Index = item.Index;
        Type = item.Type;
        Payload = item.Payload;
        Proxy = item.Proxy;
        HitCount = item.Size is null ? null : long.TryParse(item.Size, out var n) ? n : null;
    }
}

public sealed partial class RulesViewModel : ObservableObject
{
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private IReadOnlyList<RuleItem> _all = Array.Empty<RuleItem>();
    private readonly EventHandler<CoreStateChanged> _onStateChanged;
    private readonly DispatcherQueueTimer _filterTimer;
    private readonly Dictionary<int, RuleRowViewModel> _rowCache = new();
    private readonly List<RuleRowViewModel> _filterRows = new();

    public ObservableCollection<RuleRowViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "未加载";

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public RulesViewModel(ICoreManager core, DispatcherQueue dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        _filterTimer = dispatcher.CreateTimer();
        _filterTimer.Interval = TimeSpan.FromMilliseconds(200);
        _filterTimer.IsRepeating = false;
        _filterTimer.Tick += (_, _) => Apply();
        _onStateChanged = (_, e) =>
        {
            if (e.State != CoreState.Running)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _all = Array.Empty<RuleItem>();
                    Items.Clear();
                    DisabledIndexes = Array.Empty<int>();
                    OnPropertyChanged(nameof(HasDisabled));
                });
            }
            // 同 Proxies：StateChanged 常在线程池上触发，LoadAsync 会写绑定属性。
            else _dispatcher.TryEnqueue(() => _ = AutoReloadAsync());
        };
    }

    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _core.StateChanged += _onStateChanged;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _core.StateChanged -= _onStateChanged;
        _filterTimer.Stop();
    }

    private async Task AutoReloadAsync()
    {
        await Task.Delay(500);
        await LoadAsync(force: true);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_core.Api is null || _core.State != CoreState.Running)
        {
            _dispatcher.TryEnqueue(() => StatusText = "内核未运行");
            return;
        }
        if (_all.Count > 0 && !force) return;

        _dispatcher.TryEnqueue(() => IsBusy = true);
        try
        {
            _all = await _core.Api.GetRulesAsync().ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                _filterTimer.Stop();
                _rowCache.Clear();
                Apply();
                StatusText = $"共 {_all.Count} 条规则";
                IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
            {
                StatusText = $"加载失败：{ex.Message}";
                IsBusy = false;
            });
        }
    }

    private void Apply()
    {
        var keyword = Filter?.Trim() ?? "";
        var rows = _filterRows;
        rows.Clear();

        if (keyword.Length == 0)
        {
            foreach (var r in _all) rows.Add(GetRow(r));
        }
        else
        {
            foreach (var r in _all)
            {
                if (!r.Payload.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    && !r.Type.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    && !r.Proxy.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(GetRow(r));
            }
        }

        SyncItems(rows);
    }

    private RuleRowViewModel GetRow(RuleItem item)
    {
        if (_rowCache.TryGetValue(item.Index, out var row)) return row;
        row = new RuleRowViewModel(item);
        _rowCache[item.Index] = row;
        return row;
    }

    private void SyncItems(List<RuleRowViewModel> target)
    {
        if (Items.Count == target.Count)
        {
            var same = true;
            for (var i = 0; i < target.Count; i++)
            {
                if (ReferenceEquals(Items[i], target[i])) continue;
                same = false;
                break;
            }
            if (same) return;
        }

        var targetSet = new HashSet<RuleRowViewModel>(target);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(Items[i])) Items.RemoveAt(i);
        }

        var pos = new Dictionary<RuleRowViewModel, int>(Items.Count);
        for (var i = 0; i < Items.Count; i++) pos[Items[i]] = i;

        for (var i = 0; i < target.Count; i++)
        {
            var row = target[i];
            if (i < Items.Count && ReferenceEquals(Items[i], row)) continue;

            if (pos.TryGetValue(row, out var existing))
            {
                Items.Move(existing, i);
                var lo = Math.Min(existing, i);
                var hi = Math.Max(existing, i);
                for (var k = lo; k <= hi; k++) pos[Items[k]] = k;
            }
            else
            {
                Items.Insert(i, row);
                for (var k = i; k < Items.Count; k++) pos[Items[k]] = k;
            }
        }
    }

    private void ScheduleFilter()
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    /// <summary>临时禁用的规则（运行态删除，配置不变；内核重载配置或重启后恢复）。</summary>
    public IReadOnlyList<int> DisabledIndexes { get; private set; } = Array.Empty<int>();

    /// <summary>是否有临时禁用的规则（页头显示"恢复全部"）。</summary>
    public bool HasDisabled => DisabledIndexes.Count > 0;

    [RelayCommand]
    private async Task ToggleRuleAsync(RuleRowViewModel? row)
    {
        if (row is null || _core.Api is null) return;
        try
        {
            // 删除规则后索引会前移，这里先禁用、再全量重拉以刷新索引。
            await _core.Api.DeleteRuleAsync(row.Index).ConfigureAwait(false);
            var remaining = new List<int>(DisabledIndexes) { row.Index };
            // 续体在线程池,绑定属性必须在 UI 线程更新。
            _dispatcher.TryEnqueue(() =>
            {
                DisabledIndexes = remaining;
                OnPropertyChanged(nameof(HasDisabled));
            });
            await LoadAsync(force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => StatusText = $"禁用失败：{ex.Message}");
        }
    }

    /// <summary>恢复全部临时禁用：重载内核配置（禁用的规则会重新生效）。</summary>
    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        if (_core.Api is null || DisabledIndexes.Count == 0) return;
        try
        {
            await _core.ReloadConfigAsync(_core.Paths.RuntimeConfigPath).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                DisabledIndexes = Array.Empty<int>();
                OnPropertyChanged(nameof(HasDisabled));
            });
            await LoadAsync(force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => StatusText = $"恢复失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(force: true);

    partial void OnFilterChanged(string value) => ScheduleFilter();
}
