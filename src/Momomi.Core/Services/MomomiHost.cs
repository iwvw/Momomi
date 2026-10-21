using Momomi.Data;

namespace Momomi.Core.Services;

public sealed class MomomiHost : IDisposable
{
    public MomomiDatabase Database { get; }
    public ISettingsService Settings { get; }
    public IMihomoProcessManager Process { get; }
    public ICoreManager Core { get; }
    public ISystemProxyService SystemProxy { get; }
    public IStartupService Startup { get; }
    public TrafficRepository Traffic { get; }
    public IKernelUpdateService KernelUpdate { get; }
    public IProfileService Profiles { get; }
    public IOverrideService Overrides { get; }
    public IElevatedClient Elevated { get; }
    public IAppUpdateService AppUpdate { get; }
    public SsidService Ssid { get; }
    public ILoopbackExemptionService Loopback { get; }
    public PacServer Pac { get; } = new();

    private readonly TrafficRecorder _recorder;
    private readonly Timer _profileRefreshTimer;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly string _root;

    public static string ResolveRootDirectory()
    {
        var dir = MomomiAppData.ResolveDirectory();
        Directory.CreateDirectory(dir);
        return dir;
    }

    public MomomiHost(string? rootDirectory = null)
    {
        _root = rootDirectory ?? ResolveRootDirectory();
        Directory.CreateDirectory(_root);

        Database = new MomomiDatabase(Path.Combine(_root, "momomi.db"));
        Settings = new SettingsService(Database);
        Process = new MihomoProcessManager();
        SystemProxy = new SystemProxyService();
        Startup = new StartupService();
        Traffic = new TrafficRepository(Database);

        var coreDir = Path.Combine(_root, "core");
        Directory.CreateDirectory(coreDir);

        KernelUpdate = new KernelUpdateService(coreDir, Settings);
        Elevated = new ElevatedClient(Path.Combine(AppContext.BaseDirectory, "Momomi.Elevated.exe"));
        Profiles = new ProfileService(Database, Settings, Path.Combine(_root, "profiles"));
        Overrides = new OverrideService(Database, Path.Combine(_root, "overrides"));
        AppUpdate = new AppUpdateService(Settings);
        Ssid = new SsidService(this);
        Loopback = new LoopbackExemptionService();

        var paths = new CorePaths(
            BinaryPath: KernelUpdate.BinaryPath,
            WorkingDirectory: coreDir,
            RuntimeConfigPath: Path.Combine(coreDir, "runtime.yaml"),
            ControllerAddress: "http://127.0.0.1:9090",
            ControllerPort: 9090,
            Secret: "momomi",
            ElevatedHostPath: Path.Combine(AppContext.BaseDirectory, "Momomi.Elevated.exe"));

        Core = new CoreManager(Process, Settings, Elevated, SystemProxy, paths);
        _recorder = new TrafficRecorder(Traffic);
        Core.TrafficUpdated += (_, snap) => _recorder.Record(snap);
        Core.ConnectionsUpdated += (_, snap) => _recorder.RecordConnections(snap);

        _profileRefreshTimer = new Timer(OnProfileRefresh, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await Database.InitializeAsync().ConfigureAwait(false);
        // 内置版：首次启动从安装目录的 bundled 释放内核/wintun/geodata（已存在则跳过）。
        try
        {
            var bundle = Path.Combine(AppContext.BaseDirectory, "bundled");
            if (Directory.Exists(bundle)) KernelUpdate.ProvisionFromBundle(bundle);
        }
        catch
        {
        }
        await Core.InitializeAsync(ct).ConfigureAwait(false);
        // 载入手动下载代理端口（用于内核未运行时下载内核/geodata）。
        DownloadProxy.SetManualPort(await Settings.GetIntAsync("core.downloadProxyPort", 0).ConfigureAwait(false));
        // SSID 感知：按 WiFi 自动切换订阅 / 暂停代理。
        Ssid.Start();
    }

    private void OnProfileRefresh(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var enabled = await Settings.GetBoolAsync("profile.autoUpdate", true).ConfigureAwait(false);
                if (!enabled) return;

                var items = await Profiles.ListAsync().ConfigureAwait(false);
                var activeId = items.FirstOrDefault(i => i.IsActive)?.Id;
                var activeRefreshed = false;

                foreach (var item in items.Where(i => i.Kind == "url"))
                {
                    if (await Profiles.RefreshAsync(item.Id).ConfigureAwait(false) && item.Id == activeId)
                        activeRefreshed = true;
                }

                // 定时更新只写了订阅文件；必须再重新生成 runtime.yaml 并交给内核，
                // 否则内核仍持有旧配置，订阅里已删除的节点会继续在前端显示。
                // 热重载被拒时 ApplyActiveProfileAsync 会自动重启内核兜底。
                if (activeRefreshed && !await ApplyActiveProfileAsync().ConfigureAwait(false))
                    CoreLog("[MomomiHost] 定时更新后新配置未能在内核生效，前端仍是旧节点。");
            }
            catch (Exception ex)
            {
                CoreLog($"[MomomiHost] 定时更新订阅失败：{ex.Message}");
            }
        });
    }

    /// <summary>把订阅/配置应用过程中的关键失败写进数据目录下的 core.log，便于排查「更新了却没变化」。</summary>
    private static void CoreLog(string message)
    {
        try
        {
            var path = Path.Combine(MomomiAppData.ResolveDirectory(), "core.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 生成最终交给内核的 runtime.yaml：订阅原始配置 + 应用全局项 + 用户覆写。
    /// 覆写按"全局启用 → 当前订阅挂载"的顺序叠加，订阅刷新后依然保留。
    /// </summary>
    public async Task<string> GenerateRuntimeYamlAsync(RuntimeYamlOptions options, CancellationToken ct = default)
    {
        var active = await Profiles.GetActiveAsync(ct).ConfigureAwait(false);
        var baseYaml = active is null
            ? MihomoConfigBuilder.BuildFallbackYaml(options)
            : await Profiles.GenerateRuntimeConfigAsync(active.Id, options, ct).ConfigureAwait(false);

        var documents = await Overrides.ResolveActiveDocumentsAsync(active?.Id, ct).ConfigureAwait(false);

        // 无论有没有覆写，输出都必须走 "Apply" 这条规范化路径。
        //
        // 这里曾经用 Normalize 处理"没有覆写"的分支，而 Normalize 特意保留 YAML 的
        // 文档结束标记 `...`（那是给差异视图做基线用的）。但这份文本是要写进
        // runtime.yaml 交给内核的：mihomo 会把 `...` 当成"配置到此为止"，
        // 结果是内核既不报错也没有任何代理（0.8.1 之前正是靠这条触发过一长串
        // 「配置重载失败 400」）。Apply 即使传入空集合也会剥掉结束标记，
        // 所以统一走它。
        return YamlOverrideMerger.Apply(baseYaml, documents);
    }

    /// <summary>
    /// 仅基于设置重写 runtime.yaml（不触发内核 reload）。用于 TUN 等设置热切换时
    /// 先把文件落盘，之后由 PATCH 让内核即时生效，避免全量重载。
    ///
    /// 必须与 <see cref="ApplyActiveProfileAsync"/> 共用同一把门：两者都会整份重写
    /// runtime.yaml，TUN 开关与订阅刷新/SSID 切换并发时，两份 WriteAllTextAsync
    /// 交错写入会让内核读到半截文件（表现为配置加载失败、节点列表回退）。
    /// </summary>
    public async Task RewriteRuntimeConfigAsync(CancellationToken ct = default)
    {
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = await Profiles.GetActiveAsync(ct).ConfigureAwait(false);
            if (active is null) return;
            var options = await BuildRuntimeOptionsAsync(ct).ConfigureAwait(false);
            var yaml = await GenerateRuntimeYamlAsync(options, ct).ConfigureAwait(false);
            await WriteRuntimeConfigAtomicAsync(yaml, ct).ConfigureAwait(false);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// 原子写 runtime.yaml：先写同目录临时文件再替换。
    /// 直接 WriteAllText 会在写的过程中让内核看到一个被截断的配置。
    /// </summary>
    private async Task WriteRuntimeConfigAtomicAsync(string yaml, CancellationToken ct)
    {
        var path = Core.Paths.RuntimeConfigPath;
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, yaml, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<bool> ApplyActiveProfileAsync(CancellationToken ct = default)
    {
        // 串行化：定时刷新、迷你面板、SSID 切换、设置页可能同时触发应用。
        // 两个调用同时写入同一个 runtime.yaml 会导致内核读到半截文件。
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = await Profiles.GetActiveAsync(ct).ConfigureAwait(false);
            if (active is null) return false;

            var options = await BuildRuntimeOptionsAsync(ct).ConfigureAwait(false);
            var yaml = await GenerateRuntimeYamlAsync(options, ct).ConfigureAwait(false);
            await WriteRuntimeConfigAtomicAsync(yaml, ct).ConfigureAwait(false);

            // 热重载失败时内核会保留旧配置继续跑；ApplyConfigAsync 会自动重启兜底，
            // 否则节点列表会一直停在上一次的配置上（“更新了但没变化”）。
            var applied = true;
            if (Core.State == CoreState.Running)
                applied = await Core.ApplyConfigAsync(Core.Paths.RuntimeConfigPath, ct).ConfigureAwait(false);

            // 配置应用后代理组/节点已变化，通知各视图重新拉取。
            ProxiesReloaded?.Invoke(this, EventArgs.Empty);
            return applied;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>订阅刷新或切换配置导致 runtime 配置重载后触发，供各视图强制重新拉取代理数据。</summary>
    public event EventHandler? ProxiesReloaded;

    public async Task<RuntimeYamlOptions> BuildRuntimeOptionsAsync(CancellationToken ct = default)
    {
        var dnsHijack = await GetStringListAsync("core.tunDnsHijack").ConfigureAwait(false);
        var exclude = await GetStringListAsync("core.tunRouteExcludeAddress").ConfigureAwait(false);

        return new RuntimeYamlOptions(
            MixedPort: await Settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false),
            ControllerPort: Core.Paths.ControllerPort,
            Secret: Core.Paths.Secret,
            Mode: await Settings.GetAsync("core.mode").ConfigureAwait(false) ?? "rule",
            LogLevel: await Settings.GetAsync("core.logLevel").ConfigureAwait(false) ?? "info",
            AllowLan: await Settings.GetBoolAsync("core.allowLan").ConfigureAwait(false),
            Ipv6: await Settings.GetBoolAsync("core.ipv6", true).ConfigureAwait(false),
            TunEnabled: await Settings.GetBoolAsync("core.tun").ConfigureAwait(false),
            TunStack: await Settings.GetAsync("core.tunStack").ConfigureAwait(false) ?? "mixed",
            TunAutoRoute: await Settings.GetBoolAsync("core.tunAutoRoute", true).ConfigureAwait(false),
            TunAutoRedirect: await Settings.GetBoolAsync("core.tunAutoRedirect").ConfigureAwait(false),
            TunAutoDetectInterface: await Settings.GetBoolAsync("core.tunAutoDetectInterface", true).ConfigureAwait(false),
            TunStrictRoute: await Settings.GetBoolAsync("core.tunStrictRoute").ConfigureAwait(false),
            TunMtu: await Settings.GetIntAsync("core.tunMtu", 0).ConfigureAwait(false),
            TunDnsHijack: dnsHijack,
            TunRouteExcludeAddress: exclude);
    }

    /// <summary>读取以换行分隔的多值设置项，返回去空去重后的列表。</summary>
    private async Task<IReadOnlyList<string>?> GetStringListAsync(string key)
    {
        var raw = await Settings.GetAsync(key).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var items = raw
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        return items.Count > 0 ? items : null;
    }

    /// <summary>当前进程是否以管理员权限运行（TUN 需要）。</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _profileRefreshTimer.Dispose();
        Ssid.Dispose();
        Pac.Dispose();
        Core.Dispose();
        _recorder.Dispose();
    }

    /// <summary>
    /// 退出前停内核并清理会阻断主机上网的痕迹。系统代理的关闭已内聚在 Core.StopAsync 中，
    /// 这里只需保证内核确实停下（内核退出会自动移除 TUN 适配器与路由）。
    ///
    /// 带超时：这是在 UI 线程上同步等待，如果内核正卡在启动流程里（例如 WaitForReady
    /// 等 API 上线的 120 秒），无超时会让「退出」看起来像死机，只能强制结束进程，
    /// 结果系统代理/TUN 残留、下次开机断网。超时后不再阻塞界面。
    /// </summary>
    public void ShutdownNetwork(TimeSpan? timeout = null)
    {
        try
        {
            var limit = timeout ?? TimeSpan.FromSeconds(8);
            _ = Core.StopAsync().Wait(limit);
        }
        catch
        {
        }
        try
        {
            Pac.Stop();
        }
        catch
        {
        }
    }
}
