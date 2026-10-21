using Momomi.Core.Models;
using Momomi.Data;

namespace Momomi.Core.Services;

public enum CoreState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

public sealed record CoreStateChanged(CoreState State, string? Version, string? Error);

public sealed record CorePaths(
    string BinaryPath,
    string WorkingDirectory,
    string RuntimeConfigPath,
    string ControllerAddress,
    int ControllerPort,
    string Secret,
    string ElevatedHostPath);

public interface ICoreManager : IDisposable
{
    CoreState State { get; }
    string? Version { get; }
    string? LastError { get; }
    CorePaths Paths { get; }
    IMihomoApiClient? Api { get; }
    bool IsElevatedMode { get; }

    event EventHandler<CoreStateChanged>? StateChanged;
    event EventHandler<TrafficSnapshot>? TrafficUpdated;
    event EventHandler<MemorySnapshot>? MemoryUpdated;
    event EventHandler<ConnectionsSnapshot>? ConnectionsUpdated;
    event EventHandler<LogEntry>? LogReceived;

    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default);

    /// <summary>应用新配置：热重载失败时自动重启内核兜底。返回配置是否已真正生效。</summary>
    Task<bool> ApplyConfigAsync(string configPath, CancellationToken ct = default);
    Task<bool> RestartAsync(CancellationToken ct = default);
    Task<bool> ValidateAsync(string configPath, CancellationToken ct = default);
}

public sealed class CoreManager : ICoreManager
{
    private readonly IMihomoProcessManager _process;
    private readonly ISettingsService _settings;
    private readonly IElevatedClient _elevated;
    private readonly ISystemProxyService _systemProxy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly object _streamGate = new();
    private readonly Timer _watchdog;

    private IMihomoApiClient? _api;
    private CancellationTokenSource? _streamCts;
    private CoreState _state = CoreState.Stopped;
    private string? _version;
    private string? _lastError;
    private bool _tunRequested;
    private bool _restoreSystemProxyAfterStart;

    public CorePaths Paths { get; }
    public CoreState State => _state;
    public string? Version => _version;
    public string? LastError => _lastError;
    public IMihomoApiClient? Api => _api;
    public bool IsElevatedMode { get; private set; }

    public event EventHandler<CoreStateChanged>? StateChanged;
    public event EventHandler<TrafficSnapshot>? TrafficUpdated;
    public event EventHandler<MemorySnapshot>? MemoryUpdated;
    public event EventHandler<ConnectionsSnapshot>? ConnectionsUpdated;
    public event EventHandler<LogEntry>? LogReceived;

    public CoreManager(IMihomoProcessManager process, ISettingsService settings, IElevatedClient elevated, ISystemProxyService systemProxy, CorePaths paths)
    {
        _process = process;
        _settings = settings;
        _elevated = elevated;
        _systemProxy = systemProxy;
        Paths = paths;
        _process.Exited += OnProcessExited;
        _watchdog = new Timer(OnWatchdog, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private static void Log(string message)
    {
        var files = new List<string>();

        // 便携/安装版数据目录固定，但排查时常去 LocalApplicationData 找日志；
        // 两处都写一份，免得「core.log 里没有今天的记录」被误当成没问题。
        try
        {
            files.Add(Path.Combine(MomomiAppData.ResolveDirectory(), "core.log"));
        }
        catch
        {
        }
        files.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Momomi", "core.log"));

        foreach (var path in files)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Paths.WorkingDirectory);
        if (!File.Exists(Paths.RuntimeConfigPath))
        {
            var options = new RuntimeYamlOptions(
                MixedPort: 7890,
                ControllerPort: Paths.ControllerPort,
                Secret: Paths.Secret,
                Mode: "rule",
                LogLevel: "info",
                AllowLan: false,
                Ipv6: true,
                TunEnabled: false);
            await File.WriteAllTextAsync(Paths.RuntimeConfigPath, MihomoConfigBuilder.BuildFallbackYaml(options), ct)
                .ConfigureAwait(false);
        }
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state is CoreState.Running or CoreState.Starting) return _state == CoreState.Running;

            SetState(CoreState.Starting, null, null);

            if (!File.Exists(Paths.BinaryPath))
            {
                SetState(CoreState.Error, null, $"未找到内核：{Paths.BinaryPath}");
                return false;
            }

            // 清理上次异常退出遗留的本应用内核进程。
            await _process.KillOrphansAsync(Paths.BinaryPath, ct).ConfigureAwait(false);

            _tunRequested = await _settings.GetBoolAsync("core.tun").ConfigureAwait(false);

            var options = new CoreLaunchOptions(
                Paths.BinaryPath,
                Paths.WorkingDirectory,
                Paths.RuntimeConfigPath,
                Paths.Secret);

            var started = false;
            if (_tunRequested)
            {
                if (MomomiHost.IsRunningAsAdmin())
                {
                    // 主程序已是管理员：内核作为子进程直接启动，继承管理员令牌，TUN 生效。
                    started = await _process.StartAsync(options, ct).ConfigureAwait(false);
                    IsElevatedMode = false;
                }
                else
                {
                    // 未以管理员运行：尝试经由提权宿主启动（兼容旧路径）。
                    started = await StartElevatedAsync(ct).ConfigureAwait(false);
                    IsElevatedMode = started;
                    if (!started)
                        Log($"[CoreManager] 提权启动失败，回退普通模式（TUN 不会生效）：{_lastError}");
                }
            }

            if (!started)
            {
                started = await _process.StartAsync(options, ct).ConfigureAwait(false);
                IsElevatedMode = false;
            }

            if (!started)
            {
                SetState(CoreState.Error, null, "内核进程启动失败");
                return false;
            }

            _api = new MihomoApiClient(Paths.ControllerAddress, Paths.Secret);

            var version = await WaitForReadyAsync(ct).ConfigureAwait(false);
            if (version is null)
            {
                // 注意：这里已经在持有 _gate，不能再调 StopAsync（SemaphoreSlim 不可重入，
                // 会永久等待自己持有的锁，导致 _state 卡在 Starting、之后再也起不来、
                // 退出时 ShutdownNetwork 也卡在同一把锁上关不掉程序）。
                // 因此走不取锁的清理，并且只发一次状态（Stopped 带原因），
                // 避免先 Error 再 Stopped 让自动退出倒计时短暂启动。
                await CleanupLockedAsync(ct).ConfigureAwait(false);
                SetState(CoreState.Stopped, null, "内核启动后无法连接 API");
                return false;
            }

            _version = version;
            // 仅在“请求了 TUN 但没有管理员权限”时告警；主程序本身是管理员时 TUN 已生效，不告警。
            var tunIneffective = _tunRequested && !IsElevatedMode && !MomomiHost.IsRunningAsAdmin();
            var portWarning = tunIneffective
                ? $"TUN 模式启动失败，已回退为普通代理（TUN 未生效）。原因：{_lastError}"
                : await CheckPortConflictAsync(ct).ConfigureAwait(false);
            SetState(CoreState.Running, version, portWarning);
            RestoreOwnSystemProxy();
            StartStreams();
            // 内核已就绪，后续下载（内核/geodata/订阅）经本机混合端口，借力自身代理。
            DownloadProxy.SetCorePort(await _settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false));
            return true;
        }
        catch (Exception ex)
        {
            SetState(CoreState.Error, null, ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartElevatedAsync(CancellationToken ct)
    {
        if (!File.Exists(Paths.ElevatedHostPath))
        {
            _lastError = $"未找到提权宿主：{Paths.ElevatedHostPath}";
            return false;
        }

        if (!_elevated.IsElevatedHostRunning)
        {
            if (!_elevated.LaunchElevatedHost())
            {
                _lastError = _elevated.LaunchError
                    ?? "提权宿主启动失败（可能拒绝了 UAC 授权）";
                return false;
            }
            await Task.Delay(1200, ct).ConfigureAwait(false);
        }

        var response = await _elevated
            .StartCoreAsync(Paths.BinaryPath, Paths.WorkingDirectory, Paths.RuntimeConfigPath, Paths.Secret, ct)
            .ConfigureAwait(false);

        if (!response.Success)
        {
            _lastError = response.Message;
            return false;
        }

        return true;
    }

    private async Task<string?> CheckPortConflictAsync(CancellationToken ct)
    {
        if (_api is null) return null;
        try
        {
            var config = await _api.GetConfigsAsync(ct).ConfigureAwait(false);
            if (config is null) return null;
            if (config.MixedPort == 0)
                return "混合端口未能绑定，可能已被其他程序占用。请在设置中改用其他端口。";
        }
        catch
        {
        }
        return null;
    }

    private async Task<string?> WaitForReadyAsync(CancellationToken ct)
    {
        if (_api is null) return null;
        // 首次启动时内核需要下载 MMDB/GeoSite 等地理数据，可能耗时较久，因此给足等待时间。
        for (var i = 0; i < 240; i++)
        {
            if (ct.IsCancellationRequested) return null;
            var version = await _api.GetVersionAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(version)) return version;
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    private void StartStreams()
    {
        lock (_streamGate)
        {
            StopStreamsLocked();
            _streamCts = new CancellationTokenSource();
            var ct = _streamCts.Token;
            var api = _api;
            if (api is null) return;

            _ = RunStreamAsync(() => api.StartTrafficStreamAsync(s => { TrafficUpdated?.Invoke(this, s); return Task.CompletedTask; }, ct), ct);
            _ = RunStreamAsync(() => api.StartMemoryStreamAsync(s => { MemoryUpdated?.Invoke(this, s); return Task.CompletedTask; }, ct), ct);
            _ = RunStreamAsync(() => PollConnectionsAsync(api, ct), ct);
            _ = RunStreamAsync(() => api.StartLogStreamAsync(s => { LogReceived?.Invoke(this, s); return Task.CompletedTask; }, ct), ct);
        }
    }

    private async Task PollConnectionsAsync(IMihomoApiClient api, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var snapshot = await api.GetConnectionsAsync(ct).ConfigureAwait(false);
            if (snapshot is not null) ConnectionsUpdated?.Invoke(this, snapshot);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task RunStreamAsync(Func<Task> stream, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await stream().ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }

            if (ct.IsCancellationRequested) return;
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
        }
    }

    private void StopStreamsLocked()
    {
        if (_streamCts is null) return;
        try
        {
            _streamCts.Cancel();
            _streamCts.Dispose();
        }
        catch
        {
        }
        _streamCts = null;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == CoreState.Stopped) return;
            SetState(CoreState.Stopping, _version, null);
            await CleanupLockedAsync(ct).ConfigureAwait(false);
            SetState(CoreState.Stopped, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 停内核并清理相关资源。<b>调用方必须已持有 <see cref="_gate"/></b>，
    /// 这样 StartAsync 的失败分支可以复用它而不重入锁。
    /// </summary>
    private async Task CleanupLockedAsync(CancellationToken ct)
    {
        // 内核一停，指向本机混合端口的系统代理就会让主机断网，因此同步关闭。
        // 记录原状态，供随后的 StartAsync（重启场景）恢复。
        _restoreSystemProxyAfterStart = TryDisableOwnSystemProxy();

        lock (_streamGate) StopStreamsLocked();

        _api?.Dispose();
        _api = null;

        try
        {
            if (IsElevatedMode)
            {
                try
                {
                    await _elevated.StopCoreAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                }
                IsElevatedMode = false;
            }
            else
            {
                await _process.StopAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _version = null;
            // 内核已停，下载改回直连（代理端口不再可用）。
            DownloadProxy.SetCorePort(0);
        }
    }

    /// <summary>
    /// 若系统代理指向本机混合端口则关闭，返回是否确实关闭过（用于重启后恢复）。
    /// 只动"自己设置的"代理，避免误关用户手动配置的代理。
    /// </summary>
    private bool TryDisableOwnSystemProxy()
    {
        try
        {
            var port = _settings.GetIntAsync("core.mixedPort", 7890).GetAwaiter().GetResult();
            // 交给 SystemProxyService 判断：同时兼容 "127.0.0.1:port"、
            // 分协议写法（http=...;https=...）与 PAC 回环地址。
            return _systemProxy.ClearOwnProxy(port);
        }
        catch
        {
        }
        return false;
    }

    private void RestoreOwnSystemProxy()
    {
        if (!_restoreSystemProxyAfterStart) return;
        _restoreSystemProxyAfterStart = false;
        try
        {
            var port = _settings.GetIntAsync("core.mixedPort", 7890).GetAwaiter().GetResult();
            // 内核重启（同一进程内）的恢复：无条件写回手动代理。
            // PAC 模式由 PacServer 常驻监听，AutoConfigURL 在 StopAsync 时被清掉，
            // 这里统一回到手动代理即可；App 层随后会按真实开关状态同步界面。
            _systemProxy.Enable($"127.0.0.1:{port}", SystemProxyState.Bypass);

            // 这一步是"重启内核后把代理开回来"，属于用户意图为开启的情况，
            // 同步意图，避免下次启动时按旧值判断。
            _ = SystemProxyState.SetDesiredAsync(_settings, true);
        }
        catch
        {
        }
    }

    public async Task<bool> ValidateAsync(string configPath, CancellationToken ct = default)
    {
        var options = new CoreLaunchOptions(
            Paths.BinaryPath,
            Paths.WorkingDirectory,
            configPath,
            Paths.Secret);
        return await _process.ValidateConfigAsync(options, ct).ConfigureAwait(false);
    }

    public async Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default)
    {
        if (_api is null || _state != CoreState.Running) return false;
        try
        {
            await _api.ReloadConfigAsync(configPath, force: true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // 重载被拒（如配置存在悬空引用：代理组引用了已删除的节点）时内核会保留旧配置，
            // 表现为「订阅已更新但旧节点仍在」。记录原因便于排查。
            Log($"[CoreManager] 配置重载失败，内核仍使用旧配置：{ex.Message}");
            return false;
        }
    }

    private static readonly TimeSpan RestartForReloadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 让内核用新配置生效，优先原地热重载。
    ///
    /// 热重载被拒时内核会**保留旧配置继续运行**——这正是「订阅更新了但节点列表没变」的来源。
    /// 旧逻辑只写一行日志就返回，用户侧毫无感知。这里在重载失败后自动重启内核兜底：
    /// 新配置在启动时还会再做一次严格校验，起不来会进入 Error 状态并给出原因，
    /// 至少不会继续拿着旧配置假装"已生效"。
    /// </summary>
    public async Task<bool> ApplyConfigAsync(string configPath, CancellationToken ct = default)
    {
        if (_state != CoreState.Running) return true;
        if (await ReloadConfigAsync(configPath, ct).ConfigureAwait(false)) return true;

        Log("[CoreManager] 热重载失败，改为重启内核以应用新配置。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RestartForReloadTimeout);

            await _restartGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await StopAsync(timeout.Token).ConfigureAwait(false);
                if (await StartAsync(timeout.Token).ConfigureAwait(false))
                {
                    Log("[CoreManager] 内核已重启，新配置生效。");
                    return true;
                }

                Log($"[CoreManager] 重启后内核未能运行：{_lastError ?? "未知原因"}");
                return false;
            }
            finally
            {
                _restartGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Log("[CoreManager] 重启内核超时，仍在使用旧配置。");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[CoreManager] 重启内核失败：{ex.Message}");
            return false;
        }
    }

    public async Task<bool> RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);
        return await StartAsync(ct).ConfigureAwait(false);
    }

    private void OnProcessExited(object? sender, CoreProcessExitedEventArgs e)
    {
        lock (_streamGate) StopStreamsLocked();
        if (_state is CoreState.Stopping or CoreState.Stopped) return;
        SetState(CoreState.Error, _version,
            string.IsNullOrWhiteSpace(e.LastError)
                ? $"内核进程已退出（代码 {e.ExitCode}）"
                : $"内核进程已退出（代码 {e.ExitCode}）：{e.LastError}");
    }

    private void OnWatchdog(object? state)
    {
        if (!IsElevatedMode || _state != CoreState.Running) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var status = await _elevated.GetStatusAsync().ConfigureAwait(false);
                if (status.Success) return;
                SetState(CoreState.Error, _version, $"提权内核已退出：{status.Message}");
                lock (_streamGate) StopStreamsLocked();
                IsElevatedMode = false;
            }
            catch
            {
            }
        });
    }

    private void SetState(CoreState state, string? version, string? error)
    {
        _state = state;
        if (version is not null) _version = version;
        _lastError = error;
        StateChanged?.Invoke(this, new CoreStateChanged(state, _version, error));
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _process.Exited -= OnProcessExited;
        lock (_streamGate) StopStreamsLocked();
        _api?.Dispose();
        _process.Dispose();

        // 无论是否处于提权模式，只要提权宿主还活着就让它退出，否则它会锁住可执行文件。
        if (_elevated.IsElevatedHostRunning)
        {
            try
            {
                _elevated.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
    }
}

