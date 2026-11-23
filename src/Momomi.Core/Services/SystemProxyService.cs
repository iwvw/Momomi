using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Momomi.Core.Services;

public interface ISystemProxyService
{
    bool IsEnabled();
    string? CurrentServer();
    string? CurrentAutoConfigUrl();
    bool Enable(string server, string bypass);
    bool EnablePac(string pacUrl);
    bool Disable();

    /// <summary>
    /// 当前系统代理是否指向 Momomi 自己的监听端口（手动代理指向 127.0.0.1:port，或 PAC 指向本机回环）。
    /// 用于区分「自己设置的代理」和「用户/其它软件设置的代理」，避免误关别人的配置。
    /// </summary>
    bool IsOwnProxy(int corePort);

    /// <summary>
    /// 强制关闭「指向 Momomi 自己端口」的残留代理，无论端口是否还在监听。
    /// 供启动时清理崩溃遗留（此时内核已退出，残留代理等于断网）。
    /// </summary>
    bool ClearOwnProxy(int corePort);
}

public sealed class SystemProxyService : ISystemProxyService
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    // 首次接管系统代理前快照用户原有配置,关闭时还原,避免永久覆盖第三方代理/PAC。
    private const string SnapshotSavedKey = "proxy.original.saved";
    private const string SnapshotEnableKey = "proxy.original.enable";
    private const string SnapshotServerKey = "proxy.original.server";
    private const string SnapshotOverrideKey = "proxy.original.override";
    private const string SnapshotPacKey = "proxy.original.pac";
    private const string AbsentMarker = "\u0001";

    private readonly ISettingsService? _settings;

    public SystemProxyService(ISettingsService? settings = null)
    {
        _settings = settings;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            return key?.GetValue("ProxyEnable") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    public string? CurrentServer()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            return key?.GetValue("ProxyServer") as string;
        }
        catch
        {
            return null;
        }
    }

    public string? CurrentAutoConfigUrl()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            return key?.GetValue("AutoConfigURL") as string;
        }
        catch
        {
            return null;
        }
    }

    public bool IsOwnProxy(int corePort)
    {
        if (!IsEnabled()) return false;

        // 手动代理：ProxyServer 形如 "127.0.0.1:7890"（也可能带协议前缀或分协议配置）。
        var server = CurrentServer();
        if (!string.IsNullOrEmpty(server) && IsOwnServer(server, corePort)) return true;

        // PAC：AutoConfigURL 指向本机回环，同样属于本应用（PacServer 只监听 127.0.0.1）。
        var pac = CurrentAutoConfigUrl();
        if (!string.IsNullOrEmpty(pac) && Uri.TryCreate(pac, UriKind.Absolute, out var uri))
            return uri.IsLoopback;

        return false;
    }

    public bool ClearOwnProxy(int corePort)
    {
        try
        {
            if (!IsOwnProxy(corePort)) return false;
            return Disable();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断 ProxyServer 配置里的主机端口是否指向本机指定端口。</summary>
    private static bool IsOwnServer(string server, int corePort)
    {
        // 支持 "127.0.0.1:7890"、"http=127.0.0.1:7890;https=127.0.0.1:7890" 两种写法。
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = part;
            var eq = value.IndexOf('=');
            if (eq >= 0) value = value[(eq + 1)..];

            value = value.Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                value = value["http://".Length..];

            var colon = value.LastIndexOf(':');
            if (colon <= 0) continue;

            var host = value[..colon].Trim();
            if (!int.TryParse(value[(colon + 1)..].Trim(), out var port)) continue;

            var isLoopback = host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
            if (isLoopback && port == corePort) return true;
        }
        return false;
    }

    public bool Enable(string server, string bypass)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RegPath);
            if (key is null) return false;
            SaveOriginalIfNeeded();
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", server, RegistryValueKind.String);
            key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            Notify();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用 PAC 模式：写 AutoConfigURL（本地 PAC 脚本），清理手动代理设置。</summary>
    public bool EnablePac(string pacUrl)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RegPath);
            if (key is null) return false;
            SaveOriginalIfNeeded();
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            Notify();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            // 优先还原接管前的原始配置;没有快照时退回"仅关闭代理"。
            if (RestoreOriginal()) return true;

            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            if (key is null) return false;
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            Notify();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveOriginalIfNeeded()
    {
        if (_settings is null) return;
        try
        {
            if (_settings.GetBoolAsync(SnapshotSavedKey).GetAwaiter().GetResult()) return;
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            var enable = key?.GetValue("ProxyEnable") is int v && v == 1 ? "1" : "0";
            var server = key?.GetValue("ProxyServer") as string;
            var bypass = key?.GetValue("ProxyOverride") as string;
            var pac = key?.GetValue("AutoConfigURL") as string;
            _settings.SetAsync(SnapshotEnableKey, enable).GetAwaiter().GetResult();
            _settings.SetAsync(SnapshotServerKey, server ?? AbsentMarker).GetAwaiter().GetResult();
            _settings.SetAsync(SnapshotOverrideKey, bypass ?? AbsentMarker).GetAwaiter().GetResult();
            _settings.SetAsync(SnapshotPacKey, pac ?? AbsentMarker).GetAwaiter().GetResult();
            _settings.SetBoolAsync(SnapshotSavedKey, true).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    private bool RestoreOriginal()
    {
        if (_settings is null) return false;
        try
        {
            if (!_settings.GetBoolAsync(SnapshotSavedKey).GetAwaiter().GetResult()) return false;

            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RegPath);
            if (key is null) return false;

            var enable = _settings.GetAsync(SnapshotEnableKey).GetAwaiter().GetResult() == "1" ? 1 : 0;
            key.SetValue("ProxyEnable", enable, RegistryValueKind.DWord);
            ApplyOrDelete(key, "ProxyServer", _settings.GetAsync(SnapshotServerKey).GetAwaiter().GetResult());
            ApplyOrDelete(key, "ProxyOverride", _settings.GetAsync(SnapshotOverrideKey).GetAwaiter().GetResult());
            ApplyOrDelete(key, "AutoConfigURL", _settings.GetAsync(SnapshotPacKey).GetAwaiter().GetResult());
            Notify();

            _settings.SetBoolAsync(SnapshotSavedKey, false).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyOrDelete(RegistryKey key, string name, string? value)
    {
        if (string.IsNullOrEmpty(value) || string.Equals(value, AbsentMarker, StringComparison.Ordinal))
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
