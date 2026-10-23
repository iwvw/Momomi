using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

/// <summary>
/// 更新检查的调度中枢，职责与 BetterScene 的 UpdateCoordinator 一致：
/// - 决定「什么时候检查」（启动时 / 距上次超过设定间隔）
/// - 决定「检查到新版本后做什么」（只提示 / 自动下载安装）
/// - 记住上次检查时间与用户跳过的版本
///
/// 真正的网络与安装动作在 <see cref="IAppUpdateService"/> 里，这里只做编排。
/// 之所以单独抽出来：检查时机涉及「跳过版本」「检查间隔」「通知开关」三份设置，
/// 散在 App.xaml.cs 的轮询循环里会让那几条规则互相纠缠、也没法被设置页复用。
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private readonly IAppUpdateService _service;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();

    private Timer? _timer;
    private bool _busy;
    private bool _disposed;

    public UpdateCoordinator(IAppUpdateService service, ISettingsService settings)
    {
        _service = service;
        _settings = settings;
    }

    /// <summary>检查到新版本时触发（UI 线程）。第二个参数表示是否弹托盘通知。</summary>
    public event Action<AppUpdateInfo, bool>? UpdateAvailable;

    /// <summary>检查完成但没有更新时触发（UI 线程，仅手动检查）。</summary>
    public event Action? UpToDate;

    /// <summary>自动安装即将开始（UI 线程）。</summary>
    public event Action<AppUpdateInfo>? AutoInstallStarting;

    /// <summary>自动安装失败，已退化为「提示有更新」（UI 线程）。</summary>
    public event Action<AppUpdateInfo>? AutoInstallFailed;

    /// <summary>检查失败（UI 线程，仅手动检查），参数为简短原因。</summary>
    public event Action<string>? CheckFailed;

    /// <summary>程序启动后调用一次：按设置决定是否立即检查，并排好后续周期。</summary>
    public void Start()
    {
        if (_disposed) return;

        ScheduleNextTick();

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await AutoCheckEnabledAsync().ConfigureAwait(false)) return;
                if (!await _settings.GetBoolAsync("ui.updateCheckOnStartup", true).ConfigureAwait(false)) return;

                // 启动检查延后：让主窗口先完成首屏，避免和内核启动抢带宽。
                await Task.Delay(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                if (_disposed) return;

                await CheckAsync(manual: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                global::Momomi.App.App.WriteLog($"启动检查更新失败：{ex}");
            }
        });
    }

    /// <summary>设置里改了更新相关选项后调用，重新排期。</summary>
    public void Refresh()
    {
        if (_disposed) return;
        ScheduleNextTick();
    }

    /// <summary>
    /// 执行一次检查。
    /// <paramref name="manual"/> = true 表示用户主动点了「检查更新」：
    /// 此时忽略间隔限制、忽略「跳过此版本」、忽略「不显示通知」。
    /// </summary>
    public async Task CheckAsync(bool manual)
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (_busy) return;
            _busy = true;
        }

        try
        {
            if (!manual && !await AutoCheckEnabledAsync().ConfigureAwait(false)) return;

            var info = await _service.CheckAsync().ConfigureAwait(false);

            if (info.Error is not null)
            {
                global::Momomi.App.App.WriteLog($"检查应用更新失败：{info.Error}");
                if (manual) Post(() => CheckFailed?.Invoke(info.Error));
                return;
            }

            // MSIX 形态不支持应用内覆盖式自更新，只提示不安装。
            var updatable = info.HasUpdate
                            && !string.IsNullOrEmpty(info.LatestVersion)
                            && !global::Momomi.App.App.IsMsixPackaged();

            if (!updatable)
            {
                if (manual) Post(() => UpToDate?.Invoke());
                return;
            }

            // 用户明确跳过这个版本，就静默（手动检查不受此限）。
            if (!manual)
            {
                var skipped = await _service.GetSkippedVersionAsync().ConfigureAwait(false);
                if (string.Equals(skipped, info.LatestVersion, StringComparison.Ordinal))
                {
                    global::Momomi.App.App.WriteLog($"更新 v{info.LatestVersion} 已被用户跳过");
                    return;
                }
            }

            if (await _settings.GetBoolAsync("ui.updateAutoInstall").ConfigureAwait(false))
            {
                Post(() => AutoInstallStarting?.Invoke(info));

                var ok = await global::Momomi.App.App.TryAutoInstallUpdateAsync().ConfigureAwait(false);
                if (!ok)
                {
                    // 自动安装失败时退化成提示，至少让用户知道有新版本。
                    Post(() => AutoInstallFailed?.Invoke(info));
                    Post(() => UpdateAvailable?.Invoke(info, true));
                }
                return;
            }

            // 「不显示通知」只压制托盘气泡，顶栏胶囊仍然出现——
            // 否则用户会以为根本没检查过。
            var notify = manual
                         || !await _settings.GetBoolAsync("ui.updateSuppressNotifications").ConfigureAwait(false);

            Post(() => UpdateAvailable?.Invoke(info, notify));
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"检查应用更新异常：{ex}");
            if (manual) Post(() => CheckFailed?.Invoke(ex.Message));
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    /// <summary>用户选择「跳过此版本」。</summary>
    public Task SkipVersionAsync(string version) => _service.SetSkippedVersionAsync(version);

    /// <summary>清掉「跳过此版本」的记录（设置页「恢复提示」用）。</summary>
    public Task ClearSkipAsync() => _service.SetSkippedVersionAsync(null);

    private Task<bool> AutoCheckEnabledAsync() => _settings.GetBoolAsync("ui.autoCheckAppUpdate", true);

    /// <summary>
    /// 把回调切到 UI 线程。
    ///
    /// 窗口可能还没建好（启动早期、「仅迷你面板」模式）：此时不能直接丢弃事件，
    /// 否则更新状态会永远停在「没检查过」。改为把结果写进 AppSignals 的静态状态，
    /// 主窗口创建时会在 HookUpdatePill 里补读一次；事件本身在无队列时跳过即可。
    /// </summary>
    private static void Post(Action action)
    {
        var queue = global::Momomi.App.App.Main?.DispatcherQueue;
        if (queue is null) return;
        queue.TryEnqueue(() => action());
    }

    /// <summary>按设定间隔安排下一次检查。0 表示不排期（只在启动时检查）。</summary>
    private void ScheduleNextTick()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;

            if (_disposed) return;

            // 每分钟醒一次再判断是否到期，而不是直接等 N 小时。
            // 理由：一次性长计时器在系统休眠/唤醒后会漂，用「时间戳 + 轮询」更稳，
            // 而且用户改了间隔能立刻生效（Refresh 会重建这个计时器）。
            _timer = new Timer(_ => OnTick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    private void OnTick()
    {
        if (_disposed) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await AutoCheckEnabledAsync().ConfigureAwait(false)) return;

                var hours = await GetIntervalHoursAsync().ConfigureAwait(false);
                if (hours <= 0) return;

                if (!await IsDueAsync(hours).ConfigureAwait(false)) return;

                await CheckAsync(manual: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                global::Momomi.App.App.WriteLog($"更新定时检查失败：{ex}");
            }
        });
    }

    private async Task<int> GetIntervalHoursAsync()
    {
        var hours = await _settings.GetIntAsync("ui.checkAppUpdateIntervalHours", 6).ConfigureAwait(false);
        return hours is < 1 or > 168 ? 6 : hours;
    }

    /// <summary>距上次检查是否已超过设定间隔。</summary>
    private async Task<bool> IsDueAsync(int hours)
    {
        var last = await _service.GetLastCheckedAsync().ConfigureAwait(false);
        if (last is null) return true;

        // 时钟被回拨（或时间戳来自未来）时直接当作到期，避免永远不再检查。
        var elapsed = DateTimeOffset.UtcNow - last.Value;
        if (elapsed < TimeSpan.Zero) return true;

        return elapsed >= TimeSpan.FromHours(hours);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
