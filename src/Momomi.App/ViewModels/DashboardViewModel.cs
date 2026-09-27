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
    public partial IReadOnlyList<double> UpPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial IReadOnlyList<double> DownPoints { get; set; } = Array.Empty<double>();

    [ObservableProperty]
    public partial double MaxRate { get; set; } = 1;

    public ObservableCollection<DashboardGroupViewModel> Groups { get; } = new();

    private readonly EventHandler<CoreStateChanged> _onState;
    private readonly EventHandler<TrafficSnapshot> _onTraffic;
    private readonly EventHandler<MemorySnapshot> _onMemory;
    private readonly EventHandler<ConnectionsSnapshot> _onConnections;
    private readonly EventHandler _onProxiesReloaded;

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

        _onState = (_, e) => _dispatcher.TryEnqueue(() => OnStateChanged(e));
        _onTraffic = (_, t) => _dispatcher.TryEnqueue(() => OnTraffic(t));
        _onMemory = (_, m) => _dispatcher.TryEnqueue(() => Memory = Format.Bytes(m.InUse));
        _onConnections = (_, c) => _dispatcher.TryEnqueue(() => ConnectionCount = c.Connections.Count);
        _onProxiesReloaded = (_, _) => _dispatcher.TryEnqueue(() => _ = LoadAllAsync());
    }

    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;

        _core.StateChanged += _onState;
        _core.TrafficUpdated += _onTraffic;
        _core.MemoryUpdated += _onMemory;
        _core.ConnectionsUpdated += _onConnections;
        AppSignals.ProxiesChanged += _onProxiesReloaded;

        OnStateChanged(new CoreStateChanged(_core.State, _core.Version, _core.LastError));
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;

        _core.StateChanged -= _onState;
        _core.TrafficUpdated -= _onTraffic;
        _core.MemoryUpdated -= _onMemory;
        _core.ConnectionsUpdated -= _onConnections;
        AppSignals.ProxiesChanged -= _onProxiesReloaded;
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
        if (IsRunning) _ = LoadAllAsync();
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
    private async Task RefreshAsync() => await LoadAllAsync();
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
