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
    private const string MutexName = "Momomi.SingleInstance.v1";
    private const string WindowTitle = "Momomi";

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

    /// <summary>尝试成为唯一实例。返回 false 表示已有实例在运行。</summary>
    private static bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
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

    /// <summary>通知已有实例把窗口/迷你面板带到前台。</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var hwnd = FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }
        catch
        {
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

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
                main.DispatcherQueue.TryEnqueue(() => main.ShowMiniPanel());
        }
        catch (Exception ex)
        {
            WriteLog($"显示迷你面板失败：{ex}");
        }
    }

    /// <summary>启动时后台静默检查应用更新（不打扰，结果供设置页展示）。</summary>
    private static async Task AutoCheckUpdateAsync()
    {
        try
        {
            var enabled = await AppHost.Host.Settings.GetBoolAsync("ui.autoCheckAppUpdate", true).ConfigureAwait(false);
            if (!enabled) return;
            // 延迟到内核启动后，避免与启动流量抢带宽。
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            _ = AppHost.Host.AppUpdate.CheckAsync();
        }
        catch (Exception ex)
        {
            WriteLog($"自动检查更新失败：{ex}");
        }
    }

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
            ApplyTheme(theme);
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
            await host.Core.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteLog($"自动启动内核失败：{ex}");
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
