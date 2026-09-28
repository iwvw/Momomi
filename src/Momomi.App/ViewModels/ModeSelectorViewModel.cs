using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class ModeSelectorViewModel : ObservableObject
{
    private static readonly string[] Modes = ["rule", "global", "direct"];

    private const string ProxyBypass = "localhost;127.*;10.*;172.16.*;192.168.*";

    private readonly MomomiHost _host;
    private readonly DispatcherQueue _dispatcher;
    private bool _loading;
    private bool _suppressSwitch;
    private bool _switchBusy;
    private bool? _pendingTun;
    private bool _proxyBusy;
    private bool? _pendingProxy;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string ModeText { get; set; } = "规则";

    [ObservableProperty]
    public partial bool SystemProxyOn { get; set; }

    [ObservableProperty]
    public partial bool TunOn { get; set; }

    public string Mode { get; private set; } = "rule";

    public bool IsGlobal => Mode == "global";

    public event EventHandler<string>? ModeChanged;

    public ModeSelectorViewModel(MomomiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        _host.Core.StateChanged += (_, e) => _dispatcher.TryEnqueue(() => OnCoreStateChanged(e.State));
        AppSignals.SwitchesChanged += (_, _) => _dispatcher.TryEnqueue(() => _ = LoadAsync());
        OnCoreStateChanged(_host.Core.State);
    }

    private void OnCoreStateChanged(CoreState state)
    {
        IsRunning = state == CoreState.Running;
        _ = SyncSwitchesAsync();
        if (IsRunning) _ = ReloadAsync();
    }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var mode = await _host.Settings.GetAsync("core.mode") ?? "rule";

            if (_host.Core.Api is not null && _host.Core.State == CoreState.Running)
            {
                try
                {
                    var config = await _host.Core.Api.GetConfigsAsync();
                    if (config is not null) mode = config.Mode;
                }
                catch
                {
                }
            }

            var proxyOn = _host.SystemProxy.IsEnabled();
            var tunOn = await _host.Settings.GetBoolAsync("core.tun").ConfigureAwait(false);

            _dispatcher.TryEnqueue(() =>
            {
                ApplyMode(mode);
                _suppressSwitch = true;
                try
                {
                    SystemProxyOn = proxyOn;
                    TunOn = tunOn;
                }
                finally
                {
                    _suppressSwitch = false;
                }
            });
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ReloadAsync()
    {
        await Task.Delay(500);
        await LoadAsync();
    }

    private void ApplyMode(string mode)
    {
        var index = Array.IndexOf(Modes, mode);
        if (index < 0) index = 0;
        SelectedIndex = index;
        ModeText = index switch
        {
            1 => "全局",
            2 => "直连",
            _ => "规则",
        };

        var normalized = Modes[index];
        if (normalized != Mode)
        {
            Mode = normalized;
            OnPropertyChanged(nameof(IsGlobal));
            ModeState.Set(normalized);
            ModeChanged?.Invoke(this, normalized);
        }
    }

    public async Task SelectAsync(int index)
    {
        if (_loading || index < 0 || index >= Modes.Length) return;

        var mode = Modes[index];
        await _host.Settings.SetAsync("core.mode", mode);

        if (_host.Core.Api is not null && _host.Core.State == CoreState.Running)
        {
            try
            {
                await _host.Core.Api.PatchConfigsAsync(new { mode });
            }
            catch
            {
            }
        }

        ApplyMode(mode);
        AppSignals.RaiseSwitchesChanged();
    }

    private async Task SyncSwitchesAsync()
    {
        var proxyOn = _host.SystemProxy.IsEnabled();
        var tunOn = await _host.Settings.GetBoolAsync("core.tun").ConfigureAwait(false);
        _dispatcher.TryEnqueue(() =>
        {
            _suppressSwitch = true;
            try
            {
                SystemProxyOn = proxyOn;
                TunOn = tunOn;
            }
            finally
            {
                _suppressSwitch = false;
            }
        });
    }

    public async Task RefreshSwitchesAsync() => await SyncSwitchesAsync().ConfigureAwait(false);

    public async Task SetSystemProxyAsync(bool enabled)
    {
        if (_suppressSwitch) return;

        if (_proxyBusy)
        {
            _pendingProxy = enabled;
            return;
        }
        _proxyBusy = true;
        _pendingProxy = null;
        try
        {
            if (enabled)
            {
                var port = await _host.Settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false);
                _host.SystemProxy.Enable($"127.0.0.1:{port}", ProxyBypass);
            }
            else
            {
                _host.SystemProxy.Disable();
            }
        }
        catch
        {
        }
        finally
        {
            _proxyBusy = false;
            await SyncSwitchesAsync().ConfigureAwait(false);
            AppSignals.RaiseSwitchesChanged();

            if (_pendingProxy is not null)
            {
                var next = _pendingProxy.Value;
                _pendingProxy = null;
                await SetSystemProxyAsync(next).ConfigureAwait(false);
            }
        }
    }

    public async Task SetTunAsync(bool enabled)
    {
        if (_suppressSwitch) return;

        // 快速连续切换收敛：切换中时记录最新请求，完成后按最后一次继续，避免连点丢状态。
        if (_switchBusy)
        {
            _pendingTun = enabled;
            return;
        }
        _switchBusy = true;
        _pendingTun = null;
        try
        {
            await Task.Run(async () =>
            {
                await _host.Settings.SetBoolAsync("core.tun", enabled).ConfigureAwait(false);

                if (enabled)
                {
                    try
                    {
                        await _host.KernelUpdate.EnsureWintunAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                // 先把设置落盘到 runtime.yaml（TUN 段依据 core.tun 重新生成），供下次全量重载/重启使用。
                await _host.RewriteRuntimeConfigAsync().ConfigureAwait(false);

                if (_host.Core.Api is not null && _host.Core.State == CoreState.Running)
                {
                    // 热切换：只 PATCH tun.enable，内核即时应用，不重启进程、不动系统代理。
                    try
                    {
                        await _host.Core.Api.PatchConfigsAsync(new Dictionary<string, object>
                        {
                            ["tun"] = new Dictionary<string, object>
                            {
                                ["enable"] = enabled,
                            },
                        }).ConfigureAwait(false);
                    }
                    catch
                    {
                        // PATCH 失败（旧内核不支持热切换）时兜底：整配置重载让 tun 段生效；
                        // 仍失败才重启内核。
                        var reloaded = await _host.ApplyActiveProfileAsync().ConfigureAwait(false);
                        if (!reloaded)
                            await _host.Core.RestartAsync().ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _switchBusy = false;
            await SyncSwitchesAsync().ConfigureAwait(false);
            AppSignals.RaiseSwitchesChanged();

            // 切换期间若有新的请求进来，紧接着执行它，保证最终状态等于用户最后一次操作。
            if (_pendingTun is not null)
            {
                var next = _pendingTun.Value;
                _pendingTun = null;
                await SetTunAsync(next).ConfigureAwait(false);
            }
        }
    }
}
