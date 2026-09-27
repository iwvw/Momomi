using Momomi.Core.Models;

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
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Momomi", "core.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Paths.WorkingDirectory);
        if (!File.Exists(Paths.RuntimeConfigPath))
        {
            var options = new RuntimeYamlOptions(
                MixedPort: 7897,
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
                started = await StartElevatedAsync(ct).ConfigureAwait(false);
                IsElevatedMode = started;
                if (!started)
                    Log($"[CoreManager] 提权启动失败，回退普通模式（TUN 不会生效）：{_lastError}");
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
                SetState(CoreState.Error, null, "内核启动后无法连接 API");
                await StopAsync(ct).ConfigureAwait(false);
                return false;
            }

            _version = version;
            // TUN 请求了但没走提权时，明确告警，避免用户误以为 TUN 已生效。
            var portWarning = _tunRequested && !IsElevatedMode
                ? $"TUN 模式启动失败，已回退普通代理：{_lastError}"
                : await CheckPortConflictAsync(ct).ConfigureAwait(false);
            SetState(CoreState.Running, version, portWarning);
            RestoreOwnSystemProxy();
            StartStreams();
            // 内核已就绪，后续下载（内核/geodata/订阅）经本机混合端口，借力自身代理。
            DownloadProxy.SetCorePort(await _settings.GetIntAsync("core.mixedPort", 7897).ConfigureAwait(false));
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
                _lastError = "提权宿主启动失败（可能拒绝了 UAC 授权）";
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

            // 内核一停，指向本机混合端口的系统代理就会让主机断网，因此同步关闭。
            // 记录原状态，供随后的 StartAsync（重启场景）恢复。
            _restoreSystemProxyAfterStart = TryDisableOwnSystemProxy();

            lock (_streamGate) StopStreamsLocked();

            _api?.Dispose();
            _api = null;

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

            _version = null;
            SetState(CoreState.Stopped, null, null);
            // 内核已停，下载改回直连（代理端口不再可用）。
            DownloadProxy.SetCorePort(0);
        }
        finally
        {
            _gate.Release();
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
            var port = _settings.GetIntAsync("core.mixedPort", 7897).GetAwaiter().GetResult();
            var expected = $"127.0.0.1:{port}";
            if (_systemProxy.IsEnabled()
                && string.Equals(_systemProxy.CurrentServer(), expected, StringComparison.OrdinalIgnoreCase))
            {
                _systemProxy.Disable();
                return true;
            }
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
            var port = _settings.GetIntAsync("core.mixedPort", 7897).GetAwaiter().GetResult();
            _systemProxy.Enable($"127.0.0.1:{port}", "localhost;127.*;10.*;172.16.*;192.168.*");
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
        catch
        {
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
        SetState(CoreState.Error, _version, $"内核进程已退出（代码 {e.ExitCode}）{Environment.NewLine}{e.LastError}");
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

