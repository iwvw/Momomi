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
        try
        {
            if (enabled)
            {
                var port = await _host.Settings.GetIntAsync("core.mixedPort", 7897).ConfigureAwait(false);
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

        await SyncSwitchesAsync().ConfigureAwait(false);
        AppSignals.RaiseSwitchesChanged();
    }

    public async Task SetTunAsync(bool enabled)
    {
        if (_suppressSwitch || _switchBusy) return;
        _switchBusy = true;
        try
        {
            // TUN 切换会重启内核，期间可能同步等待 UAC 提权与内核就绪；
            // 必须整段放到后台线程，否则会占死 UI 线程导致界面卡死。
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

                // 必须先按新设置重新生成 runtime.yaml，否则 tun 段不会出现，重启也无济于事。
                await _host.ApplyActiveProfileAsync().ConfigureAwait(false);

                if (_host.Core.State is CoreState.Running or CoreState.Error)
                    await _host.Core.RestartAsync().ConfigureAwait(false);
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
        }
    }
}
