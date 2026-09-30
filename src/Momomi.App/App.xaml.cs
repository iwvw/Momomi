using Microsoft.UI.Xaml;
using Momomi.Core.Services;

namespace Momomi.App;

public partial class App : Application
{
    private Window? _window;

    public static MainWindow? Main { get; private set; }

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
        try
        {
            await AppHost.InitializeAsync();
        }
        catch (Exception ex)
        {
            WriteLog($"MomomiHost 初始化失败：{ex}");
        }

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
            await host.Core.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteLog($"自动启动内核失败：{ex}");
        }
    }
}
