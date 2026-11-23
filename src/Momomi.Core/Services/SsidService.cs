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
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private string? _lastSsid;
    private bool _proxyPausedBySsid;
    private volatile bool _disposed;

    private static readonly Regex SsidPattern = new(@"SSID\s*:\s*(.+)", RegexOptions.Compiled);

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);
    private readonly object _debounceGate = new();
    private CancellationTokenSource? _debounceCts;

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
        if (_disposed) return;
        _disposed = true;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts = null;
        }
    }

    private void OnNetworkStatusChanged(object? sender)
    {
        CancellationTokenSource cts;
        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            cts = new CancellationTokenSource();
            _debounceCts = cts;
        }
        _ = DebouncedCheckAsync(cts);
    }

    private async Task DebouncedCheckAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DebounceDelay, cts.Token).ConfigureAwait(false);
            await CheckAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_debounceGate)
            {
                if (ReferenceEquals(_debounceCts, cts)) _debounceCts = null;
            }
            cts.Dispose();
        }
    }

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
                var match = SsidPattern.Match(trimmed);
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
        // 串行化:Start() 的立即检查与网络变化防抖可能并发,并发读写 _lastSsid/_proxyPausedBySsid
        // 会造成漏触发或重复开关代理。
        await _checkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            var ssid = await Task.Run(GetCurrentSsid).ConfigureAwait(false);
            if (string.Equals(ssid, _lastSsid, StringComparison.OrdinalIgnoreCase)) return;
            _lastSsid = ssid;
            await ApplyRulesAsync(ssid).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MomomiHost.CoreLog($"[SsidService] 应用 SSID 规则失败:{ex.Message}");
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>按当前代理模式恢复系统代理(手动 / PAC),供暂停网络恢复时复用。</summary>
    private async Task RestoreProxyAsync(int mixedPort)
    {
        var mode = await _host.Settings.GetAsync("ui.systemProxyMode").ConfigureAwait(false) ?? "manual";
        if (mode == "pac")
        {
            if (_host.Pac.Start(mixedPort) && _host.Pac.PacUrl is { } pacUrl)
            {
                _host.SystemProxy.EnablePac(pacUrl);
                return;
            }
            MomomiHost.CoreLog("[SsidService] PAC 启动失败,回退为手动代理。");
        }
        _host.SystemProxy.Enable($"127.0.0.1:{mixedPort}", SystemProxyState.Bypass);
    }

    private async Task ApplyRulesAsync(string? ssid)
    {
        // 暂停代理规则：命中的 SSID 关系统代理，离开后恢复。
        var pauseSsids = (await _host.Settings.GetAsync("ui.pauseSsids").ConfigureAwait(false) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inPauseList = ssid is not null && pauseSsids.Contains(ssid, StringComparer.OrdinalIgnoreCase);

        var mixedPort = await _host.Settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false);

        if (inPauseList)
        {
            // 只暂停「本应用」设置的代理,不碰用户/其它软件配置的系统代理。
            if (!_proxyPausedBySsid && _host.SystemProxy.IsOwnProxy(mixedPort))
            {
                _host.SystemProxy.ClearOwnProxy(mixedPort);
                _proxyPausedBySsid = true;
                // 注意：这里不改写 ui.systemProxyDesired —— 暂停是临时策略，
                // 用户的「希望开启」意图应保持为 true，离开该网络后要能恢复。
            }
        }
        else if (_proxyPausedBySsid)
        {
            // 离开暂停网络:按当前代理模式恢复,不能无条件写回手动代理,否则 PAC 模式会被静默降级。
            await RestoreProxyAsync(mixedPort).ConfigureAwait(false);
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
}
