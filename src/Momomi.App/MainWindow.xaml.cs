using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Momomi.App.Mini;
using Momomi.App.Pages;
using Momomi.App.Services;
using Momomi.App.ViewModels;
using Momomi.Core.Services;

namespace Momomi.App;

public sealed partial class MainWindow : Window
{
    private TrayIconService? _tray;
    private MiniWindow? _miniWindow;
    private bool _forceExit;
    private bool _modeBarLoading;
    private double _paneWidth = 200;
    private double _dragStartWidth;
    private Microsoft.UI.Xaml.DispatcherTimer? _sizeSaveTimer;

    private const double MinPaneWidth = 160;
    private const double MaxPaneWidth = 420;

    public ModeSelectorViewModel ModeSelector { get; }

    public Services.HotkeyManager Hotkeys { get; }

    public MainWindow()
    {
        ModeSelector = new ModeSelectorViewModel(AppHost.Host, DispatcherQueue);
        Hotkeys = new Services.HotkeyManager(AppHost.Host, this);

        InitializeComponent();
        Title = "Momomi";
        AppVersionText.Text = $"v{Momomi.Core.Services.AppUpdateService.GetCurrentVersion()}";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 600;
        }

        // 默认窗口尺寸（用户调整后持久化，重启恢复；无记录时用较小的默认值）。
        RestoreWindowSize();
        _sizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _sizeSaveTimer.Tick += async (_, _) =>
        {
            _sizeSaveTimer.Stop();
            await SaveWindowSizeAsync();
        };
        SizeChanged += (_, _) =>
        {
            // 最大化/最小化时不要覆盖已保存的常规尺寸。
            if (AppWindow.Presenter is OverlappedPresenter p
                && p.State == OverlappedPresenterState.Maximized)
                return;
            _sizeSaveTimer?.Stop();
            _sizeSaveTimer?.Start();
        };

        ApplyBackdrop();
        // 不在构造时加载首页：仅用迷你面板 / 静默启动到托盘时不创建页面，
        // 首次真正显示主窗口（Activate/ShowAndActivate）时才加载，降低常驻内存。

        _ = InitializePaneWidthAsync();
        _ = InitializeNavigationStyleAsync();
        _ = InitializeBackdropStyleAsync();

        ModeSelector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModeSelectorViewModel.SelectedIndex))
                SyncModeBar();
        };
        _ = InitializeModeBarAsync();

        Activated += (_, _) =>
        {
            EnsureInitialPageLoaded();
            _ = ModeSelector.RefreshSwitchesAsync();
        };

        CreateTray();
        HookThemeChange();

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        AppHost.Host.Core.StateChanged += (_, _) => Dispatch(UpdateTrayState);
        AppHost.Host.Core.StateChanged += OnCoreStateForRestore;
        AppHost.Host.Core.StateChanged += OnCoreStateForAutoQuit;
        ViewModels.AppSignals.SwitchesChanged += (_, _) => Dispatch(UpdateTrayState);

        _ = Hotkeys.LoadAsync();
        _ = WarmupMiniWindowAsync();
    }

    /// <summary>
    /// 启动后延迟预热迷你窗口：提前完成 XAML 解析与窗口创建，避免首次点击时才同步构造（约 40ms）
    /// 与滑动动画叠加造成卡顿。预热只创建并保持隐藏，不显示。
    /// </summary>
    private async Task WarmupMiniWindowAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    EnsureMiniWindow();
                }
                catch (Exception ex)
                {
                    global::Momomi.App.App.WriteLog($"预热迷你窗口失败：{ex}");
                }
            });
        }
        catch
        {
        }
    }

    // ---- 快捷键触发的操作（经 DispatcherQueue 回到 UI 线程）----

    /// <summary>快捷键：切换系统代理。</summary>
    public void HotkeyToggleSystemProxy() => ToggleSystemProxy();

    /// <summary>快捷键：切换 TUN 模式。</summary>
    public void HotkeyToggleTun()
    {
        try
        {
            var current = AppHost.Host.Settings.GetBoolAsync("core.tun").GetAwaiter().GetResult();
            // 交给 ModeSelector 统一处理（写设置、热切换、广播同步），避免重复写设置。
            _ = ModeSelector.SetTunAsync(!current);
        }
        catch
        {
        }
    }

    /// <summary>快捷键：切换运行模式（0 规则 / 1 全局 / 2 直连）。</summary>
    public async Task HotkeySelectModeAsync(int index)
    {
        try
        {
            await ModeSelector.SelectAsync(index);
            SyncModeBar();
        }
        catch
        {
        }
    }

    private CancellationTokenSource? _autoQuitCts;

    /// <summary>内核停止后若开启自动退出，倒计时后退出应用。</summary>
    private void OnCoreStateForAutoQuit(object? sender, CoreStateChanged e)
    {
        _autoQuitCts?.Cancel();
        _autoQuitCts = null;

        if (e.State is not (CoreState.Stopped or CoreState.Error)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var host = AppHost.Host;
                if (!await host.Settings.GetBoolAsync("ui.autoQuitWithoutCore").ConfigureAwait(false)) return;
                var delay = await host.Settings.GetIntAsync("ui.autoQuitWithoutCoreDelay", 30).ConfigureAwait(false);
                if (delay < 0) delay = 0;

                var cts = new CancellationTokenSource();
                _autoQuitCts = cts;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // 倒计时结束时若内核仍未运行，则退出。
                if (host.Core.State is CoreState.Stopped or CoreState.Error)
                    Dispatch(ExitFromTray);
            }
            catch
            {
            }
        });
    }

    private bool _restoreInFlight;

    /// <summary>内核进入运行态时，恢复上次为各代理组手动选择的节点。</summary>
    private void OnCoreStateForRestore(object? sender, CoreStateChanged e)
    {
        if (e.State != CoreState.Running || _restoreInFlight) return;
        _restoreInFlight = true;
        _ = Task.Run(async () =>
        {
            try
            {
                // 等待内核 API 就绪。
                await Task.Delay(800).ConfigureAwait(false);
                var host = AppHost.Host;
                var saved = await ProxySelectionStore.LoadAsync(host.Settings).ConfigureAwait(false);
                if (saved.Count == 0 || host.Core.Api is null) return;

                var proxies = await host.Core.Api.GetProxiesAsync().ConfigureAwait(false);
                var applied = 0;
                foreach (var (group, node) in saved)
                {
                    if (!proxies.TryGetValue(group, out var groupItem)) continue;
                    if (groupItem.All is null || !groupItem.All.Contains(node)) continue;
                    if (string.Equals(groupItem.Now, node, StringComparison.Ordinal)) continue;
                    try
                    {
                        await host.Core.Api.SelectProxyAsync(group, node).ConfigureAwait(false);
                        applied++;
                    }
                    catch
                    {
                    }
                }

                if (applied > 0)
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    AppSignals.RaiseProxiesChanged();
                }
            }
            catch
            {
            }
            finally
            {
                _restoreInFlight = false;
            }
        });
    }

    /// <summary>切换导航样式：0 = 左侧，1 = 顶部。</summary>
    public void ApplyNavigationStyle(int style)
    {
        try
        {
            NavView.PaneDisplayMode = style == 1
                ? NavigationViewPaneDisplayMode.Top
                : NavigationViewPaneDisplayMode.Left;

            // 顶部导航时侧栏拖拽条没有意义，隐藏。
            _navigationOnTop = style == 1;
            PaneSplitter.Visibility = _navigationOnTop
                ? Visibility.Collapsed
                : (NavView.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed);
        }
        catch
        {
        }
    }

    private bool _navigationOnTop;
    public void ApplyBackdrop()
    {
        try
        {
            if (DesktopAcrylicController.IsSupported())
                SetBackdrop(new Controls.AlwaysActiveAcrylicBackdrop());
            else if (MicaController.IsSupported())
                SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
        }
        catch
        {
        }
    }

    /// <summary>替换系统背景材质，并释放旧实例（AlwaysActiveAcrylicBackdrop 持有控制器需显式释放）。</summary>
    private void SetBackdrop(SystemBackdrop? next)
    {
        var previous = SystemBackdrop;
        SystemBackdrop = next;
        if (!ReferenceEquals(previous, next) && previous is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
            }
        }
    }

    /// <summary>背景材质：0=亚克力(透出下方窗口)，1=Mica，2=纯色。不支持的会回退。</summary>
    public void ApplyBackdropStyle(int style)
    {
        try
        {
            switch (style)
            {
                case 1 when MicaController.IsSupported():
                    SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
                    break;
                case 2:
                    SetBackdrop(null);
                    break;
                default:
                    if (DesktopAcrylicController.IsSupported())
                        SetBackdrop(new Controls.AlwaysActiveAcrylicBackdrop());
                    else if (MicaController.IsSupported())
                        SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
                    else
                        SetBackdrop(null);
                    break;
            }
        }
        catch
        {
        }

        _miniWindow?.ApplyBackdropStyle(style);
    }

    /// <summary>
    /// 订阅根元素的实际主题变化：应用主题设置或系统主题变化都会触发，
    /// 用于实时刷新托盘菜单主题（菜单是独立窗口，不随根元素主题自动传播）。
    /// </summary>
    private void HookThemeChange()
    {
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) =>
            {
                _tray?.ApplyTheme(root.ActualTheme);
                UpdateTrayState();
            };
        }
    }

    /// <summary>
    /// Mica 背景与标题栏按钮不受根元素 RequestedTheme 影响，需要单独跟随主题。
    /// </summary>
    public void ApplyThemeToChrome(string theme)
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var dark = theme == "dark" || (theme == "default" && IsSystemDark());
            var fg = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                          : Windows.UI.Color.FromArgb(255, 0, 0, 0);
            titleBar.ButtonForegroundColor = fg;
            titleBar.ForegroundColor = fg;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch
        {
        }

        // 托盘菜单主题跟随（SecondWindow 是独立窗口，需显式设 ContextMenuThemeMode）。
        var actualTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        _tray?.ApplyTheme(actualTheme);

        // 窗口/托盘图标跟随主题（浅色用黑猫、深色用白猫）。
        UpdateTrayState();
    }

    private static void Dispatch(Action action)
    {
        var queue = App.Main?.DispatcherQueue;
        if (queue is null) action();
        else queue.TryEnqueue(() => action());
    }

    private void Dispatch(Func<Task> action)
        => Dispatch(() => { _ = action(); });

    private void CreateTray()
    {
        try
        {
            _tray = new Services.TrayIconService();
            _tray.LeftClicked += () => Dispatch(OnTrayLeftClick);
            _tray.OpenRequested += () => Dispatch(ShowAndActivate);
            _tray.ToggleMiniRequested += () => Dispatch(ToggleMiniWindow);
            _tray.StartCoreRequested += () => Dispatch(async () => await AppHost.Host.Core.StartAsync());
            _tray.StopCoreRequested += () => Dispatch(async () => await AppHost.Host.Core.StopAsync());
            _tray.RestartCoreRequested += () => Dispatch(async () => await AppHost.Host.Core.RestartAsync());
            _tray.ModeRequested += index => Dispatch(async () => await HotkeySelectModeAsync(index));
            _tray.ToggleSystemProxyRequested += () => Dispatch(ToggleSystemProxy);
            _tray.ToggleTunRequested += () => Dispatch(ToggleTun);
            _tray.ExitRequested += () => Dispatch(ExitFromTray);
            _tray.ApplyTheme(IsAppDark() ? ElementTheme.Dark : ElementTheme.Light);
            _tray.Show(true);
            UpdateTrayState();
        }
        catch
        {
            _tray = null;
        }
    }

    private void UpdateTrayState()
    {
        if (_tray is null) return;
        try
        {
            var core = AppHost.Host.Core;
            var running = core.State == CoreState.Running;

            var (statusText, statusGlyph) = core.State switch
            {
                CoreState.Running => ($"内核：运行中 {core.Version}", "\uEA3B"),
                CoreState.Starting => ("内核：启动中", "\uEA3B"),
                CoreState.Stopping => ("内核：停止中", "\uEA3B"),
                CoreState.Error => ("内核：异常", "\uEA3B"),
                _ => ("内核：已停止", "\uEA3B"),
            };
            var statusBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(core.State switch
            {
                CoreState.Running => Windows.UI.Color.FromArgb(0xFF, 0x3C, 0xB0, 0x4A),   // 绿
                CoreState.Starting => Windows.UI.Color.FromArgb(0xFF, 0xEA, 0xA5, 0x3B),  // 黄
                CoreState.Stopping => Windows.UI.Color.FromArgb(0xFF, 0xF0, 0x7C, 0x00),  // 橙
                CoreState.Error => Windows.UI.Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D),     // 红
                _ => Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E),                    // 灰
            });

            var proxyOn = AppHost.Host.SystemProxy.IsEnabled();
            var tunOn = false;
            try
            {
                tunOn = AppHost.Host.Settings.GetBoolAsync("core.tun").GetAwaiter().GetResult();
            }
            catch
            {
            }

            string iconFile;
            if (running && proxyOn && tunOn) iconFile = "TrayRed.ico";
            else if (running && proxyOn) iconFile = "TrayGreen.ico";
            else iconFile = "TrayDefault.ico";

            _tray.UpdateState(new Services.TrayState(
                statusText,
                statusGlyph,
                statusBrush,
                running ? "Momomi \u00b7 运行中" : "Momomi \u00b7 已停止",
                iconFile,
                CanStart: !running && core.State is not (CoreState.Starting or CoreState.Stopping),
                CanStop: running || core.State is CoreState.Starting or CoreState.Stopping,
                Mode: ModeSelector.Mode,
                MiniVisible: _miniWindow?.IsVisible == true,
                SystemProxyOn: proxyOn,
                TunOn: tunOn));
        }
        catch
        {
        }
    }

    private async void ToggleSystemProxy()
    {
        // 交给 ModeSelector 统一处理：写代理、广播同步到主/迷你面板、支持快速连续操作收敛。
        var host = AppHost.Host;
        try
        {
            var enabled = !host.SystemProxy.IsEnabled();
            await ModeSelector.SetSystemProxyAsync(enabled);
        }
        catch
        {
        }
        UpdateTrayState();
    }

    /// <summary>托盘菜单切换 TUN 模式（复用 ModeSelector 的完整逻辑：写设置、重生成配置、重启内核）。</summary>
    private async void ToggleTun()
    {
        try
        {
            var current = await AppHost.Host.Settings.GetBoolAsync("core.tun").ConfigureAwait(false);
            await ModeSelector.SetTunAsync(!current).ConfigureAwait(false);
        }
        catch
        {
        }
        Dispatch(UpdateTrayState);
    }

    private static bool IsAppDark()
    {
        // App.CurrentTheme 在 ApplyTheme 里同步更新，最可靠。
        if (App.CurrentTheme == "dark") return true;
        if (App.CurrentTheme == "light") return false;

        if (App.Main?.Content is FrameworkElement root)
        {
            if (root.RequestedTheme == ElementTheme.Dark) return true;
            if (root.RequestedTheme == ElementTheme.Light) return false;
        }

        return IsSystemDark();
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceExit) return;

        var behavior = AppHost.Host.Settings.GetAsync("closeBehavior").GetAwaiter().GetResult() ?? "tray";
        if (behavior == "exit")
        {
            ExitFromTray();
            return;
        }

        args.Cancel = true;
        MinimizeToTray();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Hotkeys.Dispose();
        _tray?.Dispose();
        _tray = null;
        _miniWindow?.ForceClose();
        _miniWindow = null;
    }

    private void MinimizeToTray()
    {
        AppWindow.Hide();
        ViewModels.AppSignals.RaiseMainWindowVisibility(false);
        // 释放主界面页面的可视化树并压缩工作集：托盘/仅迷你模式下常驻内存显著下降。
        ReleasePageContent();
    }

    /// <summary>隐藏主窗口到托盘（用于静默启动）。</summary>
    public void HideToTray() => MinimizeToTray();

    /// <summary>启动时直接最小化到托盘（供 App 依据设置调用）。</summary>
    public void StartMinimizedToTray() => MinimizeToTray();

    private void OnTrayLeftClick()
    {
        // H.NotifyIcon 的 LeftClickCommand 只在真实左键单击时触发（右键菜单不触发），无需再区分。
        ToggleMiniWindow();
    }

    public void ToggleMiniWindow()
    {
        EnsureMiniWindow();
        _miniWindow!.ToggleVisible();
        UpdateTrayState();
    }

    public void ShowMiniPanel()
    {
        EnsureMiniWindow();
        _miniWindow!.Show();
        UpdateTrayState();
    }

    /// <summary>惰性创建迷你窗口并订阅关闭通知；拖拽收起关闭后会置空引用，下次调用重建。</summary>
    private void EnsureMiniWindow()
    {
        if (_miniWindow is not null) return;
        var mini = new MiniWindow();
        mini.Dismissed += (_, _) =>
        {
            _miniWindow = null;
            UpdateTrayState();
        };
        _miniWindow = mini;
    }

    public void ApplyMiniPanelTheme(string theme) => _miniWindow?.ApplyTheme(theme);

    public void ShowAndActivate()
    {
        EnsureInitialPageLoaded();
        AppWindow.Show();
        ViewModels.AppSignals.RaiseMainWindowVisibility(true);
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private Type? _lastPageType;

    /// <summary>首次显示或从托盘恢复时加载页面：优先恢复上次的页面类型，否则用首页。</summary>
    private void EnsureInitialPageLoaded()
    {
        if (NavFrame.Content is not null) return;
        _lastPageType ??= typeof(DashboardPage);
        NavFrame.Navigate(_lastPageType);
    }

    /// <summary>隐藏到托盘时释放当前页面内容（保留页面类型），并压缩工作集，降低常驻内存。</summary>
    private void ReleasePageContent()
    {
        try
        {
            if (NavFrame.Content is not null)
                _lastPageType = NavFrame.Content.GetType();
            NavFrame.Content = null;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            TrimWorkingSet();
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"释放页面内容失败：{ex}");
        }
    }

    /// <summary>把物理内存工作集交还系统（不减少私有提交，但任务管理器观感明显下降）。</summary>
    private static void TrimWorkingSet()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _ = EmptyWorkingSet(process.Handle);
        }
        catch
        {
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private void ExitFromTray()
    {
        _forceExit = true;
        _miniWindow?.ForceClose();
        _miniWindow = null;
        _tray?.Dispose();
        _tray = null;

        try
        {
            // 先关系统代理、停内核（会移除 TUN 适配器与路由），再清理遗留进程，避免退出后主机断网。
            AppHost.Host.ShutdownNetwork();
            AppHost.Host.Process.KillOrphansAsync(AppHost.Host.Core.Paths.BinaryPath)
                .GetAwaiter().GetResult();
            AppHost.Host.Elevated.KillOrphanHosts();
        }
        catch
        {
        }

        AppHost.Shutdown();
        Close();
    }

    /// <summary>
    /// 应用自更新退出：停内核/TUN/系统代理后启动静默更新脚本（等待本进程退出后
    /// 安装/覆盖并以 --startcore 重启），随后退出主程序。
    /// </summary>
    public void ExitForUpdate()
    {
        var script = AppHost.Host.AppUpdate.ConsumePendingScript();
        if (string.IsNullOrEmpty(script) || !File.Exists(script))
        {
            ExitFromTray();
            return;
        }

        _forceExit = true;

        try
        {
            // 先用 cmd 以隐藏窗口方式启动更新脚本，再停内核并退出，让脚本接管后续。
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            ExitFromTray();
            return;
        }

        try
        {
            // 停系统代理、停内核（移除 TUN 适配器与路由），清理遗留进程后立即退出。
            AppHost.Host.ShutdownNetwork();
            AppHost.Host.Process.KillOrphansAsync(AppHost.Host.Core.Paths.BinaryPath)
                .GetAwaiter().GetResult();
            AppHost.Host.Elevated.KillOrphanHosts();
        }
        catch
        {
        }

        AppHost.Shutdown();
        Close();
    }

    private void SyncModeBar()
    {
        _modeBarLoading = true;
        try
        {
            var index = ModeSelector.SelectedIndex;
            ModeRuleButton.IsChecked = index == 0;
            ModeGlobalButton.IsChecked = index == 1;
            ModeDirectButton.IsChecked = index == 2;
        }
        finally
        {
            _modeBarLoading = false;
        }
        UpdateTrayState();
    }

    private const double DefaultWindowWidth = 1080;
    private const double DefaultWindowHeight = 700;

    /// <summary>恢复上次窗口尺寸；无记录时用较小的默认尺寸。</summary>
    private void RestoreWindowSize()
    {
        try
        {
            var w = AppHost.Host.Settings.GetIntAsync("ui.windowWidth", 0).GetAwaiter().GetResult();
            var h = AppHost.Host.Settings.GetIntAsync("ui.windowHeight", 0).GetAwaiter().GetResult();
            if (w <= 0 || h <= 0)
            {
                w = (int)DefaultWindowWidth;
                h = (int)DefaultWindowHeight;
            }
            w = Math.Max(w, 900);
            h = Math.Max(h, 600);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        }
        catch
        {
        }
    }

    /// <summary>持久化窗口尺寸（防抖后写设置）。</summary>
    private async Task SaveWindowSizeAsync()
    {
        try
        {
            var size = AppWindow.Size;
            await AppHost.Host.Settings.SetIntAsync("ui.windowWidth", size.Width);
            await AppHost.Host.Settings.SetIntAsync("ui.windowHeight", size.Height);
        }
        catch
        {
        }
    }

    private async Task InitializePaneWidthAsync()
    {
        try
        {
            var saved = await AppHost.Host.Settings.GetIntAsync("ui.paneWidth", 200);
            _paneWidth = Math.Clamp(saved, MinPaneWidth, MaxPaneWidth);
        }
        catch
        {
            _paneWidth = 200;
        }
        ApplyPaneWidth();
    }

    private async Task InitializeNavigationStyleAsync()
    {
        int style;
        try
        {
            style = await AppHost.Host.Settings.GetIntAsync("ui.navigationStyle", 0);
        }
        catch
        {
            style = 0;
        }
        ApplyNavigationStyle(style);
    }

    private async Task InitializeBackdropStyleAsync()
    {
        int style;
        try
        {
            style = await AppHost.Host.Settings.GetIntAsync("ui.backdropStyle", 0);
        }
        catch
        {
            style = 0;
        }
        ApplyBackdropStyle(style);
    }

    private void ApplyPaneWidth()
    {
        NavView.OpenPaneLength = _paneWidth;
        PaneSplitter.Margin = new Thickness(_paneWidth, 0, 0, 0);
        PaneSplitter.Visibility = !_navigationOnTop && NavView.IsPaneOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PaneSplitter_DragStarted(object? sender, EventArgs e)
    {
        _dragStartWidth = _paneWidth;
    }

    private void PaneSplitter_Dragged(object? sender, double deltaX)
    {
        _paneWidth = Math.Clamp(_dragStartWidth + deltaX, MinPaneWidth, MaxPaneWidth);
        ApplyPaneWidth();
    }

    private void PaneSplitter_DragCompleted(object? sender, EventArgs e)
    {
        _ = AppHost.Host.Settings.SetIntAsync("ui.paneWidth", (int)Math.Round(_paneWidth));
    }

    private void NavView_PaneChanged(NavigationView sender, object args) => ApplyPaneWidth();

    private async Task InitializeModeBarAsync()
    {
        await ModeSelector.LoadAsync();
        SyncModeBar();
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modeBarLoading) return;
        if (sender is not ToggleButton button) return;
        if (button.Tag is not string tag || !int.TryParse(tag, out var index)) return;

        if (ModeSelector.SelectedIndex == index)
        {
            SyncModeBar();
            return;
        }

        await ModeSelector.SelectAsync(index);
        SyncModeBar();
    }

    private void SystemProxy_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ModeSelector.SystemProxyOn) return;
        _ = ModeSelector.SetSystemProxyAsync(toggle.IsOn);
    }

    private void Tun_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ModeSelector.TunOn) return;
        _ = ModeSelector.SetTunAsync(toggle.IsOn);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }
        if (args.SelectedItem is not NavigationViewItem item) return;
        switch (item.Tag)
        {
            case "dashboard": NavFrame.Navigate(typeof(DashboardPage)); break;
            case "proxies": NavFrame.Navigate(typeof(ProxiesPage)); break;
            case "connections": NavFrame.Navigate(typeof(ConnectionsPage)); break;
            case "rules": NavFrame.Navigate(typeof(RulesPage)); break;
            case "profiles": NavFrame.Navigate(typeof(ProfilesPage)); break;
            case "traffic": NavFrame.Navigate(typeof(TrafficPage)); break;
            case "network": NavFrame.Navigate(typeof(NetworkPage)); break;
            case "logs": NavFrame.Navigate(typeof(LogsPage)); break;
        }
    }

    public void NavigateToTag(string tag)
    {
        // 通过 DispatcherQueue 延后执行，确保 NavigationView 已完成加载。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.Equals(tag, "settings", StringComparison.OrdinalIgnoreCase))
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }

            foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
            {
                if (item.Tag is string value && value == tag)
                {
                    NavView.SelectedItem = item;
                    return;
                }
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
