using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Networking.Connectivity;

namespace Momomi.Core.Services;

/// <summary>
/// SSID 感知：监听当前 WiFi 网络变化，把 SSID 与订阅/暂停规则匹配，
/// 自动切换订阅或暂停系统代理。用于"回家直连 / 公司自动走代理"等场景。
/// </summary>
public sealed class SsidService : IDisposable
{
    private readonly MomomiHost _host;
    private string? _lastSsid;
    private bool _proxyPausedBySsid;

    public SsidService(MomomiHost host)
    {
        _host = host;
    }

    /// <summary>启动监听：立即检查一次，之后在 WiFi 变化时检查。</summary>
    public void Start()
    {
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        _ = CheckAsync();
    }

    public void Dispose()
    {
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
    }

    private void OnNetworkStatusChanged(object? sender)
        => _ = CheckAsync();

    /// <summary>读取当前 SSID（无 WiFi 或获取失败返回 null）。</summary>
    public static string? GetCurrentSsid()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)) continue;
                var match = Regex.Match(trimmed, @"SSID\s*:\s*(.+)");
                if (!match.Success) continue;
                var ssid = match.Groups[1].Value.Trim();
                return string.IsNullOrEmpty(ssid) ? null : ssid;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task CheckAsync()
    {
        try
        {
            var ssid = await Task.Run(GetCurrentSsid).ConfigureAwait(false);
            if (string.Equals(ssid, _lastSsid, StringComparison.OrdinalIgnoreCase)) return;
            _lastSsid = ssid;
            await ApplyRulesAsync(ssid).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task ApplyRulesAsync(string? ssid)
    {
        // 暂停代理规则：命中的 SSID 关系统代理，离开后恢复。
        var pauseSsids = (await _host.Settings.GetAsync("ui.pauseSsids").ConfigureAwait(false) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inPauseList = ssid is not null && pauseSsids.Contains(ssid, StringComparer.OrdinalIgnoreCase);

        if (inPauseList)
        {
            if (!_proxyPausedBySsid && _host.SystemProxy.IsEnabled())
            {
                _host.SystemProxy.Disable();
                _proxyPausedBySsid = true;
            }
        }
        else if (_proxyPausedBySsid)
        {
            // 离开暂停网络：恢复之前由本服务关闭的系统代理。
            _host.SystemProxy.Enable("127.0.0.1:" + EffectiveProxyPort(), DefaultBypass);
            _proxyPausedBySsid = false;
        }

        // 订阅切换规则：命中的 SSID 自动切换为指定订阅。
        if (ssid is not null)
        {
            var mapLines = (await _host.Settings.GetAsync("ui.ssidProfileMap").ConfigureAwait(false) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var entry = mapLines
                .Select(l => l.Split('\t', 2))
                .FirstOrDefault(parts => parts.Length == 2
                    && string.Equals(parts[0].Trim(), ssid, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                var profileName = entry[1].Trim();
                var items = await _host.Profiles.ListAsync().ConfigureAwait(false);
                var target = items.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                if (target is not null && !target.IsActive)
                {
                    await _host.Profiles.SetActiveAsync(target.Id).ConfigureAwait(false);
                    await _host.ApplyActiveProfileAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static int EffectiveProxyPort()
    {
        try
        {
            return DownloadProxy.EffectivePort;
        }
        catch
        {
            return 7890;
        }
    }

    private const string DefaultBypass = "localhost;127.*;10.*;172.16.*;192.168.*;<local>";
}
