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
            // 每个节点独立测速，而不是整组重测。单点测速完成后广播，让迷你面板同步延迟。
            n.TestRequested = node => _ = TestNodeAsync(node, broadcast: true);
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

                // 同步快照里的 Now：ApplyGroupDelaysFromMembers 靠它判断组的当前节点，
                // 不同步的话切换后引用的还是旧节点，其它组里「手动」会一直显示旧延迟。
                _owner.SetGroupNow(Name, node.Name);

                _owner.ReportStatus($"已切换到 {node.Name}");

                // 组的当前节点变了，引用它的组（含嵌套组）显示值要立刻跟着变。
                _owner.ApplyGroupDelaysFromMembers();

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
        if (_core.Api is null || IsBusy) return;
        IsBusy = true;
        try
        {
            // 统一走 DelayTester：并发受控 + 每节点单次测量，与全部测速/单点测速一致。
            // 结果按 200ms 批量写回，避免 N 次跨线程调度把 UI 拖住。
            _owner.Dispatcher.TryEnqueue(() => { foreach (var n in _all) n.IsTesting = true; });
            var names = _all.Select(n => n.Name).ToList();
            var scope = new HashSet<ProxyItemViewModel>(_all);
            var batch = new DelayBatch(_owner.Dispatcher, results =>
            {
                _owner.ApplyDelayBatch(results, scope);
                DelayCache.SetMany(results);
            });
            try
            {
                // 按当前模式分派：TCP 模式直连节点入口，默认模式走内核 URL 测速。
                await _owner.MeasureNodesAsync(
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

            _owner.Dispatcher.TryEnqueue(() =>
            {
                foreach (var n in _all) n.IsTesting = false;
                if (SortIndex == 1) ApplySort();
                _owner.ReportStatus($"{Name} 测速完成");
                IsBusy = false;
                // 整组测速结束后统一广播一次，让迷你面板同步延迟。
                _owner.RaiseProxiesChangedExceptSelf();
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

    /// <summary>测单个节点，并把延迟同步到所有分组的同名节点。</summary>
    public async Task TestNodeAsync(ProxyItemViewModel node, bool broadcast = false)
    {
        if (_core.Api is null || node.IsTesting) return;
        node.IsTesting = true;
        var delay = await _owner.MeasureNodeAsync(node.Name).ConfigureAwait(false);
        _owner.Dispatcher.TryEnqueue(() =>
        {
            node.IsTesting = false;
            node.Delay = delay;
            node.IsAlive = delay > 0;
            // 单点测速同样进全局缓存，否则只有当前面板能看到结果。
            DelayCache.Set(node.Name, delay);
            _owner.SyncNodeDelay(node.Name, delay, delay > 0);
            // 该节点变了，引用它的组（含嵌套组）的显示值也要跟着更新。
            _owner.ApplyGroupDelaysFromMembers();
            if (SortIndex == 1) ApplySort();
            // 单点测速时广播一次，让迷你面板同步延迟（整组测速由 TestAsync 统一广播）。
            if (broadcast) _owner.RaiseProxiesChangedExceptSelf();
        });
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

    /// <summary>是否只测 TCP 握手（设置项）。开启后节点测速改为直连节点 server:port。</summary>
    public bool DelayTestTcpOnly { get; private set; } = DelayTestSettings.DefaultTcpOnly;

    /// <summary>节点名 → 可直连地址，从 runtime.yaml 读取，供 TCP 模式使用。</summary>
    private IReadOnlyDictionary<string, ProxyEndpoint> _endpoints =
        new Dictionary<string, ProxyEndpoint>();

    /// <summary>切换节点后是否自动关闭旧连接。</summary>
    public bool AutoCloseConnection { get; private set; } = true;

    public async Task ReloadDelayTestAsync()
    {
        var settings = global::Momomi.App.AppHost.Host.Settings;
        var (url, timeout, concurrency, tcpOnly) = await DelayTestSettings.ReadAsync(settings).ConfigureAwait(false);
        DelayTestUrl = url;
        DelayTestTimeoutMs = timeout;
        DelayTestConcurrency = concurrency;
        DelayTestTcpOnly = tcpOnly;
        AutoCloseConnection = await settings.GetBoolAsync("core.autoCloseConnection", true).ConfigureAwait(false);

        // 节点地址只在 TCP 模式下需要；读整份 runtime.yaml 有解析成本，
        // 走 Task.Run 挪出 UI 线程，并按文件时间戳缓存。
        if (tcpOnly)
        {
            _endpoints = await Task.Run(() => MihomoConfigBuilder.ReadProxyEndpoints(
                _core.Paths.RuntimeConfigPath)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 取节点地址表，必要时现读。
    ///
    /// 不能只依赖 <see cref="ReloadDelayTestAsync"/> 里的加载：用户在设置页打开开关时，
    /// 代理页可能早就建好了（Attach 只跑一次），此时 _endpoints 还是空的，
    /// 结果就是「开关看起来没生效、所有节点仍走 URL 测速」。
    /// 这里按需补一次，读失败也只是回退，不会抛。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ProxyEndpoint>> GetEndpointsAsync()
    {
        if (_endpoints.Count > 0) return _endpoints;

        var loaded = await Task.Run(() => MihomoConfigBuilder.ReadProxyEndpoints(
            _core.Paths.RuntimeConfigPath)).ConfigureAwait(false);
        _endpoints = loaded;
        return loaded;
    }

    /// <summary>
    /// 测一组节点的延迟，按当前模式分派：
    /// - TCP 模式：直连各节点 server:port 量握手；没有可直连地址的节点（QUIC 类协议）
    ///   回退到内核测速，保证每个节点都有结果。
    /// - 默认模式：走内核 API 的 URL 测速。
    /// </summary>
    internal async Task MeasureNodesAsync(
        IReadOnlyCollection<string> names,
        Func<string, int, Task> onMeasured,
        CancellationToken ct = default)
    {
        if (_core.Api is null) return;

        // 并发不该超过目标数：对 5 个节点开 50 并发没有任何好处，
        // 只会让它们同时抢连接，读数互相污染（实测并发下同一节点会从 178ms 跳到 56ms）。
        var concurrency = EffectiveConcurrency(names.Count);

        if (!DelayTestTcpOnly)
        {
            await DelayTester.MeasureAllAsync(
                _core.Api, names, DelayTestUrl, DelayTestTimeoutMs, concurrency, onMeasured, ct)
                .ConfigureAwait(false);
            return;
        }

        var endpoints = await GetEndpointsAsync().ConfigureAwait(false);
        var tcpNames = names.Where(endpoints.ContainsKey).ToList();
        var fallback = names.Where(n => !endpoints.ContainsKey(n)).ToList();

        await DelayTester.MeasureAllTcpAsync(
            tcpNames, endpoints, DelayTestTimeoutMs, concurrency, onMeasured, ct)
            .ConfigureAwait(false);

        if (fallback.Count > 0)
        {
            await DelayTester.MeasureAllAsync(
                _core.Api, fallback, DelayTestUrl, DelayTestTimeoutMs, concurrency, onMeasured, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 实际使用的并发数。
    ///
    /// 两条约束：
    /// 1. 不超过目标数——多出来的名额只是让任务空转；
    /// 2. 小规模时收敛到 10——节点少的时候高并发不会更快，反而互相干扰。
    ///    实测 5 个节点开高并发，同一节点读数会在 56ms 与 178ms 之间跳。
    /// </summary>
    private int EffectiveConcurrency(int targetCount)
    {
        if (targetCount <= 0) return 1;
        var cap = DelayTestConcurrency;
        if (targetCount <= 20) cap = Math.Min(cap, 10);
        return Math.Max(1, Math.Min(cap, targetCount));
    }

    /// <summary>测单个节点（按当前模式）。策略组与 DIRECT/REJECT 直接跳过。</summary>
    internal async Task<int> MeasureNodeAsync(string name, CancellationToken ct = default)
    {
        if (_core.Api is null) return 0;
        if (_proxies.TryGetValue(name, out var item) && !IsTestableNode(item)) return 0;

        if (DelayTestTcpOnly)
        {
            var endpoints = await GetEndpointsAsync().ConfigureAwait(false);
            if (endpoints.TryGetValue(name, out var ep))
                return await DelayTester.MeasureTcpAsync(ep.Server, ep.Port, DelayTestTimeoutMs, ct)
                    .ConfigureAwait(false);
        }

        return await DelayTester
            .MeasureAsync(_core.Api, name, DelayTestUrl, DelayTestTimeoutMs, ct)
            .ConfigureAwait(false);
    }

    public ProxiesViewModel(ICoreManager core, DispatcherQueue dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        IsGlobalMode = ModeState.IsGlobal;

        _onModeChanged = (_, mode) => ApplyMode(mode);
        _onStateChanged = (_, e) =>
        {
            // StateChanged 多数来自线程池（StartAsync 全程 ConfigureAwait(false)、
            // watchdog、Process.Exited 回调）。AutoReloadAsync → LoadAsync 会直接写
            // StatusText/IsBusy 这些被 x:Bind 绑定的属性，不回到 UI 线程就是跨线程改控件。
            if (e.State != CoreState.Running) _loaded = false;
            else _dispatcher.TryEnqueue(() => _ = AutoReloadAsync());
        };
        _onSignals = (_, _) =>
        {
            // 自己发起的广播（如切换节点）已就地更新，跳过整页重建。
            if (_suppressSelfReload) return;
            _dispatcher.TryEnqueue(() => _ = LoadAsync(force: true));
        };
        _onDelayCached = (_, name) => _dispatcher.TryEnqueue(() => ApplyCachedDelay(name));
    }

    private readonly EventHandler<string> _onDelayCached;

    /// <summary>
    /// 把全局缓存里的新值写进本面板对应节点。
    ///
    /// 这是「在迷你面板测的速，主面板也要变」的实现：不重新拉内核
    /// （那会丢掉客户端测出来的值，策略组在快照里恒为 null），
    /// 只按名字更新现有节点实例，然后重算组继承。
    /// </summary>
    private void ApplyCachedDelay(string name)
    {
        var delay = DelayCache.Get(name);
        if (delay is null) return;
        var alive = DelayCache.GetAlive(name) ?? delay > 0;

        var touched = false;
        foreach (var group in Groups)
        {
            foreach (var node in group.AllNodes)
            {
                if (!string.Equals(node.Name, name, StringComparison.Ordinal)) continue;
                node.Delay = delay;
                node.IsAlive = alive;
                touched = true;
            }
        }

        if (!touched) return;

        ApplyGroupDelaysFromMembers();
        foreach (var g in Groups)
            if (g.SortIndex == 1) g.ApplySort();
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
        DelayCache.Updated += _onDelayCached;
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
        DelayCache.Updated -= _onDelayCached;
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
            _dispatcher.TryEnqueue(() => StatusText = "内核未运行");
            return;
        }
        if (_loaded && !force) return;

        _dispatcher.TryEnqueue(() => IsBusy = true);
        try
        {
            _proxies = await _core.Api.GetProxiesAsync().ConfigureAwait(false);

            // 组顺序要读并解析 runtime.yaml，放到后台取，避免在 UI 线程上做这件事。
            var order = await Task.Run(
                () => MihomoConfigBuilder.ReadProxyGroupOrder(_core.Paths.RuntimeConfigPath))
                .ConfigureAwait(false);

            _dispatcher.TryEnqueue(() =>
            {
                Rebuild(order);
                _loaded = true;
                IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => StatusText = $"加载失败：{ex.Message}");
            _dispatcher.TryEnqueue(() => IsBusy = false);
        }
    }

    private void Rebuild(IReadOnlyList<string>? declaredOrder = null)
    {
        Groups.Clear();

        var declared = declaredOrder
            ?? MihomoConfigBuilder.ReadProxyGroupOrder(_core.Paths.RuntimeConfigPath);
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

        // 重建后立刻补齐组的延迟：BuildNodes 只能给真实节点带上缓存值，
        // 策略组条目（「稳定」组里的「手动」）在 _proxies 里恒为 null，
        // 必须在这里沿 now 链继承一次，否则每次刷新都会变回「—」。
        ApplyGroupDelaysFromMembers();
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

            // 全局缓存优先：内核快照里的 delay 可能是旧的（甚至策略组根本没有值），
            // 而缓存记录的是最近一次真实测速结果，跨面板一致。
            var cached = DelayCache.Get(p.Name);
            var delay = cached ?? p.Delay;
            var alive = DelayCache.GetAlive(p.Name) ?? p.Alive;

            nodes.Add(new ProxyItemViewModel(p.Name, p.Type, alive, delay, now == member, groupName: groupName));
        }
        return nodes;
    }

    /// <summary>
    /// 该条目是否可独立测速。
    ///
    /// 组的 members 里会混进不该单独测的东西：
    /// 1. 嵌套的策略组（如「稳定」的成员里有「手动」）——Selector 没有自己的地址，
    ///    对它发 delay 会递归测它的 now，和直接测那个节点重复，纯属浪费并发；
    /// 2. REJECT / PASS 这类内置目标——没有可连的服务器，测了必然超时。
    ///
    /// 但 DIRECT 要测：它代表「直连」，有真实的本地网络路径可量，
    /// 用户也需要知道直连延迟来和代理节点对比。内核支持对 DIRECT 做 delay。
    /// </summary>
    private static bool IsTestableNode(ProxyItem p)
        => p.All is not { Count: > 0 }
           && !string.Equals(p.Type, "Reject", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(p.Type, "Pass", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(p.Type, "Compatible", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(p.Type, "RejectDrop", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(p.Type, "PassRule", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// 更新快照里某个组的当前节点。
    ///
    /// _proxies 是 GetProxiesAsync 的快照，切换节点后不会自动变。而
    /// <see cref="ApplyGroupDelaysFromMembers"/> 靠 self.Now 判断组的当前节点，
    /// 不同步就会拿着旧节点去继承延迟。
    /// </summary>
    internal void SetGroupNow(string groupName, string nodeName)
    {
        if (!_proxies.TryGetValue(groupName, out var item)) return;
        if (string.Equals(item.Now, nodeName, StringComparison.Ordinal)) return;

        var updated = new Dictionary<string, ProxyItem>(_proxies, StringComparer.Ordinal)
        {
            [groupName] = item with { Now = nodeName },
        };
        _proxies = updated;
    }

    /// <summary>
    /// 把单个节点的测速结果同步到所有分组里同名的节点（同一物理节点可能同时出现在多个组）。
    /// 只更新延迟与存活，不动 IsTesting，避免干扰其他分组并行中的测速。
    /// </summary>
    public void SyncNodeDelay(string name, int delay, bool alive)
    {
        foreach (var group in Groups)
        {
            foreach (var node in group.AllNodes)
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal))
                {
                    node.IsAlive = alive;
                    node.Delay = delay;
                }
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(force: true);

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (_core.Api is null || IsBusy) return;
        IsBusy = true;
        try
        {
            // 只测真实节点：策略组（嵌套组）与 DIRECT/REJECT 没有自己的地址，
            // 对它们发请求只会得到超时。组的延迟稍后由其 now 指向的节点结果填充。
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var nodes = new List<ProxyItemViewModel>();
            foreach (var group in Groups)
            {
                foreach (var node in group.AllNodes)
                {
                    nodes.Add(node);
                    if (!_proxies.TryGetValue(node.Name, out var item)) continue;
                    if (!IsTestableNode(item)) continue;
                    if (seen.Add(node.Name)) names.Add(node.Name);
                }
            }

            foreach (var g in Groups) g.SetBusy(true);
            // 测速期间只为真正要测的节点点亮旋转动画。
            _dispatcher.TryEnqueue(() =>
            {
                foreach (var node in nodes)
                {
                    if (_proxies.TryGetValue(node.Name, out var item) && IsTestableNode(item))
                        node.IsTesting = true;
                }
            });

            // 统一走 DelayTester：并发受控 + 每节点单次测量，与单点/组测速一致。
            // 结果批量写回，节点多时才不会卡在界面更新上。
            var batch = new DelayBatch(_dispatcher, results =>
            {
                ApplyDelayBatch(results);
                // 写入全局缓存：迷你面板与其它视图都从这里取，保证跨面板一致。
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
                // 组的延迟沿用其 now 指向的节点，不再对组本身发请求。
                ApplyGroupDelaysFromMembers();

                // 取消/异常时可能有节点没走到回调，统一收掉旋转动画。
                foreach (var g in Groups)
                {
                    g.SetBusy(false);
                    foreach (var n in g.AllNodes) n.IsTesting = false;
                    if (g.SortIndex == 1) g.ApplySort();
                }
                StatusText = $"测速完成（{names.Count} 个节点）";
                IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
            {
                foreach (var g in Groups)
                {
                    g.SetBusy(false);
                    foreach (var n in g.AllNodes) n.IsTesting = false;
                }
                StatusText = $"测速失败：{ex.Message}";
                IsBusy = false;
            });
        }
    }

    /// <summary>
    /// 组的延迟沿用其 now 指向的节点：组本身没有地址，对它单独测速既慢又容易假超时。
    ///
    /// 分两步，不能一步到位：
    /// 1. 先算出每个组的「有效延迟」（递归沿 now 链找到最终的真实节点）；
    /// 2. 再把结果回填到**所有**引用该组的位置。
    ///
    /// 之所以要第二步：组的成员里除了节点还有别的组（「直连」组里有「手动」，
    /// 「漏网」组里有「直连」）。只填 now 指向的那一个条目的话，这些嵌套组成员
    /// 会一直是「—」，看起来像没继承到结果。
    /// </summary>
    internal void ApplyGroupDelaysFromMembers()
    {
        // 第 1 步：算出每个组的有效延迟。
        var resolved = new Dictionary<string, (int? Delay, bool Alive)>(StringComparer.Ordinal);
        foreach (var group in Groups)
            ResolveGroupDelay(group, resolved, new HashSet<string>(StringComparer.Ordinal));

        // 第 2 步：把结果写回所有条目——节点用自身测速值，组用上面算出的值。
        foreach (var group in Groups)
        {
            foreach (var node in group.AllNodes)
            {
                if (!_proxies.TryGetValue(node.Name, out var item)) continue;

                if (IsTestableNode(item))
                {
                    // 真实节点：界面上已有测速结果，保持不动。
                    continue;
                }

                // 策略组或 REJECT/PASS 之类：能用解析结果就用，否则留空。
                if (resolved.TryGetValue(node.Name, out var r))
                {
                    node.Delay = r.Delay;
                    node.IsAlive = r.Alive;
                }
            }
        }
    }

    /// <summary>
    /// 递归求一个组的有效延迟：沿 now 链找到最终的真实节点，取它的延迟。
    /// 返回 false 表示链断了（指向 REJECT、空 now、或循环引用）。
    /// </summary>
    private bool ResolveGroupDelay(
        ProxyGroupViewModel group,
        Dictionary<string, (int? Delay, bool Alive)> resolved,
        HashSet<string> visiting)
    {
        if (resolved.TryGetValue(group.Name, out var cached))
            return cached.Delay is not null;

        if (!visiting.Add(group.Name)) return false;

        try
        {
            if (!_proxies.TryGetValue(group.Name, out var self)) return false;
            var now = self.Now;
            if (string.IsNullOrEmpty(now)) return false;

            // now 指向真实节点（含 DIRECT）：优先取全局缓存，其次取界面上的值。
            //
            // 全局缓存是主面板与迷你面板的唯一数据源：内核的 /proxies 不返回
            // 策略组延迟，而客户端测速结果原本只留在发起测速的那个面板里，
            // 于是「在迷你面板测的速主面板看不到」。缓存解决了这个问题。
            if (_proxies.TryGetValue(now, out var target) && IsTestableNode(target))
            {
                var cachedDelay = DelayCache.Get(now);
                if (cachedDelay is not null)
                {
                    resolved[group.Name] = (cachedDelay, cachedDelay > 0);
                    return true;
                }

                var fresh = FindNodeByName(now);
                if (fresh is null) return false;
                resolved[group.Name] = (fresh.Delay, fresh.IsAlive);
                return fresh.Delay is not null;
            }

            // now 指向另一个组：递归求那个组，再继承它的结果。
            var inner = Groups.FirstOrDefault(g => string.Equals(g.Name, now, StringComparison.Ordinal));
            if (inner is null) return false;
            if (!ResolveGroupDelay(inner, resolved, visiting)) return false;

            resolved[group.Name] = resolved[inner.Name];
            return true;
        }
        finally
        {
            visiting.Remove(group.Name);
        }
    }

    /// <summary>在界面上按名字找节点实例（同名节点可能出现在多个组，取第一个）。</summary>
    private ProxyItemViewModel? FindNodeByName(string name)
    {
        foreach (var group in Groups)
        {
            foreach (var node in group.AllNodes)
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal)) return node;
            }
        }
        return null;
    }

    /// <summary>
    /// 把一批测速结果写到界面：更新同名节点延迟（同一物理节点可能出现在多个组）。
    /// 由 <see cref="DelayBatch"/> 合批后在 UI 线程调用；排序留到整批结束后统一做，
    /// 免得每 200ms 就把列表重排一次。
    /// </summary>
    /// <param name="scope">
    /// 本次测速覆盖的节点：只有这些节点会结束旋转动画。为空表示整页所有节点（全部测速）。
    /// 组测速时传该组的节点，避免把另一个正在测速的组的进度环提前关掉。
    /// </param>
    internal void ApplyDelayBatch(
        IReadOnlyDictionary<string, int> results, HashSet<ProxyItemViewModel>? scope = null)
    {
        foreach (var group in Groups)
        {
            foreach (var node in group.AllNodes)
            {
                if (!results.TryGetValue(node.Name, out var delay)) continue;
                node.Delay = delay;
                node.IsAlive = delay > 0;
                if (scope is null || scope.Contains(node)) node.IsTesting = false;
            }
        }
    }

    partial void OnSortIndexChanged(int value)
    {
        foreach (var g in Groups) g.SortIndex = value;
        if (_loadingSort) return;
        _ = global::Momomi.App.AppHost.Host.Settings.SetIntAsync("ui.proxySort", value);
    }
}
