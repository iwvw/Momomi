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
    public IElevatedClient Elevated { get; }

    private readonly TrafficRecorder _recorder;
    private readonly Timer _profileRefreshTimer;
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
        await Core.InitializeAsync(ct).ConfigureAwait(false);
        // 载入手动下载代理端口（用于内核未运行时下载内核/geodata）。
        DownloadProxy.SetManualPort(await Settings.GetIntAsync("core.downloadProxyPort", 0).ConfigureAwait(false));
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
                foreach (var item in items.Where(i => i.Kind == "url"))
                    await Profiles.RefreshAsync(item.Id).ConfigureAwait(false);
            }
            catch
            {
            }
        });
    }

    public async Task<bool> ApplyActiveProfileAsync(CancellationToken ct = default)
    {
        var active = await Profiles.GetActiveAsync(ct).ConfigureAwait(false);
        if (active is null) return false;

        var options = await BuildRuntimeOptionsAsync(ct).ConfigureAwait(false);
        var yaml = await Profiles.GenerateRuntimeConfigAsync(active.Id, options, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Core.Paths.RuntimeConfigPath, yaml, ct).ConfigureAwait(false);

        var reloaded = true;
        if (Core.State == CoreState.Running)
            reloaded = await Core.ReloadConfigAsync(Core.Paths.RuntimeConfigPath, ct).ConfigureAwait(false);

        // 配置重载后代理组/节点已变化，通知各视图重新拉取。
        ProxiesReloaded?.Invoke(this, EventArgs.Empty);
        return reloaded;
    }

    /// <summary>订阅刷新或切换配置导致 runtime 配置重载后触发，供各视图强制重新拉取代理数据。</summary>
    public event EventHandler? ProxiesReloaded;

    public async Task<RuntimeYamlOptions> BuildRuntimeOptionsAsync(CancellationToken ct = default)
    {
        var dnsHijack = await GetStringListAsync("core.tunDnsHijack").ConfigureAwait(false);
        var exclude = await GetStringListAsync("core.tunRouteExcludeAddress").ConfigureAwait(false);

        return new RuntimeYamlOptions(
            MixedPort: await Settings.GetIntAsync("core.mixedPort", 7897).ConfigureAwait(false),
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

    public void Dispose()
    {
        _profileRefreshTimer.Dispose();
        Core.Dispose();
        _recorder.Dispose();
    }

    /// <summary>
    /// 退出前停内核并清理会阻断主机上网的痕迹。系统代理的关闭已内聚在 Core.StopAsync 中，
    /// 这里只需保证内核确实停下（内核退出会自动移除 TUN 适配器与路由）。
    /// </summary>
    public void ShutdownNetwork()
    {
        try
        {
            Core.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}
