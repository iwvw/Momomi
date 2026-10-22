using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Momomi.Core.Services;

namespace Momomi.App;

public partial class App : Application
{
    private Window? _window;

    public static MainWindow? Main { get; private set; }

    // 单实例互斥：防止开机自启计划任务、托盘重复点击、用户多次双击等导致多开。
    private static Mutex? _instanceMutex;
    private const string MutexName = "Momomi.SingleInstance.v2";
    private const string WindowTitle = "Momomi";

    /// <summary>
    /// 参与单实例判定的程序目录。
    ///
    /// 互斥体按「身份」区分而不是写死一个名字：安装版的每次升级都会生成新目录（_isXXXX），
    /// 写死同名会让「旧目录残留的实例」和「新装的实例」互斥失败，两个程序各自拉起一个内核，
    /// 抢同一个 9090 控制端口和同一个 runtime.yaml——被挤掉的那个会把配置悄悄写坏或
    /// 拿着旧配置继续跑，正好表现为「订阅更新了但节点没变」。
    ///
    /// 同一份程序则永远命中同一个互斥体：包括以管理员（提权）与普通权限先后启动（全局命名空间）。
    /// 不看代码签名，因为安装版（Inno）与 MSIX 版签名证书不同，但两者都用同一套数据目录，不该同时跑。
    /// </summary>
    private static string SingleInstanceKey
    {
        get
        {
            try
            {
                var dir = (AppContext.BaseDirectory ?? "").TrimEnd('\\', '/').ToLowerInvariant();
                return dir.Length == 0 ? "root" : dir;
            }
            catch
            {
                return "root";
            }
        }
    }

    private static string MutexNameFor(string key)
    {
        // 全局命名空间：同一会话内提权/非提权实例互相看得见。路径哈希进名字，避免目录名里的非法字符。
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        return $@"Global\{MutexName}.{hash}";
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Momomi", "app-crash.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteLog($"UnhandledException: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteLog($"AppDomain: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => WriteLog($"UnobservedTask: {e.Exception}");
    }

    public static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 数据层初始化失败时的兜底提示：弹一个原生消息框说明原因与日志位置，然后退出。
    /// 比静默崩溃好排查得多。
    /// </summary>
    private static void ShowStartupFailure()
    {
        try
        {
            MessageBoxW(IntPtr.Zero,
                "Momomi 初始化失败，无法读取数据目录。\n\n" +
                $"数据目录：{Momomi.Data.MomomiAppData.ResolveDirectory()}\n" +
                $"诊断日志：{LogPath}\n\n" +
                "常见原因：程序目录不可写、数据库被另一个实例占用、磁盘空间不足。",
                "Momomi 启动失败",
                0x00000010 /* MB_ICONERROR */);
        }
        catch
        {
        }

        Current.Exit();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>尝试成为唯一实例。返回 false 表示已有实例在运行。</summary>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexNameFor(SingleInstanceKey), out var createdNew);
            if (createdNew) return true;
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch
        {
            // 互斥体异常时不阻止启动（宁多开不失败）。
            return true;
        }
    }

    /// <summary>
    /// 通知已有实例把窗口或迷你面板带到前台。
    ///
    /// 按固定标题 FindWindow 在这里不可行：隐藏到托盘时主窗口会改标题（常驻流量），
    /// 迷你面板则是无标题的置顶小窗，按标题查不到就会悄无声息地什么都不做。
    /// 改成枚举顶层窗口，按「进程名 + 标题」定位。
    /// </summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var own = Environment.ProcessId;
            IntPtr fallback = IntPtr.Zero;

            EnumWindows((hwnd, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == own || !IsWindowVisible(hwnd)) return true;

                    var owner = GetProcessName(pid);
                    if (!string.Equals(owner, "Momomi", StringComparison.OrdinalIgnoreCase)) return true;

                    var title = GetWindowTitle(hwnd);
                    if (title.Contains(WindowTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                        fallback = IntPtr.Zero;
                        return false;
                    }

                    // 迷你面板标题为空：记下来兜底（直接点托盘图标时它就是用户想看到的那个）。
                    if (fallback == IntPtr.Zero) fallback = hwnd;
                    return true;
                }
                catch
                {
                    return true;
                }
            }, IntPtr.Zero);

            if (fallback != IntPtr.Zero)
            {
                ShowWindow(fallback, SW_RESTORE);
                SetForegroundWindow(fallback);
            }
        }
        catch
        {
        }
    }

    private static string? GetProcessName(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var buffer = new System.Text.StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void ApplyTheme(string theme)
    {
        // Application.RequestedTheme 只能在窗口创建前设置，运行时赋值会抛异常，因此必须保护。
        try
        {
            Current.RequestedTheme = theme switch
            {
                "dark" => ApplicationTheme.Dark,
                "light" => ApplicationTheme.Light,
                _ => Current.RequestedTheme,
            };
        }
        catch
        {
        }

        CurrentTheme = theme;

        // 运行时切换主题靠根元素的 RequestedTheme，它会向下传播到所有子控件。
        if (Main?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "dark" => ElementTheme.Dark,
                "light" => ElementTheme.Light,
                _ => ElementTheme.Default,
            };
        }

        Main?.ApplyThemeToChrome(theme);
        Main?.ApplyMiniPanelTheme(theme);
    }

    public static string CurrentTheme { get; private set; } = "default";

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例：已有实例在运行时，激活它的窗口后直接退出本进程，避免多开。
        if (!TryAcquireSingleInstance())
        {
            ActivateExistingInstance();
            Exit();
            return;
        }

        try
        {
            await AppHost.InitializeAsync();
        }
        catch (Exception ex)
        {
            WriteLog($"MomomiHost 初始化失败：{ex}");
        }

        // 初始化失败时 AppHost.Host 会抛异常，而 MainWindow 构造函数第一件事就是取它。
        // 之前只记日志就继续 new MainWindow()，结果是启动即崩且没有任何提示
        // （数据目录只读、数据库被其它实例占用、磁盘写满都会走到这里）。
        if (!AppHost.IsReady)
        {
            ShowStartupFailure();
            return;
        }

        // 修复历史版本用 schtasks 默认参数创建的自启任务：其电源/空闲/超时设置会在
        // 笔记本拔电源、系统空闲或连续运行 72 小时后被任务计划程序终止（表现为程序自己退出）。
        _ = Task.Run(() =>
        {
            try
            {
                if (AppHost.IsReady && AppHost.Host.Startup.RepairIfNeeded())
                    WriteLog("已修复自启计划任务的电源/空闲/超时设置");
            }
            catch (Exception ex)
            {
                WriteLog($"修复自启任务失败：{ex}");
            }
        });

        _window = new MainWindow();
        Main = (MainWindow)_window;

        var launchArgs = Environment.GetCommandLineArgs();
        var launchedByStartup = launchArgs.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        var launchedAsMini = launchArgs.Any(a => string.Equals(a, "--mini", StringComparison.OrdinalIgnoreCase));

        // 仅迷你面板模式：不显示主窗口，只拉起迷你面板，省去主窗口整棵可视化树的显示开销。
        if (launchedAsMini)
        {
            _ = ShowMiniOnlyAsync();
            _ = ApplyThemeFromSettingsAsync();
            _ = AutoStartCoreAsync();
            _ = AutoCheckUpdateAsync();
            return;
        }

        // 静默启动（开机自启 + 该开关打开）时不显示主窗口，最小化到托盘。
        var silent = false;
        try
        {
            if (launchedByStartup)
                silent = await AppHost.Host.Settings.GetBoolAsync("ui.silentStart").ConfigureAwait(false);
        }
        catch
        {
        }

        // 「启动时最小化到托盘」：每次启动都不显示主窗口，只保留托盘与迷你面板。
        var startMinimized = false;
        try
        {
            startMinimized = await AppHost.Host.Settings.GetBoolAsync("ui.startMinimized").ConfigureAwait(false);
        }
        catch
        {
        }

        if (silent || startMinimized)
        {
            var main = Main;
            if (main is not null)
                main.DispatcherQueue.TryEnqueue(() => main.StartMinimizedToTray());
        }
        else
        {
            _window.Activate();
        }

        _ = ApplyThemeFromSettingsAsync();
        _ = AutoStartCoreAsync();
        _ = NavigateFromCommandLineAsync();
        _ = AutoCheckUpdateAsync();
    }

    /// <summary>仅迷你面板模式：延迟等待就绪后只弹出迷你面板，不激活主窗口。</summary>
    private static async Task ShowMiniOnlyAsync()
    {
        try
        {
            await Task.Delay(800).ConfigureAwait(false);
            var main = Main;
            if (main is not null)
            {
                // 开机只显示迷你面板时没有托盘点击事件，用主任务栏所在屏幕作为落点。
                var monitor = Mini.DpiLayout.MonitorForTrayTaskbar();
                main.DispatcherQueue.TryEnqueue(() => main.ShowMiniPanel(monitor));
            }
        }
        catch (Exception ex)
        {
            WriteLog($"显示迷你面板失败：{ex}");
        }
    }

    /// <summary>
    /// 后台检查应用更新：启动后查一次，之后每 6 小时一次。
    ///
    /// 之前只在启动时查一次，结果也只有设置页能读到——一直开着不关的用户永远
    /// 看不到有新版本。现在把结果广播给顶栏胶囊标签，并按固定间隔复查。
    /// 失败不打扰用户（错误只写进 LastResult 与日志）。
    /// </summary>
    private static async Task AutoCheckUpdateAsync()
    {
        try
        {
            var enabled = await AppHost.Host.Settings.GetBoolAsync("ui.autoCheckAppUpdate", true).ConfigureAwait(false);
            if (!enabled) return;

            // 延迟到内核启动后，避免与启动流量抢带宽。
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);

            while (true)
            {
                await CheckAppUpdateOnceAsync().ConfigureAwait(false);

                // 复查间隔。用设置项便于以后调整，默认 6 小时。
                var hours = await AppHost.Host.Settings.GetIntAsync("ui.checkAppUpdateIntervalHours", 6).ConfigureAwait(false);
                if (hours is < 1 or > 168) hours = 6;
                await Task.Delay(TimeSpan.FromHours(hours)).ConfigureAwait(false);

                // 每次复查前重新读开关，用户中途关掉了就停。
                if (!await AppHost.Host.Settings.GetBoolAsync("ui.autoCheckAppUpdate", true).ConfigureAwait(false))
                    return;
            }
        }
        catch (Exception ex)
        {
            WriteLog($"自动检查更新失败：{ex}");
        }
    }

    /// <summary>执行一次检查并把结果广播给界面（顶栏胶囊标签）。</summary>
    private static async Task CheckAppUpdateOnceAsync()
    {
        try
        {
            var info = await AppHost.Host.AppUpdate.CheckAsync().ConfigureAwait(false);

            // 有更新就广播；MSIX 形态不支持应用内自更新，不提示。
            var updatable = info.HasUpdate
                            && !string.IsNullOrEmpty(info.LatestVersion)
                            && !IsMsixPackaged();
            ViewModels.AppSignals.RaiseAppUpdateNotice(
                updatable ? new ViewModels.AppUpdateNotice(info.LatestVersion!) : null);

            if (info.Error is not null) WriteLog($"检查应用更新失败：{info.Error}");
        }
        catch (Exception ex)
        {
            WriteLog($"检查应用更新异常：{ex}");
        }
    }

    /// <summary>是否运行在 MSIX 包身份下（该形态不支持应用内覆盖式自更新）。</summary>
    private static bool IsMsixPackaged()
    {
        try
        {
            // 非打包进程返回 APPMODEL_ERROR_NO_PACKAGE(15700)。
            uint length = 0;
            return GetCurrentPackageFullName(ref length, null) != 15700;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, System.Text.StringBuilder? packageFullName);

    private static async Task NavigateFromCommandLineAsync()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            string? tag = null;
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--page", StringComparison.OrdinalIgnoreCase))
                {
                    tag = args[i + 1];
                    break;
                }
            }

            if (string.IsNullOrEmpty(tag)) return;

            // 等待窗口与 NavigationView 完成加载。
            await Task.Delay(600);
            Main?.NavigateToTag(tag);
        }
        catch (Exception ex)
        {
            WriteLog($"处理 --page 参数失败：{ex}");
        }
    }

    private static async Task ApplyThemeFromSettingsAsync()
    {
        try
        {
            var theme = await AppHost.Host.Settings.GetAsync("theme").ConfigureAwait(false) ?? "default";
            // ApplyTheme 会访问 Window.Content / AppWindow.TitleBar，只能在 UI 线程执行。
            // 之前直接在这里调用，续体落在线程池上，异常被外层 catch 吞掉，
            // 表现为「启动时读到的主题设置从未生效，只有从设置页切换才管用」。
            var queue = Main?.DispatcherQueue;
            if (queue is null)
            {
                ApplyTheme(theme);
                return;
            }

            if (queue.HasThreadAccess) ApplyTheme(theme);
            else queue.TryEnqueue(() => ApplyTheme(theme));
        }
        catch (Exception ex)
        {
            WriteLog($"应用主题失败：{ex}");
        }
    }

    private static async Task AutoStartCoreAsync()
    {
        try
        {
            var host = AppHost.Host;
            // 更新后重新启动并附带 --startcore：无论设置如何都强制拉起内核，恢复更新前状态。
            var fromUpdate = Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--startcore", StringComparison.OrdinalIgnoreCase));
            var autoStart = fromUpdate
                || await host.Settings.GetBoolAsync("core.autoStart", true).ConfigureAwait(false);
            if (!autoStart) return;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            // 启动时若开启自动更新订阅，先刷新当前订阅再应用（当前订阅需立即可用）。
            try
            {
                var autoUpdate = await host.Settings.GetBoolAsync("profile.autoUpdate", true).ConfigureAwait(false);
                if (autoUpdate)
                {
                    var active = await host.Profiles.GetActiveAsync().ConfigureAwait(false);
                    if (active is { Kind: "url" })
                        await host.Profiles.RefreshAsync(active.Id).ConfigureAwait(false);

                    // 其余订阅后台错峰刷新（每个间隔数秒），避免启动时挤占网络。
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var items = await host.Profiles.ListAsync().ConfigureAwait(false);
                            var others = items.Where(i => i.Kind == "url" && i.Id != active?.Id).ToList();
                            for (var i = 0; i < others.Count; i++)
                            {
                                await host.Profiles.RefreshAsync(others[i].Id).ConfigureAwait(false);
                                await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                        }
                    });
                }
            }
            catch
            {
            }

            // 先应用当前订阅生成运行时配置，再启动内核。
            await host.ApplyActiveProfileAsync().ConfigureAwait(false);
            await EnsureKernelInstalledAsync(host).ConfigureAwait(false);

            // 内核还没起来时，注册表里若残留着指向本应用端口的代理，等于断网
            // （崩溃/强杀后常见）。必须在启动内核之前处理：此时"指向自己"基本可判定为残留。
            await PrepareSystemProxyBeforeStartAsync(host).ConfigureAwait(false);

            var started = await host.Core.StartAsync().ConfigureAwait(false);

            // 内核就绪后按「用户意图」恢复系统代理（含上次崩溃/升级重启前的状态）。
            if (started) await RestoreSystemProxyAsync(host).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteLog($"自动启动内核失败：{ex}");
        }
    }

    /// <summary>
    /// 启动内核之前处理系统代理：补记意图 + 清理崩溃残留。
    /// 这两件事都必须在「内核未运行」的窗口期完成，否则无法区分
    /// 「上次崩溃遗留的坏代理」和「用户本来开着的正常代理」。
    /// </summary>
    private static async Task PrepareSystemProxyBeforeStartAsync(MomomiHost host)
    {
        try
        {
            var port = await host.Settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false);

            // 顺序很重要：先按现状补记意图（首次升级时没有记录），再清理残留。
            // 反过来会导致清掉之后无从判断用户本来是不是开着。
            if (await SystemProxyState.BootstrapDesiredIfMissingAsync(host.Settings, host.SystemProxy, port)
                    .ConfigureAwait(false))
            {
                WriteLog("首次记录系统代理意图（依据升级前的注册表状态）");
            }

            if (SystemProxyState.ClearStaleProxy(host.SystemProxy, port))
                WriteLog($"已清理上次异常退出遗留的系统代理（127.0.0.1:{port}）——内核此前未运行，该代理只会导致断网");
        }
        catch (Exception ex)
        {
            WriteLog($"启动前处理系统代理失败：{ex}");
        }
    }

    /// <summary>
    /// 按持久化的用户意图恢复系统代理（内核已就绪后调用）。
    ///
    /// 退出时一律会关掉自己的代理（避免指向已关闭的端口），因此「上次开着」这个状态
    /// 必须靠 <c>ui.systemProxyDesired</c> 记录并在启动时重放，否则每次重启都要手动再开一次。
    /// </summary>
    private static async Task RestoreSystemProxyAsync(MomomiHost host)
    {
        try
        {
            var port = await host.Settings.GetIntAsync("core.mixedPort", 7890).ConfigureAwait(false);

            var restored = await SystemProxyState.RestoreAsync(
                host.Settings, host.SystemProxy, port,
                applyPac: () =>
                {
                    // PAC 模式：本地服务器监听随机端口，脚本指向混合端口。
                    if (!host.Pac.Start(port)) return false;
                    return host.SystemProxy.EnablePac(host.Pac.PacUrl ?? $"http://127.0.0.1:{port}/proxy.pac");
                }).ConfigureAwait(false);

            if (restored)
                WriteLog("已按上次状态恢复系统代理");

            // 通知界面刷新开关与托盘图标。
            ViewModels.AppSignals.RaiseSwitchesChanged();
        }
        catch (Exception ex)
        {
            WriteLog($"恢复系统代理失败：{ex}");
        }
    }

    /// <summary>
    /// 内核缺失时自动下载（修精简版首装「找不到内核」）。
    /// 已内置内核（完整版释放过）或网络失败则跳过，交由用户到设置页手动处理。
    /// 全程通过 AppSignals 上报进度，前端首页/托盘可显示反馈。
    /// </summary>
    private static async Task EnsureKernelInstalledAsync(MomomiHost host)
    {
        if (File.Exists(host.KernelUpdate.BinaryPath)) return;

        try
        {
            WriteLog("未找到内核，尝试自动下载…");
            ViewModels.AppSignals.RaiseKernelInstall(new ViewModels.KernelInstallProgress("正在检查内核版本…", null));

            var info = await host.KernelUpdate.CheckAsync().ConfigureAwait(false);
            var target = info.LatestVersion;
            if (string.IsNullOrEmpty(target))
            {
                WriteLog($"自动下载内核失败：无法确定版本（{info.Error}）");
                ViewModels.AppSignals.RaiseKernelInstall(new ViewModels.KernelInstallProgress("内核下载失败，请在设置页手动下载", null));
                return;
            }

            var downloadProgress = new Progress<double>(p =>
                ViewModels.AppSignals.RaiseKernelInstall(
                    new ViewModels.KernelInstallProgress($"正在下载内核 {target}… {p * 100:0}%", p * 100)));

            var ok = await host.KernelUpdate.DownloadAndInstallAsync(target, downloadProgress).ConfigureAwait(false);
            if (!ok)
            {
                WriteLog("自动下载内核失败，请在设置页手动下载");
                ViewModels.AppSignals.RaiseKernelInstall(new ViewModels.KernelInstallProgress("内核下载失败，请在设置页手动下载", null));
                return;
            }

            try
            {
                ViewModels.AppSignals.RaiseKernelInstall(new ViewModels.KernelInstallProgress("正在下载地理数据…", null));
                var geoProgress = new Progress<double>(p =>
                    ViewModels.AppSignals.RaiseKernelInstall(
                        new ViewModels.KernelInstallProgress($"正在下载地理数据… {p * 100:0}%", p * 100)));
                await host.KernelUpdate.EnsureGeodataAsync(geoProgress).ConfigureAwait(false);
                if (await host.Settings.GetBoolAsync("core.tun").ConfigureAwait(false))
                    await host.KernelUpdate.EnsureWintunAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            WriteLog($"内核已自动下载到 {host.KernelUpdate.BinaryPath}");
        }
        catch (Exception ex)
        {
            WriteLog($"自动下载内核异常：{ex}");
        }
        finally
        {
            // 清除安装态（无论成功失败），首页/托盘恢复正常显示。
            ViewModels.AppSignals.RaiseKernelInstall(null);
        }
    }
}
