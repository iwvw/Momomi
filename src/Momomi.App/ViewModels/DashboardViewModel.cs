using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Models;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private const int RateWindow = 60;

    private readonly MomomiHost _host;
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private readonly Queue<double> _upHistory = new();
    private readonly Queue<double> _downHistory = new();

    [ObservableProperty]
    public partial string UpRate { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial string DownRate { get; set; } = "0 B/s";

    [ObservableProperty]
    public partial string UpTotal { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DownTotal { get; set; } = "0 B";

    [ObservableProperty]
    public partial string Memory { get; set; } = "0 B";

    [ObservableProperty]
    public partial int ConnectionCount { get; set; }

    [ObservableProperty]
    public partial string CoreStateText { get; set; } = "已停止";

    [ObservableProperty]
    public partial string CoreVersion { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string Mode { get; set; } = "rule";

    [ObservableProperty]
    public partial string MixedPort { get; set; } = "—";

    [ObservableProperty]
    public partial string StateKey { get; set; } = "stopped";

    [ObservableProperty]
    public partial string ActiveProfileName { get; set; } = "未选择订阅";

    [ObservableProperty]
    public partial string ProfileUsageText { get; set; } = "";

    [ObservableProperty]
    public partial double ProfileUsagePercent { get; set; }

    [ObservableProperty]
    public partial bool HasProfileUsage { get; set; }

    [ObservableProperty]
    public partial string ProfileExpireText { get; set; } = "";

    [ObservableProperty]
    public partial string GroupSummary { get; set; } = "—";

    [ObservableProperty]
    public partial int NodeCount { get; set; }

    [ObservableProperty]
    public partial string SystemProxyText { get; set; } = "已关闭";

    [ObservableProperty]
    public partial string TunText { get; set; } = "已关闭";

    [ObservableProperty]
    public partial string ExitIp { get; set; } = "—";

    [ObservableProperty]
    public partial string ExitCountry { get; set; } = "";

    /// <summary>出口 IP 归属地的国旗 emoji（空表示无）。</summary>
    [ObservableProperty]
    public partial string ExitFlag { get; set; } = "";

    [ObservableProperty]
    public partial bool HasExitIp { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingExit { get; set; }

    [ObservableProperty]
    public partial bool IsTestingAll { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<double> UpPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial IReadOnlyList<double> DownPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial double MaxRate { get; set; } = 1;

    public ObservableCollection<DashboardGroupViewModel> Groups { get; } = new();
    public ObservableCollection<LatencyTargetViewModel> Targets { get; } = new();

    public string GroupSummaryFallback => "—";

    private readonly EventHandler<CoreStateChanged> _onState;
    private readonly EventHandler<TrafficSnapshot> _onTraffic;
    private readonly EventHandler<MemorySnapshot> _onMemory;
    private readonly EventHandler<ConnectionsSnapshot> _onConnections;
    private readonly EventHandler _onProxiesReloaded;
    private readonly EventHandler<bool> _onMainWindowVisibility;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string ToggleText => IsRunning ? "停止" : "启动";

    public string ToggleGlyph => IsRunning ? "\uE71A" : "\uE768";

    public string NodeCountText => $"{NodeCount} 个节点";

    partial void OnNodeCountChanged(int value) => OnPropertyChanged(nameof(NodeCountText));

    public DashboardViewModel(MomomiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _core = host.Core;
        _dispatcher = dispatcher;

        Targets.Add(new LatencyTargetViewModel("Google", "https://www.gstatic.com/generate_204"));
        Targets.Add(new LatencyTargetViewModel("Cloudflare", "https://cloudflare.com/cdn-cgi/trace"));
        Targets.Add(new LatencyTargetViewModel("GitHub", "https://github.com"));
        Targets.Add(new LatencyTargetViewModel("YouTube", "https://www.youtube.com"));

        _onState = (_, e) => _dispatcher.TryEnqueue(() => OnStateChanged(e));
        _onTraffic = (_, t) => _dispatcher.TryEnqueue(() => OnTraffic(t));
        _onMemory = (_, m) => _dispatcher.TryEnqueue(() => Memory = Format.Bytes(m.InUse));
        _onConnections = (_, c) => _dispatcher.TryEnqueue(() => ConnectionCount = c.Connections.Count);
        _onProxiesReloaded = (_, _) => _dispatcher.TryEnqueue(() => _ = LoadAllAsync());
        _onMainWindowVisibility = (_, visible) => OnMainWindowVisibility(null, visible);
    }

    private bool _attached;
    private bool _mainWindowVisible = true;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;

        _core.StateChanged += _onState;
        _core.TrafficUpdated += _onTraffic;
        _core.MemoryUpdated += _onMemory;
        _core.ConnectionsUpdated += _onConnections;
        AppSignals.ProxiesChanged += _onProxiesReloaded;
        AppSignals.MainWindowVisibilityChanged += _onMainWindowVisibility;

        // 兜底：内核刚启动代理未就绪导致检测失败时，周期重试直到出口 IP 出现。
        _refreshTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _refreshTimer.Tick += (_, _) => _ = RefreshNetworkAsync();
        if (_mainWindowVisible) _refreshTimer.Start();

        OnStateChanged(new CoreStateChanged(_core.State, _core.Version, _core.LastError));
        if (_mainWindowVisible) _ = RefreshNetworkAsync();
    }

    private Microsoft.UI.Xaml.DispatcherTimer? _refreshTimer;

    private void OnMainWindowVisibility(object? sender, bool visible)
    {
        // 主窗口隐藏（托盘 / 仅迷你面板）时暂停首页轮询与网络探测，避免无谓的请求与分配。
        _mainWindowVisible = visible;
        _dispatcher.TryEnqueue(() =>
        {
            if (_refreshTimer is null) return;
            if (visible)
            {
                _refreshTimer.Start();
                _ = RefreshNetworkAsync();
            }
            else
            {
                _refreshTimer.Stop();
            }
        });
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;

        _refreshTimer?.Stop();
        _refreshTimer = null;

        _core.StateChanged -= _onState;
        _core.TrafficUpdated -= _onTraffic;
        _core.MemoryUpdated -= _onMemory;
        _core.ConnectionsUpdated -= _onConnections;
        AppSignals.ProxiesChanged -= _onProxiesReloaded;
        AppSignals.MainWindowVisibilityChanged -= _onMainWindowVisibility;
    }

    private void OnStateChanged(CoreStateChanged e)
    {
        IsRunning = e.State == CoreState.Running;
        CoreStateText = e.State switch
        {
            CoreState.Running => "运行中",
            CoreState.Starting => "启动中",
            CoreState.Stopping => "停止中",
            CoreState.Error => "异常",
            _ => "已停止",
        };
        CoreVersion = e.Version ?? "";
        ErrorMessage = e.Error;
        StateKey = e.State switch
        {
            CoreState.Running => "running",
            CoreState.Starting => "starting",
            CoreState.Stopping => "stopping",
            CoreState.Error => "error",
            _ => "stopped",
        };
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(ToggleGlyph));
        if (IsRunning)
        {
            _ = LoadAllAsync();
            // 内核运行后补一次出口 IP / 延迟检测（带 30 秒去重，无需担心重复请求）。
            _ = RefreshNetworkAsync();
        }
        else ClearLiveData();
    }

    private void ClearLiveData()
    {
        Groups.Clear();
        GroupSummary = "—";
        NodeCount = 0;
        _upHistory.Clear();
        _downHistory.Clear();
        UpPoints = Array.Empty<double>();
        DownPoints = Array.Empty<double>();
    }

    private DateTimeOffset _lastExitCheck = DateTimeOffset.MinValue;

    /// <summary>刷新出口 IP 与目标延迟概览。每次进入首页都刷新出口 IP，避免陈旧。</summary>
    public async Task RefreshNetworkAsync()
    {
        await CheckExitIpOnceAsync().ConfigureAwait(false);
        await TestTargetsOnceAsync().ConfigureAwait(false);
    }

    private async Task CheckExitIpOnceAsync()
    {
        // 仅在内核运行时检测：此时才走代理，出口 IP 才是真实代理出口。
        // 失败不更新时间戳，DispatcherTimer（5 秒）会持续重试直到成功。
        var now = DateTimeOffset.Now;
        if (IsCheckingExit || _core.Api is null || _core.State != CoreState.Running
            || now - _lastExitCheck < TimeSpan.FromSeconds(3)) return;
        IsCheckingExit = true;
        try
        {
            var ip = await QueryExitIpAsync().ConfigureAwait(false);
            var code = await QueryCountryCodeAsync(ip).ConfigureAwait(false);
            var country = CountryNames.GetName(code);
            if (!string.IsNullOrEmpty(ip))
            {
                _lastExitCheck = now;
                _dispatcher.TryEnqueue(() =>
                {
                    ExitIp = ip;
                    ExitCountry = country;
                    ExitFlag = CountryNames.GetFlag(code);
                    HasExitIp = true;
                });
            }
        }
        catch
        {
        }
        finally
        {
            _dispatcher.TryEnqueue(() => IsCheckingExit = false);
        }
    }

    /// <summary>查国家码：直接走外部 geo 服务（内核 geoip 接口不可用，避免白等 404）。</summary>
    private async Task<string> QueryCountryCodeAsync(string ip)
    {
        var (_, _, externalCode) = await QueryGeoAsync(ip).ConfigureAwait(false);
        return externalCode;
    }

    private bool _targetsChecked;
    private async Task TestTargetsOnceAsync()
    {
        if (_targetsChecked || IsTestingAll) return;
        _targetsChecked = true;
        IsTestingAll = true;
        try
        {
            foreach (var target in Targets)
                _dispatcher.TryEnqueue(() => target.IsTesting = true);

            // 与节点测速同策略：并发受控 + 每目标多次取最小。
            await DelayTester.MeasureAllHttpAsync(
                Targets,
                t => t.Url,
                MakeLatencyClient,
                concurrency: 4,
                timeoutMs: 6000,
                (target, ms) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        target.SetResult(ms);
                        target.IsTesting = false;
                    });
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
        finally
        {
            _dispatcher.TryEnqueue(() => IsTestingAll = false);
        }
    }

    private async Task<string> QueryExitIpAsync()
    {
        using var client = MakeNetworkClient();
        foreach (var url in new[] { "https://api.ipify.org", "https://ipv4.icanhazip.com", "https://ifconfig.me/ip" })
        {
            try
            {
                var text = (await client.GetStringAsync(url).ConfigureAwait(false)).Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch
            {
            }
        }
        throw new InvalidOperationException("所有 IP 源均不可达");
    }

    private async Task<(string Country, string Region, string CountryCode)> QueryGeoAsync(string ip)
    {
        try
        {
            using var client = MakeNetworkClient();
            var json = await client.GetStringAsync($"https://ipwho.is/{ip}").ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.GetBoolean() == false) return ("", "", "");
            var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
            var region = root.TryGetProperty("region", out var r) ? r.GetString() : null;
            var code = root.TryGetProperty("country_code", out var cc) ? cc.GetString() : null;
            return (country ?? "", region ?? "", code ?? "");
        }
        catch
        {
            return ("", "", "");
        }
    }

    private System.Net.Http.HttpClient MakeNetworkClient()
    {
        var handler = new System.Net.Http.HttpClientHandler { Proxy = DownloadProxy.Create() };
        return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>延迟测量客户端：经当前代理（与出口 IP 检测同一链路），量的是端到端真实延迟。</summary>
    private static System.Net.Http.HttpClient MakeLatencyClient()
    {
        var handler = new System.Net.Http.HttpClientHandler { Proxy = DownloadProxy.Create() };
        return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    private async Task LoadAllAsync()
    {
        await RefreshConfigAsync();
        await RefreshProfileAsync();
        await RefreshGroupsAsync();
        RefreshToggles();
    }

    private void RefreshToggles()
    {
        try
        {
            var enabled = _host.SystemProxy.IsEnabled();
            _dispatcher.TryEnqueue(() => SystemProxyText = enabled ? "已开启" : "已关闭");
        }
        catch
        {
        }
    }

    private async Task RefreshConfigAsync()
    {
        if (_core.Api is null || _core.State != CoreState.Running) return;
        try
        {
            var config = await _core.Api.GetConfigsAsync();
            if (config is null) return;
            _dispatcher.TryEnqueue(() =>
            {
                Mode = config.Mode;
                MixedPort = config.MixedPort > 0 ? config.MixedPort.ToString() : "—";
            });
        }
        catch
        {
        }
    }

    private async Task RefreshProfileAsync()
    {
        try
        {
            var active = await _host.Profiles.GetActiveAsync();
            if (active is null)
            {
                ActiveProfileName = "未选择订阅";
                HasProfileUsage = false;
                ProfileUsageText = "";
                ProfileExpireText = "";
                return;
            }

            ActiveProfileName = active.Name;
            var info = SubscriptionUsage.Parse(active.SubscriptionUserInfo);
            if (info is null)
            {
                HasProfileUsage = false;
                ProfileUsageText = "";
                ProfileExpireText = "";
                return;
            }

            ProfileUsageText = info.Total > 0
                ? $"{Format.Bytes(info.Used)} / {Format.Bytes(info.Total)}"
                : $"↑ {Format.Bytes(info.Upload)}  ↓ {Format.Bytes(info.Download)}";
            ProfileUsagePercent = info.Total > 0 ? Math.Clamp(info.Used * 100.0 / info.Total, 0, 100) : 0;
            HasProfileUsage = info.Total > 0;
            ProfileExpireText = info.Expire <= 0
                ? "长期有效"
                : $"到期 {DateTimeOffset.FromUnixTimeSeconds(info.Expire).LocalDateTime:yyyy-MM-dd}";
        }
        catch
        {
        }
    }

    private async Task RefreshGroupsAsync()
    {
        if (_core.Api is null) return;
        try
        {
            var proxies = await _core.Api.GetProxiesAsync();

            var declared = MihomoConfigBuilder.ReadProxyGroupOrder(_core.Paths.RuntimeConfigPath);
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < declared.Count; i++) rank[declared[i]] = i;

            _dispatcher.TryEnqueue(() =>
            {
                Groups.Clear();
                var nodes = new HashSet<string>(StringComparer.Ordinal);

                var ordered = proxies.Values
                    .Where(p => p.All is { Count: > 0 } && !IsGlobalGroup(p.Name))
                    .OrderBy(p => rank.TryGetValue(p.Name, out var r) ? r : int.MaxValue);

                foreach (var p in ordered)
                {
                    var members = new List<ProxyItemViewModel>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in p.All!)
                    {
                        if (!proxies.TryGetValue(member, out var node)) continue;
                        if (!IsCountableNode(node)) continue;
                        if (!seen.Add(node.Name)) continue;
                        members.Add(new ProxyItemViewModel(node.Name, node.Type, node.Alive, node.Delay, p.Now == member, groupName: p.Name));
                        nodes.Add(node.Name);
                    }

                    var selected = members.FirstOrDefault(n => n.IsSelected)?.Name ?? p.Now ?? "—";
                    Groups.Add(new DashboardGroupViewModel(p.Name, p.Type, selected, members));
                }

                NodeCount = nodes.Count;
                GroupSummary = Groups.Count == 0
                    ? "—"
                    : $"{Groups.Count} 个代理组 · {NodeCount} 个节点";
            });
        }
        catch
        {
        }
    }

    private static bool IsGlobalGroup(string name) =>
        string.Equals(name, "GLOBAL", StringComparison.OrdinalIgnoreCase);

    private static bool IsCountableNode(ProxyItem p) =>
        !string.IsNullOrEmpty(p.Type) && p.Type.ToLowerInvariant() switch
        {
            "selector" or "urltest" or "fallback" or "loadbalance" or "relay"
                or "direct" or "reject" or "rejectdrop" or "pass" or "dns"
                or "compatible" or "global" => false,
            _ => true,
        };

    private void OnTraffic(TrafficSnapshot t)
    {
        UpRate = Format.Rate(t.Up);
        DownRate = Format.Rate(t.Down);
        UpTotal = Format.Bytes(t.UpTotal);
        DownTotal = Format.Bytes(t.DownTotal);

        Push(_upHistory, t.Up);
        Push(_downHistory, t.Down);

        UpPoints = _upHistory.ToArray();
        DownPoints = _downHistory.ToArray();
        MaxRate = Math.Max(1, Math.Max(_upHistory.DefaultIfEmpty(0).Max(), _downHistory.DefaultIfEmpty(0).Max()));
    }

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > RateWindow) queue.Dequeue();
    }

    [RelayCommand]
    private async Task ToggleCoreAsync()
    {
        if (IsRunning) await _core.StopAsync();
        else await _core.StartAsync();
    }

    [RelayCommand]
    private async Task RestartCoreAsync()
    {
        await _core.RestartAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadAllAsync();
        await RefreshNetworkAsync();
    }
}

public sealed class DashboardGroupViewModel
{
    public string Name { get; }
    public string TypeText { get; }
    public string SelectedNode { get; }
    public IReadOnlyList<ProxyItemViewModel> Nodes { get; }
    public string CountText { get; }

    public DashboardGroupViewModel(string name, string type, string selectedNode, IReadOnlyList<ProxyItemViewModel> nodes)
    {
        Name = name;
        SelectedNode = selectedNode;
        Nodes = nodes;
        CountText = $"{nodes.Count} 个节点";
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
}
