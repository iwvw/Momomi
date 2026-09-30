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

        _core.TrafficUpdated += _onTraffic;
        _core.StateChanged += _onState;
        AppSignals.ProxiesChanged += _onProxiesChanged;

        ApplyCoreState(_core.State);
    }

    /// <summary>退订所有事件，供迷你窗口关闭时调用，避免静态事件累积订阅造成泄漏。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.TrafficUpdated -= _onTraffic;
        _core.StateChanged -= _onState;
        AppSignals.ProxiesChanged -= _onProxiesChanged;
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
        var (url, timeout, concurrency) = await DelayTestSettings.ReadAsync(_host.Settings).ConfigureAwait(false);
        _delayTestUrl = url;
        _delayTestTimeoutMs = timeout;
        _delayTestConcurrency = concurrency;
        _autoCloseConnection = await _host.Settings.GetBoolAsync("core.autoCloseConnection", true).ConfigureAwait(false);
        await LoadAsync().ConfigureAwait(false);
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

        HasGroups = Groups.Count > 0;
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

    public async Task TestGroupAsync(MiniProxyGroupViewModel group)
    {
        if (_core.Api is null || group.IsTesting) return;
        _dispatcher.TryEnqueue(() => group.IsTesting = true);
        // 统一走 DelayTester：并发受控 + 每节点多次取最小，与主面板测速逻辑一致。
        var names = group.Nodes.Select(n => n.Name).ToList();
        var delays = new Dictionary<string, int>(StringComparer.Ordinal);
        await DelayTester.MeasureAllAsync(
            _core.Api, names, _delayTestUrl, _delayTestTimeoutMs, _delayTestConcurrency,
            (name, delay) =>
            {
                lock (delays) delays[name] = delay;
                return Task.CompletedTask;
            }).ConfigureAwait(false);

        _dispatcher.TryEnqueue(() =>
        {
            group.ApplyDelays(delays);
            group.IsTesting = false;
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
            });
        }
    }
}
