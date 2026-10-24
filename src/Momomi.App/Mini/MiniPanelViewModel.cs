using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Models;
using Momomi.Core.Services;
using Momomi.App.ViewModels;

namespace Momomi.App.Mini;

public sealed partial class MiniPanelViewModel : ObservableObject, IDisposable
{
    private readonly MomomiHost _host;
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private readonly EventHandler<TrafficSnapshot> _onTraffic;
    private readonly EventHandler<CoreStateChanged> _onState;
    private readonly EventHandler _onProxiesChanged;
    private bool _disposed;
    private IReadOnlyDictionary<string, ProxyItem> _proxies =
        new Dictionary<string, ProxyItem>();
    private bool _groupsLoaded;

    public ObservableCollection<MiniProxyGroupViewModel> Groups { get; } = new();

    public ModeSelectorViewModel ModeSelector { get; }

    private string _delayTestUrl = DelayTestSettings.DefaultUrl;
    private int _delayTestTimeoutMs = DelayTestSettings.DefaultTimeoutMs;
    private int _delayTestConcurrency = DelayTestSettings.DefaultConcurrency;
    private bool _delayTestTcpOnly = DelayTestSettings.DefaultTcpOnly;
    private IReadOnlyDictionary<string, ProxyEndpoint> _delayEndpoints =
        new Dictionary<string, ProxyEndpoint>();
    private bool _autoCloseConnection = true;

    private Task<bool> IsAutoCloseConnectionAsync() => Task.FromResult(_autoCloseConnection);

    [ObservableProperty]
    public partial string UpRate { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial string DownRate { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial string CoreStateText { get; set; } = "已停止";

    [ObservableProperty]
    public partial string StateKey { get; set; } = "stopped";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasGroups { get; set; }

    /// <summary>当前订阅名称（无则空）。</summary>
    [ObservableProperty]
    public partial string SubscriptionName { get; set; } = "";

    /// <summary>当前订阅已用流量文本（无用量字段为空）。</summary>
    [ObservableProperty]
    public partial string SubscriptionUsageText { get; set; } = "";

    /// <summary>当前订阅剩余流量占比 0-100（用于进度条）；无总量时为 0。</summary>
    [ObservableProperty]
    public partial double SubscriptionUsagePercent { get; set; }

    /// <summary>是否有订阅用量信息（无则隐藏整块）。</summary>
    [ObservableProperty]
    public partial bool HasSubscriptionUsage { get; set; }

    /// <summary>是否显示进度条（有总量才显示）。</summary>
    [ObservableProperty]
    public partial bool HasSubscriptionTotal { get; set; }

    public DispatcherQueue Dispatcher => _dispatcher;

    public MiniPanelViewModel(MomomiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _core = host.Core;
        _dispatcher = dispatcher;
        ModeSelector = new ModeSelectorViewModel(host, dispatcher);

        _onTraffic = (_, t) => _dispatcher.TryEnqueue(() =>
        {
            UpRate = Format.Rate(t.Up);
            DownRate = Format.Rate(t.Down);
        });
        _onState = (_, e) => _dispatcher.TryEnqueue(() =>
        {
            ApplyCoreState(e.State);
            if (e.State != CoreState.Running) _groupsLoaded = false;
            _ = ReloadAsync();
        });
        _onProxiesChanged = (_, _) =>
        {
            // 自己发起的广播（如切换节点）已就地更新，跳过整页重建。
            if (_suppressSelfReload) return;
            _dispatcher.TryEnqueue(() => _ = LoadAsync(force: true));
        };

        _onDelayCached = (_, name) => _dispatcher.TryEnqueue(() => ApplyCachedDelay(name));

        _core.TrafficUpdated += _onTraffic;
        _core.StateChanged += _onState;
        AppSignals.ProxiesChanged += _onProxiesChanged;
        DelayCache.Updated += _onDelayCached;

        ApplyCoreState(_core.State);
    }

    private readonly EventHandler<string> _onDelayCached;

    /// <summary>
    /// 把全局缓存里的新值写进本面板对应行。
    ///
    /// 这是「在主面板测的速，迷你面板也要变」的实现：不重新拉内核
    /// （那会丢掉客户端测出来的值），只按名字改这一行的延迟。
    /// </summary>
    private void ApplyCachedDelay(string name)
    {
        var delay = DelayCache.Get(name);
        if (delay is null) return;

        foreach (var group in Groups)
        {
            foreach (var node in group.Nodes)
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal))
                    node.Delay = delay;
            }
        }

        // 组行显示的是 now 指向的节点，节点变了组也要重算。
        ApplyGroupDelays();
    }

    /// <summary>退订所有事件，供迷你窗口关闭时调用，避免静态事件累积订阅造成泄漏。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.TrafficUpdated -= _onTraffic;
        _core.StateChanged -= _onState;
        AppSignals.ProxiesChanged -= _onProxiesChanged;
        DelayCache.Updated -= _onDelayCached;
        ModeSelector.Dispose();
    }

    private bool _suppressSelfReload;

    private void ApplyCoreState(CoreState state)
    {
        IsRunning = state == CoreState.Running;
        CoreStateText = state switch
        {
            CoreState.Running => "运行中",
            CoreState.Starting => "启动中",
            CoreState.Stopping => "停止中",
            CoreState.Error => "异常",
            _ => "已停止",
        };
        StateKey = state switch
        {
            CoreState.Running => "running",
            CoreState.Starting => "starting",
            CoreState.Stopping => "stopping",
            CoreState.Error => "error",
            _ => "stopped",
        };
    }

    public async Task LoadModeAsync() => await ModeSelector.LoadAsync().ConfigureAwait(false);

    public async Task RefreshOnShowAsync()
    {
        await ModeSelector.LoadAsync().ConfigureAwait(false);
        await ModeSelector.RefreshSwitchesAsync().ConfigureAwait(false);
        var (url, timeout, concurrency, tcpOnly) = await DelayTestSettings.ReadAsync(_host.Settings).ConfigureAwait(false);
        _delayTestUrl = url;
        _delayTestTimeoutMs = timeout;
        _delayTestConcurrency = concurrency;
        _delayTestTcpOnly = tcpOnly;
        if (tcpOnly)
        {
            _delayEndpoints = await Task.Run(() => MihomoConfigBuilder.ReadProxyEndpoints(
                _core.Paths.RuntimeConfigPath)).ConfigureAwait(false);
        }
        _autoCloseConnection = await _host.Settings.GetBoolAsync("core.autoCloseConnection", true).ConfigureAwait(false);
        await LoadSubscriptionUsageAsync().ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    /// <summary>读取当前订阅的流量用量。无 subscription-userinfo 或无用量的订阅不显示。</summary>
    private async Task LoadSubscriptionUsageAsync()
    {
        try
        {
            var active = await _host.Profiles.GetActiveAsync().ConfigureAwait(false);
            var info = active is null ? null : SubscriptionUsage.Parse(active.SubscriptionUserInfo);
            var name = active?.Name ?? "";
            _dispatcher.TryEnqueue(() => ApplySubscriptionUsage(name, info));
        }
        catch
        {
            _dispatcher.TryEnqueue(() => ApplySubscriptionUsage("", null));
        }
    }

    private void ApplySubscriptionUsage(string name, SubscriptionUsage? info)
    {
        if (info is null || (info.Total <= 0 && info.Used <= 0))
        {
            HasSubscriptionUsage = false;
            HasSubscriptionTotal = false;
            SubscriptionName = "";
            SubscriptionUsageText = "";
            SubscriptionUsagePercent = 0;
            return;
        }

        SubscriptionName = string.IsNullOrWhiteSpace(name) ? "当前订阅" : name;
        if (info.Total > 0)
        {
            SubscriptionUsageText = $"已用 {Format.Bytes(info.Used)} / {Format.Bytes(info.Total)}";
            SubscriptionUsagePercent = Math.Clamp((double)info.Used / info.Total * 100, 0, 100);
            HasSubscriptionTotal = true;
        }
        else
        {
            SubscriptionUsageText = $"已用 ↑ {Format.Bytes(info.Upload)}  ↓ {Format.Bytes(info.Download)}";
            HasSubscriptionTotal = false;
        }

        HasSubscriptionUsage = true;
    }

    public async Task SelectModeAsync(int index) => await ModeSelector.SelectAsync(index);

    public async Task SetSystemProxyAsync(bool enabled) => await ModeSelector.SetSystemProxyAsync(enabled);

    public async Task SetTunAsync(bool enabled) => await ModeSelector.SetTunAsync(enabled);

    private async Task ReloadAsync()
    {
        await Task.Delay(400).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_core.Api is null || _core.State != CoreState.Running)
        {
            _groupsLoaded = false;
            _dispatcher.TryEnqueue(() =>
            {
                if (Groups.Count > 0)
                {
                    Groups.Clear();
                    HasGroups = false;
                }
            });
            return;
        }

        if (_groupsLoaded && !force) return;

        try
        {
            var proxies = await _core.Api.GetProxiesAsync().ConfigureAwait(false);
            _proxies = proxies;

            _dispatcher.TryEnqueue(() =>
            {
                Rebuild();
                _groupsLoaded = true;
            });
        }
        catch
        {
        }
    }

    private void Rebuild()
    {
        var declared = MihomoConfigBuilder.ReadProxyGroupOrder(_core.Paths.RuntimeConfigPath);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < declared.Count; i++) rank[declared[i]] = i;

        var ordered = _proxies.Values
            .Where(p => p.All is { Count: > 0 })
            .Where(p => !string.Equals(p.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => rank.TryGetValue(p.Name, out var r) ? r : int.MaxValue)
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups) seen.Add(group.Name);

        var wanted = ordered.Select(p => p.Name).ToList();
        var stale = Groups.Where(g => !wanted.Contains(g.Name, StringComparer.Ordinal)).ToList();
        foreach (var g in stale) Groups.Remove(g);

        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var group = Groups.FirstOrDefault(g => string.Equals(g.Name, p.Name, StringComparison.Ordinal));
            if (group is null)
            {
                group = new MiniProxyGroupViewModel(p.Name);
                Groups.Insert(Math.Min(i, Groups.Count), group);
            }
            group.Fill(p.All!, _proxies, p.Now);
        }

        // 全部 Fill 完再统一填组延迟：策略组的 history 是空的（内核不记录组延迟），
        // 必须沿 now 链继承最终节点的值。放在循环里做的话，
        // 被引用的组可能还没建好/还没填值，继承会拿到 null。
        ApplyGroupDelays();

        HasGroups = Groups.Count > 0;
    }

    /// <summary>
    /// 让面板里的策略组条目继承其 now 链末端节点的延迟。
    ///
    /// 迷你面板只显示每个组的当前选中项（一行一组），所以这里只处理 Selected；
    /// 主面板的代理页有完整列表，那边的逻辑见 ProxiesViewModel.ApplyGroupDelaysFromMembers。
    /// </summary>
    private void ApplyGroupDelays()
    {
        var resolved = new Dictionary<string, int?>(StringComparer.Ordinal);

        int? Resolve(string groupName, HashSet<string> visiting)
        {
            if (resolved.TryGetValue(groupName, out var cached)) return cached;
            if (!visiting.Add(groupName)) return null;

            try
            {
                if (!_proxies.TryGetValue(groupName, out var self)) return null;
                var now = self.Now;
                if (string.IsNullOrEmpty(now)) return null;

                // now 指向真实节点：优先用面板上刚写入的实时值。
                //
                // 不能直接用 _proxies[now].Delay：那是 GetProxiesAsync 的快照，
                // 客户端测速的结果只写进了 MiniProxyNodeViewModel。两者不同步时
                // 同一个节点会显示成两个数字。
                if (_proxies.TryGetValue(now, out var target) && !IsGroup(target))
                {
                    var cachedDelay = DelayCache.Get(now);
                    if (cachedDelay is not null)
                    {
                        resolved[groupName] = cachedDelay;
                        return cachedDelay;
                    }

                    var live = FindNode(now)?.Delay;
                    var value = live ?? target.Delay;
                    resolved[groupName] = value;
                    return value;
                }

                // now 指向另一个组：递归求它。
                var value2 = Resolve(now, visiting);
                resolved[groupName] = value2;
                return value2;
            }
            finally
            {
                visiting.Remove(groupName);
            }
        }

        static bool IsGroup(ProxyItem p) => p.All is { Count: > 0 };

        foreach (var group in Groups)
        {
            var delay = Resolve(group.Name, new HashSet<string>(StringComparer.Ordinal));

            if (group.Selected is null) continue;

            // 统一走解析结果：不管这一行显示的是真实节点还是嵌套组，
            // 都用同一条 now 链末端节点的值。
            group.Selected.Delay = delay;
            group.SortByDelay();
        }
    }

    /// <summary>
    /// 在面板已有的节点里按名字找实例，取它的实时延迟。
    ///
    /// 注意：同一个物理节点会在每个组里各有一份 MiniProxyNodeViewModel 实例。
    /// 测速只更新被点的那一组里的实例，其它组里的同名实例仍是旧值。
    /// 所以这里要取「最新的一次」——不能随便返回第一个匹配。
    /// </summary>
    private MiniProxyNodeViewModel? FindNode(string name)
    {
        MiniProxyNodeViewModel? best = null;
        foreach (var group in Groups)
        {
            foreach (var node in group.Nodes)
            {
                if (!string.Equals(node.Name, name, StringComparison.Ordinal)) continue;

                // 有值优先；都有值时取更晚更新的那个由调用方保证——
                // 这里只能按「非空优先」挑，空值一定是没测过的实例。
                if (node.Delay is not null) return node;
                best ??= node;
            }
        }
        return best;
    }

    /// <summary>
    /// 把某个节点的新延迟写进所有组的同名实例，避免同节点在不同组里显示不同值。
    /// </summary>
    private void SyncNodeDelayEverywhere(string name, int? delay)
    {
        foreach (var group in Groups)
        {
            foreach (var node in group.Nodes)
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal))
                    node.Delay = delay;
            }
        }
    }

    public async Task SelectNodeAsync(MiniProxyGroupViewModel group, MiniProxyNodeViewModel node)
    {
        if (_core.Api is null) return;
        try
        {
            await _core.Api.SelectProxyAsync(group.Name, node.Name).ConfigureAwait(false);
            if (await IsAutoCloseConnectionAsync().ConfigureAwait(false))
            {
                try
                {
                    await _core.Api.CloseAllConnectionsAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
            await ProxySelectionStore.RememberAsync(_host.Settings, group.Name, node.Name).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                // 切换后该组指向的节点变了，继承的延迟要立刻跟着变，
                // 否则这一行会一直显示切换前那个节点的值。
                if (_proxies.TryGetValue(group.Name, out var item))
                {
                    var updated = new Dictionary<string, ProxyItem>(_proxies, StringComparer.Ordinal)
                    {
                        [group.Name] = item with { Now = node.Name },
                    };
                    _proxies = updated;
                }
                group.Selected = node;
                ApplyGroupDelays();

                // 本页已就地更新选中态，广播时让自己跳过刷新，避免整页重建。
                _suppressSelfReload = true;
                try
                {
                    AppSignals.RaiseProxiesChanged();
                }
                finally
                {
                    _suppressSelfReload = false;
                }
            });
        }
        catch
        {
        }
    }

    /// <summary>
    /// 测一组节点，按当前模式分派：TCP 模式直连节点入口量握手，
    /// 没有可直连地址的节点（QUIC 类协议）回退到内核测速。
    /// </summary>
    private async Task MeasureNodesAsync(
        IReadOnlyCollection<string> names,
        Func<string, int, Task> onMeasured)
    {
        if (_core.Api is null) return;

        if (!_delayTestTcpOnly)
        {
            await DelayTester.MeasureAllAsync(
                _core.Api, names, _delayTestUrl, _delayTestTimeoutMs, _delayTestConcurrency, onMeasured)
                .ConfigureAwait(false);
            return;
        }

        var endpoints = _delayEndpoints.Count > 0
            ? _delayEndpoints
            : await Task.Run(() => MihomoConfigBuilder.ReadProxyEndpoints(
                _core.Paths.RuntimeConfigPath)).ConfigureAwait(false);
        _delayEndpoints = endpoints;

        var concurrency = EffectiveConcurrency(names.Count);
        var tcpNames = names.Where(endpoints.ContainsKey).ToList();
        var fallback = names.Where(n => !endpoints.ContainsKey(n)).ToList();

        await DelayTester.MeasureAllTcpAsync(
            tcpNames, endpoints, _delayTestTimeoutMs, concurrency, onMeasured)
            .ConfigureAwait(false);

        if (fallback.Count > 0)
        {
            await DelayTester.MeasureAllAsync(
                _core.Api, fallback, _delayTestUrl, _delayTestTimeoutMs, concurrency, onMeasured)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 实际使用的并发数，与主面板保持同一套规则：
    /// 不超过目标数；目标数 ≤20 时上限 10（节点少时高并发只会互相干扰）。
    /// </summary>
    private int EffectiveConcurrency(int targetCount)
    {
        if (targetCount <= 0) return 1;
        var cap = _delayTestConcurrency;
        if (targetCount <= 20) cap = Math.Min(cap, 10);
        return Math.Max(1, Math.Min(cap, targetCount));
    }

    public async Task TestGroupAsync(MiniProxyGroupViewModel group)
    {
        if (_core.Api is null || group.IsTesting) return;
        _dispatcher.TryEnqueue(() => group.IsTesting = true);
        // 统一走 DelayTester：并发受控 + 每节点单次测量，与主面板测速逻辑一致。
        // 结果边测边刷（每 200ms 合批），节点多的组不用等到全部测完才看到数字。
        // 只就地更新、不广播：广播会让主面板整页重载，整组测完再发一次就够。
        var names = group.Nodes.Select(n => n.Name).ToList();
        // 结果同时写回本组和所有组的同名实例：同一个物理节点在每个组里各有一份
        // MiniProxyNodeViewModel，只更新本组的话，其它组里的同名实例仍是旧值。
        var batch = new DelayBatch(_dispatcher, results =>
        {
            group.ApplyMeasuredDelays(results);
            foreach (var kv in results)
                SyncNodeDelayEverywhere(kv.Key, kv.Value > 0 ? kv.Value : 0);
            // 写入全局缓存：主面板与其它视图都从这里取。
            DelayCache.SetMany(results);
        });
        try
        {
            await MeasureNodesAsync(
                names,
                (name, delay) =>
                {
                    batch.Report(name, delay);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
        finally
        {
            batch.Close();
        }

        _dispatcher.TryEnqueue(() =>
        {
            group.IsTesting = false;
            group.SortByDelay();

            // 组里节点的新延迟要传给引用它的其它组（稳定/漏网/直连/阻断里都有「手动」）。
            // 不重算的话那些行会一直显示测速前的旧值。
            ApplyGroupDelays();

            // 广播让主面板等其他视图同步延迟；自己已就地更新，跳过重载。
            _suppressSelfReload = true;
            try
            {
                AppSignals.RaiseProxiesChanged();
            }
            finally
            {
                _suppressSelfReload = false;
            }
        });
    }

    [RelayCommand]
    private async Task ToggleCoreAsync()
    {
        if (IsRunning) await _core.StopAsync();
        else await _core.StartAsync();
    }

    [ObservableProperty]
    public partial bool IsUpdatingSubscription { get; set; }

    /// <summary>刷新当前订阅并重新应用配置，供迷你面板的更新订阅按钮调用。</summary>
    [RelayCommand]
    private async Task UpdateSubscriptionAsync()
    {
        if (IsUpdatingSubscription) return;
        IsUpdatingSubscription = true;
        try
        {
            var active = await _host.Profiles.GetActiveAsync().ConfigureAwait(false);
            if (active is null || active.Kind != "url") return;

            await _host.Profiles.RefreshAsync(active.Id).ConfigureAwait(false);
            await _host.ApplyActiveProfileAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _dispatcher.TryEnqueue(() =>
            {
                IsUpdatingSubscription = false;
                _ = LoadAsync(force: true);
                _ = LoadSubscriptionUsageAsync();
            });
        }
    }
}
