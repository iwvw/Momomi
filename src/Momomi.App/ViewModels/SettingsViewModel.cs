using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MomomiHost _host;
    private bool _loading;

    [ObservableProperty]
    public partial bool AutoStartCore { get; set; }

    [ObservableProperty]
    public partial bool StartupWithWindows { get; set; }

    [ObservableProperty]
    public partial bool SystemProxyEnabled { get; set; }

    [ObservableProperty]
    public partial bool TunEnabled { get; set; }

    [ObservableProperty]
    public partial string Theme { get; set; } = "default";

    [ObservableProperty]
    public partial int NavigationStyle { get; set; }

    /// <summary>背景材质：0=Mica，1=亚克力(Mica Alt)，2=纯色。</summary>
    [ObservableProperty]
    public partial int BackdropStyle { get; set; }

    [ObservableProperty]
    public partial string MixedPort { get; set; } = "7897";

    [ObservableProperty]
    public partial string ControllerPort { get; set; } = "9090";

    [ObservableProperty]
    public partial string Mode { get; set; } = "rule";

    [ObservableProperty]
    public partial string LogLevel { get; set; } = "info";

    [ObservableProperty]
    public partial bool AllowLan { get; set; }

    [ObservableProperty]
    public partial bool Ipv6 { get; set; } = true;

    [ObservableProperty]
    public partial string CorePath { get; set; } = "";

    [ObservableProperty]
    public partial string DataDirectory { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string KernelVersion { get; set; } = "未知";

    [ObservableProperty]
    public partial string LatestKernelVersion { get; set; } = "—";

    [ObservableProperty]
    public partial bool KernelHasUpdate { get; set; }

    [ObservableProperty]
    public partial bool IsKernelBusy { get; set; }

    [ObservableProperty]
    public partial string KernelStatusText { get; set; } = "尚未检查";

    [ObservableProperty]
    public partial double KernelProgress { get; set; }

    [ObservableProperty]
    public partial bool KernelProgressVisible { get; set; }

    partial void OnKernelHasUpdateChanged(bool value) => OnPropertyChanged(nameof(UpdateButtonText));

    [ObservableProperty]
    public partial string ElevatedHostStatus { get; set; } = "未运行";

    [ObservableProperty]
    public partial bool SilentStart { get; set; }

    [ObservableProperty]
    public partial bool AutoQuitWithoutCore { get; set; }

    [ObservableProperty]
    public partial int AutoQuitWithoutCoreDelay { get; set; } = 30;

    [ObservableProperty]
    public partial bool AutoUpdateProfileOnStart { get; set; } = true;

    [ObservableProperty]
    public partial string DelayTestUrl { get; set; } = DelayTestSettings.DefaultUrl;

    [ObservableProperty]
    public partial string DelayTestTimeout { get; set; } = "3000";

    [ObservableProperty]
    public partial string DelayTestConcurrency { get; set; } = "16";

    [ObservableProperty]
    public partial string SubscriptionUserAgent { get; set; } = "";

    [ObservableProperty]
    public partial string SubscriptionTimeout { get; set; } = "60";

    [ObservableProperty]
    public partial int GithubProxyIndex { get; set; }

    [ObservableProperty]
    public partial string GithubProxyCustom { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoCloseConnection { get; set; } = true;

    [ObservableProperty]
    public partial string TunStack { get; set; } = "mixed";

    [ObservableProperty]
    public partial bool TunAutoRoute { get; set; } = true;

    [ObservableProperty]
    public partial bool TunAutoRedirect { get; set; }

    [ObservableProperty]
    public partial bool TunAutoDetectInterface { get; set; } = true;

    [ObservableProperty]
    public partial bool TunStrictRoute { get; set; }

    [ObservableProperty]
    public partial string TunMtu { get; set; } = "";

    [ObservableProperty]
    public partial string TunDnsHijack { get; set; } = "";

    [ObservableProperty]
    public partial string TunRouteExcludeAddress { get; set; } = "";

    /// <summary>GitHub 代理内置选项，与 ComboBox 顺序一致。</summary>
    public static readonly string[] GithubProxyBuiltins =
    {
        "auto", "direct",
        "https://gh-proxy.org", "https://ghfast.top",
        "https://down.clashparty.org", "https://download.mihomo.party",
    };

    public string UpdateButtonText => KernelHasUpdate ? "更新内核" : "重新下载";

    public SettingsViewModel(MomomiHost host)
    {
        _host = host;
        CorePath = host.Core.Paths.BinaryPath;
        DataDirectory = global::Momomi.Data.MomomiAppData.ResolveDirectory();
    }

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            AutoStartCore = await _host.Settings.GetBoolAsync("core.autoStart", true);
            Theme = await _host.Settings.GetAsync("theme") ?? "default";
            NavigationStyle = await _host.Settings.GetIntAsync("ui.navigationStyle", 0);
            BackdropStyle = await _host.Settings.GetIntAsync("ui.backdropStyle", 0);
            MixedPort = (await _host.Settings.GetIntAsync("core.mixedPort", 7897)).ToString();
            ControllerPort = (await _host.Settings.GetIntAsync("core.controllerPort", 9090)).ToString();
            Mode = await _host.Settings.GetAsync("core.mode") ?? "rule";
            LogLevel = await _host.Settings.GetAsync("core.logLevel") ?? "info";
            AllowLan = await _host.Settings.GetBoolAsync("core.allowLan");
            Ipv6 = await _host.Settings.GetBoolAsync("core.ipv6", true);
            TunEnabled = await _host.Settings.GetBoolAsync("core.tun");
            StartupWithWindows = _host.Startup.IsEnabled();
            SystemProxyEnabled = _host.SystemProxy.IsEnabled();
            KernelVersion = _host.KernelUpdate.GetInstalledVersion() ?? "未安装";
            ElevatedHostStatus = _host.Elevated.IsElevatedHostRunning ? "运行中" : "未运行";

            SilentStart = await _host.Settings.GetBoolAsync("ui.silentStart");
            AutoQuitWithoutCore = await _host.Settings.GetBoolAsync("ui.autoQuitWithoutCore");
            AutoQuitWithoutCoreDelay = await _host.Settings.GetIntAsync("ui.autoQuitWithoutCoreDelay", 30);
            AutoUpdateProfileOnStart = await _host.Settings.GetBoolAsync("profile.autoUpdate", true);

            var (dtUrl, dtTimeout, dtConc) = await DelayTestSettings.ReadAsync(_host.Settings);
            DelayTestUrl = dtUrl;
            DelayTestTimeout = dtTimeout.ToString();
            DelayTestConcurrency = dtConc.ToString();

            SubscriptionUserAgent = await _host.Settings.GetAsync("core.subscriptionUserAgent") ?? "";
            SubscriptionTimeout = (await _host.Settings.GetIntAsync("core.subscriptionTimeout", 60)).ToString();

            var githubProxy = await _host.Settings.GetAsync("core.githubProxy") ?? "auto";
            var builtinIndex = Array.IndexOf(GithubProxyBuiltins, githubProxy);
            GithubProxyIndex = builtinIndex >= 0 ? builtinIndex : GithubProxyBuiltins.Length;
            GithubProxyCustom = builtinIndex >= 0 ? "" : githubProxy;

            AutoCloseConnection = await _host.Settings.GetBoolAsync("core.autoCloseConnection", true);

            TunStack = await _host.Settings.GetAsync("core.tunStack") ?? "mixed";
            TunAutoRoute = await _host.Settings.GetBoolAsync("core.tunAutoRoute", true);
            TunAutoRedirect = await _host.Settings.GetBoolAsync("core.tunAutoRedirect");
            TunAutoDetectInterface = await _host.Settings.GetBoolAsync("core.tunAutoDetectInterface", true);
            TunStrictRoute = await _host.Settings.GetBoolAsync("core.tunStrictRoute");
            TunMtu = await _host.Settings.GetAsync("core.tunMtuText") ?? "";
            TunDnsHijack = await _host.Settings.GetAsync("core.tunDnsHijack") ?? "";
            TunRouteExcludeAddress = await _host.Settings.GetAsync("core.tunRouteExcludeAddress") ?? "";
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnThemeChanged(string value)
    {
        if (_loading) return;
        global::Momomi.App.App.ApplyTheme(value);
        _ = PersistAsync("theme", value, "主题已切换");
    }

    partial void OnNavigationStyleChanged(int value)
    {
        if (_loading) return;
        _ = _host.Settings.SetIntAsync("ui.navigationStyle", value);
        global::Momomi.App.App.Main?.ApplyNavigationStyle(value);
        StatusText = value == 1 ? "导航已切换为顶部" : "导航已切换为左侧";
    }

    partial void OnBackdropStyleChanged(int value)
    {
        if (_loading) return;
        _ = _host.Settings.SetIntAsync("ui.backdropStyle", value);
        global::Momomi.App.App.Main?.ApplyBackdropStyle(value);
        StatusText = value switch
        {
            1 => "背景已切换为亚克力",
            2 => "背景已切换为纯色",
            _ => "背景已切换为 Mica",
        };
    }

    partial void OnAutoStartCoreChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("core.autoStart", value);
    }

    partial void OnStartupWithWindowsChanged(bool value)
    {
        if (_loading) return;
        _host.Startup.SetEnabled(value);
        StatusText = value ? "已设为开机自启" : "已取消开机自启";
    }

    partial void OnSystemProxyEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = ApplySystemProxyAsync(value);
    }

    partial void OnTunEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = ApplyTunAsync(value);
    }

    partial void OnAllowLanChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("core.allowLan", value);
        StatusText = "已保存，重启内核后生效";
    }

    partial void OnIpv6Changed(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("core.ipv6", value);
        StatusText = "已保存，重启内核后生效";
    }

    partial void OnLogLevelChanged(string value)
    {
        if (_loading) return;
        _ = ApplyLogLevelAsync(value);
    }

    partial void OnSilentStartChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("ui.silentStart", value);
    }

    partial void OnAutoQuitWithoutCoreChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("ui.autoQuitWithoutCore", value);
    }

    partial void OnAutoQuitWithoutCoreDelayChanged(int value)
    {
        if (_loading) return;
        if (value < 0) return;
        _ = _host.Settings.SetIntAsync("ui.autoQuitWithoutCoreDelay", value);
    }

    partial void OnAutoUpdateProfileOnStartChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("profile.autoUpdate", value);
    }

    partial void OnDelayTestUrlChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.delayTestUrl", value ?? "");
    }

    partial void OnDelayTestTimeoutChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var ms) && ms > 0)
            _ = _host.Settings.SetIntAsync("core.delayTestTimeout", ms);
    }

    partial void OnDelayTestConcurrencyChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var n) && n > 0)
            _ = _host.Settings.SetIntAsync("core.delayTestConcurrency", n);
    }

    partial void OnSubscriptionUserAgentChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.subscriptionUserAgent", value ?? "");
    }

    partial void OnSubscriptionTimeoutChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var s) && s > 0)
            _ = _host.Settings.SetIntAsync("core.subscriptionTimeout", s);
    }

    partial void OnGithubProxyIndexChanged(int value)
    {
        if (_loading) return;
        PersistGithubProxy();
    }

    partial void OnGithubProxyCustomChanged(string value)
    {
        if (_loading) return;
        if (GithubProxyIndex != GithubProxyBuiltins.Length) return;
        PersistGithubProxy();
    }

    private void PersistGithubProxy()
    {
        var key = GithubProxyIndex >= 0 && GithubProxyIndex < GithubProxyBuiltins.Length
            ? GithubProxyBuiltins[GithubProxyIndex]
            : (string.IsNullOrWhiteSpace(GithubProxyCustom) ? "auto" : GithubProxyCustom.Trim());
        _ = _host.Settings.SetAsync("core.githubProxy", key);
    }

    partial void OnAutoCloseConnectionChanged(bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync("core.autoCloseConnection", value);
    }

    partial void OnTunStackChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.tunStack", value);
        StatusText = "已保存，重启内核后生效";
    }

    partial void OnTunAutoRouteChanged(bool value) => PersistTunBool("core.tunAutoRoute", value);
    partial void OnTunAutoRedirectChanged(bool value) => PersistTunBool("core.tunAutoRedirect", value);
    partial void OnTunAutoDetectInterfaceChanged(bool value) => PersistTunBool("core.tunAutoDetectInterface", value);
    partial void OnTunStrictRouteChanged(bool value) => PersistTunBool("core.tunStrictRoute", value);

    private void PersistTunBool(string key, bool value)
    {
        if (_loading) return;
        _ = _host.Settings.SetBoolAsync(key, value);
        StatusText = "已保存，重启内核后生效";
    }

    partial void OnTunMtuChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.tunMtuText", value ?? "");
        var mtu = int.TryParse(value, out var m) && m > 0 ? m : 0;
        _ = _host.Settings.SetIntAsync("core.tunMtu", mtu);
        StatusText = "已保存，重启内核后生效";
    }

    partial void OnTunDnsHijackChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.tunDnsHijack", value ?? "");
        StatusText = "已保存，重启内核后生效";
    }

    partial void OnTunRouteExcludeAddressChanged(string value)
    {
        if (_loading) return;
        _ = _host.Settings.SetAsync("core.tunRouteExcludeAddress", value ?? "");
        StatusText = "已保存，重启内核后生效";
    }

    partial void OnMixedPortChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var mp) && mp is > 0 and < 65536)
        {
            _ = _host.Settings.SetIntAsync("core.mixedPort", mp);
            StatusText = "端口已保存，重启内核后生效";
        }
    }

    partial void OnControllerPortChanged(string value)
    {
        if (_loading) return;
        if (int.TryParse(value, out var cp) && cp is > 0 and < 65536)
        {
            _ = _host.Settings.SetIntAsync("core.controllerPort", cp);
            StatusText = "端口已保存，重启内核后生效";
        }
    }

    private async Task PersistAsync(string key, string value, string message)
    {
        try
        {
            await _host.Settings.SetAsync(key, value);
            StatusText = message;
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    private async Task ApplySystemProxyAsync(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var port = await _host.Settings.GetIntAsync("core.mixedPort", 7897);
                _host.SystemProxy.Enable($"127.0.0.1:{port}", "localhost;127.*;10.*;172.16.*;192.168.*");
                StatusText = $"系统代理已开启（127.0.0.1:{port}）";
            }
            else
            {
                _host.SystemProxy.Disable();
                StatusText = "系统代理已关闭";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"系统代理设置失败：{ex.Message}";
        }

        AppSignals.RaiseSwitchesChanged();
    }

    private async Task ApplyTunAsync(bool enabled)
    {
        try
        {
            await _host.Settings.SetBoolAsync("core.tun", enabled);
            if (enabled)
            {
                try
                {
                    await _host.KernelUpdate.EnsureWintunAsync();
                }
                catch
                {
                }
            }

            // 必须先重新生成 runtime.yaml，否则 tun 段不会写入，重启也不会生效。
            await _host.ApplyActiveProfileAsync();

            if (_host.Core.State is CoreState.Running or CoreState.Error)
            {
                StatusText = enabled ? "正在以 TUN 模式重启内核…" : "正在重启内核…";
                await _host.Core.RestartAsync();
                StatusText = _host.Core.State == CoreState.Running ? "TUN 设置已生效" : "内核重启失败";
            }
            else
            {
                StatusText = "已保存，重启内核后生效";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"TUN 设置失败：{ex.Message}";
        }

        AppSignals.RaiseSwitchesChanged();
    }

    private async Task ApplyLogLevelAsync(string value)
    {
        await _host.Settings.SetAsync("core.logLevel", value);
        if (_host.Core.Api is not null && _host.Core.State == CoreState.Running)
        {
            try
            {
                await _host.Core.Api.PatchConfigsAsync(new Dictionary<string, object>
                {
                    ["log-level"] = value,
                });
                StatusText = "日志级别已生效";
                return;
            }
            catch
            {
            }
        }
        StatusText = "已保存，重启内核后生效";
    }

    [RelayCommand]
    private async Task FlushCachesAsync()
    {
        if (_host.Core.Api is null)
        {
            StatusText = "内核未运行";
            return;
        }
        try
        {
            await _host.Core.Api.FlushDnsAsync();
            await _host.Core.Api.FlushFakeIpAsync();
            StatusText = "DNS 与 FakeIP 缓存已清除";
        }
        catch (Exception ex)
        {
            StatusText = $"清除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestartCoreAsync()
    {
        StatusText = "正在重启内核…";
        await _host.Core.RestartAsync();
        StatusText = _host.Core.State == CoreState.Running ? "内核已重启" : "内核重启失败";
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DataDirectory,
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task CheckKernelAsync()
    {
        IsKernelBusy = true;
        KernelStatusText = "正在检查更新…";
        try
        {
            var info = await _host.KernelUpdate.CheckAsync();
            KernelVersion = info.CurrentVersion ?? "未安装";
            LatestKernelVersion = info.LatestVersion ?? "—";
            KernelHasUpdate = info.HasUpdate;
            KernelStatusText = info.Error is not null
                ? $"检查失败：{info.Error}"
                : info.HasUpdate ? "发现新版本" : "已是最新";
        }
        catch (Exception ex)
        {
            KernelStatusText = $"检查失败：{ex.Message}";
        }
        finally
        {
            IsKernelBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateKernelAsync()
    {
        IsKernelBusy = true;
        KernelProgressVisible = true;
        KernelProgress = 0;
        KernelStatusText = "正在下载内核…";

        var wasRunning = _host.Core.State == CoreState.Running;
        if (wasRunning) await _host.Core.StopAsync();

        try
        {
            var target = LatestKernelVersion;
            if (string.IsNullOrEmpty(target) || target == "—")
            {
                var info = await _host.KernelUpdate.CheckAsync();
                target = info.LatestVersion;
            }
            if (string.IsNullOrEmpty(target))
            {
                KernelStatusText = "无法确定目标版本";
                return;
            }

            var progress = new Progress<double>(p =>
            {
                KernelProgress = p * 100;
                KernelStatusText = $"正在下载内核… {p * 100:0}%";
            });

            var ok = await _host.KernelUpdate.DownloadAndInstallAsync(target, progress);
            if (ok)
            {
                KernelVersion = _host.KernelUpdate.GetInstalledVersion() ?? target;
                KernelHasUpdate = false;
                KernelStatusText = $"内核已更新到 {target}，正在准备地理数据…";

                var geodataProgress = new Progress<double>(p =>
                    KernelStatusText = $"正在下载地理数据… {p * 100:0}%");
                await _host.KernelUpdate.EnsureGeodataAsync(geodataProgress);

                if (await _host.Settings.GetBoolAsync("core.tun"))
                    await _host.KernelUpdate.EnsureWintunAsync();

                KernelStatusText = $"内核已更新到 {target}";
            }
            else
            {
                KernelStatusText = "内核更新失败";
            }
        }
        catch (Exception ex)
        {
            KernelStatusText = $"更新失败：{ex.Message}";
        }
        finally
        {
            KernelProgressVisible = false;
            IsKernelBusy = false;
            if (wasRunning) await _host.Core.StartAsync();
        }
    }

    [RelayCommand]
    private async Task LaunchElevatedHostAsync()
    {
        try
        {
            var ok = _host.Elevated.LaunchElevatedHost();
            await Task.Delay(1200);
            ElevatedHostStatus = _host.Elevated.IsElevatedHostRunning ? "运行中" : "未运行";
            StatusText = ok ? "已请求管理员权限启动提权宿主" : "提权宿主启动失败";
        }
        catch (Exception ex)
        {
            StatusText = $"启动提权宿主失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ShutdownElevatedHostAsync()
    {
        try
        {
            await _host.Elevated.ShutdownAsync();
            ElevatedHostStatus = "未运行";
            StatusText = "提权宿主已退出";
        }
        catch (Exception ex)
        {
            StatusText = $"退出提权宿主失败：{ex.Message}";
        }
    }
}
