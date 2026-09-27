using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Models;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class ProxyGroupViewModel : ObservableObject
{
    private readonly ICoreManager _core;
    private readonly ProxiesViewModel _owner;
    private IReadOnlyList<ProxyItemViewModel> _all = Array.Empty<ProxyItemViewModel>();

    public string Name { get; }
    public string Type { get; }
    public string TypeText { get; }

    [ObservableProperty]
    public partial ObservableCollection<ProxyItemViewModel> Nodes { get; set; } = new();

    [ObservableProperty]
    public partial string NowText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    public ProxyGroupViewModel(string name, string type, ICoreManager core, ProxiesViewModel owner)
    {
        Name = name;
        Type = type;
        _core = core;
        _owner = owner;
        TypeText = string.IsNullOrEmpty(type) ? "代理组" : type.ToLowerInvariant() switch
        {
            "selector" => "选择器",
            "urltest" => "自动测速",
            "fallback" => "故障转移",
            "loadbalance" => "负载均衡",
            "relay" => "链式代理",
            var t => t,
        };
    }

    public void SetNodes(IReadOnlyList<ProxyItemViewModel> nodes, string? now, int sortIndex)
    {
        _all = nodes;
        NowText = string.IsNullOrEmpty(now) ? "" : now;
        SortIndex = sortIndex;
        foreach (var n in _all)
        {
            n.SelectRequested = SelectNode;
            // 每个节点独立测速，而不是整组重测。
            n.TestRequested = node => _ = TestNodeAsync(node);
        }
        ApplySort();
    }

    public void ApplySort()
    {
        // 不按选中状态排序：选中项跳到首位会让点击节点时列表重排，用户失去位置感。
        var sorted = SortIndex switch
        {
            1 => _all
                    .OrderBy(n => n.IsAlive ? 0 : 1)
                    .ThenBy(n => n.Delay is null or < 0 ? int.MaxValue : n.Delay)
                    .ToList(),
            2 => _all.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => _all.ToList(),
        };

        Nodes.Clear();
        foreach (var n in sorted) Nodes.Add(n);
    }

    partial void OnSortIndexChanged(int value) => ApplySort();

    public async void SelectNode(ProxyItemViewModel? node)
    {
        if (node is null || _core.Api is null) return;
        try
        {
            await _core.Api.SelectProxyAsync(Name, node.Name).ConfigureAwait(false);
            if (_owner.AutoCloseConnection)
                await _owner.CloseConnectionsSafeAsync().ConfigureAwait(false);
            await ProxySelectionStore
                .RememberAsync(global::Momomi.App.AppHost.Host.Settings, Name, node.Name)
                .ConfigureAwait(false);
            _owner.Dispatcher.TryEnqueue(() =>
            {
                foreach (var n in _all) n.SetSelected(n.Name == node.Name);
                NowText = node.Name;
                _owner.ReportStatus($"已切换到 {node.Name}");
                // 本页已就地更新选中态，广播时让自己跳过刷新，避免整页重建。
                _owner.RaiseProxiesChangedExceptSelf();
            });
        }
        catch (Exception ex)
        {
            _owner.ReportStatus($"切换失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (_core.Api is null) return;
        IsBusy = true;
        try
        {
            // 限流并发逐节点测速：每个返回即刷新，不必等全部完成。
            using var gate = new SemaphoreSlim(16);
            var tasks = _all.Select(async n =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await TestNodeAsync(n).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            _owner.Dispatcher.TryEnqueue(() =>
            {
                if (SortIndex == 1) ApplySort();
                _owner.ReportStatus($"{Name} 测速完成");
                IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            _owner.Dispatcher.TryEnqueue(() =>
            {
                foreach (var n in _all) n.IsTesting = false;
                IsBusy = false;
                _owner.ReportStatus($"测速失败：{ex.Message}");
            });
        }
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    /// <summary>组内全部节点（不随排序变化）。</summary>
    public IReadOnlyList<ProxyItemViewModel> AllNodes => _all;

    public void SetBusy(bool value) => IsBusy = value;

    /// <summary>测单个节点，只更新该节点的延迟。</summary>
    public async Task TestNodeAsync(ProxyItemViewModel node)
    {
        if (_core.Api is null || node.IsTesting) return;
        node.IsTesting = true;
        try
        {
            var delay = await _core.Api
                .ProxyDelayAsync(node.Name, _owner.DelayTestUrl, _owner.DelayTestTimeoutMs)
                .ConfigureAwait(false);
            _owner.Dispatcher.TryEnqueue(() =>
            {
                node.IsTesting = false;
                node.Delay = delay;
                node.IsAlive = delay > 0;
            });
        }
        catch
        {
            _owner.Dispatcher.TryEnqueue(() =>
            {
                node.IsTesting = false;
                node.Delay = 0;
                node.IsAlive = false;
            });
        }
    }
}

public sealed partial class ProxiesViewModel : ObservableObject
{
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private bool _loaded;
    private IReadOnlyDictionary<string, ProxyItem> _proxies =
        new Dictionary<string, ProxyItem>();
    private readonly EventHandler<string> _onModeChanged;
    private readonly EventHandler<CoreStateChanged> _onStateChanged;

    public DispatcherQueue Dispatcher => _dispatcher;

    public ObservableCollection<ProxyGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "未加载";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial bool IsGlobalMode { get; set; }

    /// <summary>当前延迟测试参数（供组内节点测速读取）。</summary>
    public string DelayTestUrl { get; private set; } = DelayTestSettings.DefaultUrl;
    public int DelayTestTimeoutMs { get; private set; } = DelayTestSettings.DefaultTimeoutMs;
    public int DelayTestConcurrency { get; private set; } = DelayTestSettings.DefaultConcurrency;

    /// <summary>切换节点后是否自动关闭旧连接。</summary>
    public bool AutoCloseConnection { get; private set; } = true;

    public async Task ReloadDelayTestAsync()
    {
        var settings = global::Momomi.App.AppHost.Host.Settings;
        var (url, timeout, concurrency) = await DelayTestSettings.ReadAsync(settings).ConfigureAwait(false);
        DelayTestUrl = url;
        DelayTestTimeoutMs = timeout;
        DelayTestConcurrency = concurrency;
        AutoCloseConnection = await settings.GetBoolAsync("core.autoCloseConnection", true).ConfigureAwait(false);
    }

    public ProxiesViewModel(ICoreManager core, DispatcherQueue dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        IsGlobalMode = ModeState.IsGlobal;

        _onModeChanged = (_, mode) => ApplyMode(mode);
        _onStateChanged = (_, e) =>
        {
            if (e.State != CoreState.Running) _loaded = false;
            else _ = AutoReloadAsync();
        };
        _onSignals = (_, _) =>
        {
            // 自己发起的广播（如切换节点）已就地更新，跳过整页重建。
            if (_suppressSelfReload) return;
            _dispatcher.TryEnqueue(() => _ = LoadAsync(force: true));
        };
    }

    private readonly EventHandler _onSignals;
    private bool _suppressSelfReload;

    /// <summary>广播代理数据变更，但让本页忽略这次广播（用于本页已就地更新的场景）。</summary>
    public void RaiseProxiesChangedExceptSelf()
    {
        _suppressSelfReload = true;
        try
        {
            AppSignals.RaiseProxiesChanged();
        }
        finally
        {
            _suppressSelfReload = false;
        }
    }

    private bool _attached;
    private bool _loadingSort;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        ModeState.Changed += _onModeChanged;
        _core.StateChanged += _onStateChanged;
        AppSignals.ProxiesChanged += _onSignals;
        _ = LoadSortAsync();
        _ = ReloadDelayTestAsync();
    }

    private async Task LoadSortAsync()
    {
        try
        {
            var settings = global::Momomi.App.AppHost.Host.Settings;
            var saved = await settings.GetIntAsync("ui.proxySort", 0).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                _loadingSort = true;
                try
                {
                    SortIndex = Math.Clamp(saved, 0, 2);
                }
                finally
                {
                    _loadingSort = false;
                }
            });
        }
        catch
        {
        }
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        ModeState.Changed -= _onModeChanged;
        _core.StateChanged -= _onStateChanged;
        AppSignals.ProxiesChanged -= _onSignals;
    }

    public void ReportStatus(string text) => _dispatcher.TryEnqueue(() => StatusText = text);

    public void ApplyMode(string mode)
    {
        var isGlobal = string.Equals(mode, "global", StringComparison.OrdinalIgnoreCase);
        _dispatcher.TryEnqueue(() =>
        {
            if (IsGlobalMode == isGlobal) return;
            IsGlobalMode = isGlobal;
            if (_loaded) Rebuild();
        });
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
            StatusText = "内核未运行";
            return;
        }
        if (_loaded && !force) return;

        IsBusy = true;
        try
        {
            _proxies = await _core.Api.GetProxiesAsync().ConfigureAwait(false);

            _dispatcher.TryEnqueue(() =>
            {
                Rebuild();
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

    private void Rebuild()
    {
        Groups.Clear();

        var declared = MihomoConfigBuilder.ReadProxyGroupOrder(_core.Paths.RuntimeConfigPath);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < declared.Count; i++) rank[declared[i]] = i;

        var ordered = _proxies.Values
            .Where(p => p.All is { Count: > 0 })
            .OrderBy(p => rank.TryGetValue(p.Name, out var r) ? r : int.MaxValue)
            .ToList();

        foreach (var p in ordered)
        {
            if (!IsGlobalMode && string.Equals(p.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase))
                continue;

            var group = new ProxyGroupViewModel(p.Name, p.Type, _core, this);
            group.SetNodes(BuildNodes(p.Name, p.All!, _proxies, p.Now), p.Now, SortIndex);
            Groups.Add(group);
        }

        HasData = Groups.Count > 0;
        StatusText = $"共 {Groups.Count} 个代理组";
    }

    private static List<ProxyItemViewModel> BuildNodes(
        string groupName,
        IReadOnlyList<string> members,
        IReadOnlyDictionary<string, ProxyItem> proxies,
        string? now)
    {
        var nodes = new List<ProxyItemViewModel>();
        foreach (var member in members)
        {
            if (!proxies.TryGetValue(member, out var p)) continue;
            nodes.Add(new ProxyItemViewModel(p.Name, p.Type, p.Alive, p.Delay, now == member, groupName: groupName));
        }
        return nodes;
    }

    public async Task SelectNodeAsync(string groupName, ProxyItemViewModel node)
    {
        var group = Groups.FirstOrDefault(g => g.Name == groupName);
        if (group is not null) group.SelectNode(node);
        await Task.CompletedTask;
    }

    /// <summary>关闭所有连接（切换节点后清理旧连接）。失败不抛出。</summary>
    public async Task CloseConnectionsSafeAsync()
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

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(force: true);

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (_core.Api is null) return;
        IsBusy = true;
        try
        {
            // 按节点名去重：同一物理节点在多个组里只测一次，
            // 避免重复并发请求互相竞争导致结果不一致。
            var byName = new Dictionary<string, List<ProxyItemViewModel>>(StringComparer.Ordinal);
            foreach (var group in Groups)
            {
                foreach (var node in group.AllNodes)
                {
                    if (!byName.TryGetValue(node.Name, out var list))
                    {
                        list = new List<ProxyItemViewModel>();
                        byName[node.Name] = list;
                    }
                    list.Add(node);
                }
            }

            var names = byName.Keys.ToList();
            foreach (var g in Groups) g.SetBusy(true);

            using var gate = new SemaphoreSlim(Math.Max(1, DelayTestConcurrency));
            var tasks = names.Select(async name =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var delay = await TestOnceAsync(name).ConfigureAwait(false);
                    _dispatcher.TryEnqueue(() =>
                    {
                        foreach (var node in byName[name])
                        {
                            node.IsTesting = false;
                            node.Delay = delay;
                            node.IsAlive = delay > 0;
                        }
                    });
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            _dispatcher.TryEnqueue(() =>
            {
                foreach (var g in Groups) g.SetBusy(false);
                foreach (var g in Groups) if (g.SortIndex == 1) g.ApplySort();
                StatusText = $"测速完成（{names.Count} 个节点）";
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>对单个物理节点发起一次测速，返回延迟（0 表示超时/失败）。</summary>
    private async Task<int> TestOnceAsync(string name)
    {
        try
        {
            return await _core.Api!
                .ProxyDelayAsync(name, DelayTestUrl, DelayTestTimeoutMs)
                .ConfigureAwait(false);
        }
        catch
        {
            return 0;
        }
    }

    partial void OnSortIndexChanged(int value)
    {
        foreach (var g in Groups) g.SortIndex = value;
        if (_loadingSort) return;
        _ = global::Momomi.App.AppHost.Host.Settings.SetIntAsync("ui.proxySort", value);
    }
}
