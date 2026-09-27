using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Momomi.App.Mini;
using Momomi.App.Pages;
using Momomi.App.ViewModels;
using Momomi.Core.Services;
using Forms = System.Windows.Forms;

namespace Momomi.App;

public sealed partial class MainWindow : Window
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _coreStatusItem;
    private Forms.ToolStripMenuItem? _systemProxyItem;
    private Forms.ToolStripMenuItem? _tunItem;
    private Forms.ToolStripMenuItem? _miniPanelItem;
    private MiniWindow? _miniWindow;
    private bool _forceExit;
    private bool _trayMenuWasOpened;
    private bool _modeBarLoading;
    private double _paneWidth = 200;
    private double _dragStartWidth;

    private const double MinPaneWidth = 160;
    private const double MaxPaneWidth = 420;

    public ModeSelectorViewModel ModeSelector { get; }

    public MainWindow()
    {
        ModeSelector = new ModeSelectorViewModel(AppHost.Host, DispatcherQueue);

        InitializeComponent();
        Title = "Momomi";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 600;
        }

        ApplyBackdrop();
        NavFrame.Navigate(typeof(DashboardPage));

        _ = InitializePaneWidthAsync();
        _ = InitializeNavigationStyleAsync();
        _ = InitializeBackdropStyleAsync();

        ModeSelector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModeSelectorViewModel.SelectedIndex))
                SyncModeBar();
        };
        _ = InitializeModeBarAsync();

        Activated += (_, _) => _ = ModeSelector.RefreshSwitchesAsync();

        CreateTrayIcon();

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        AppHost.Host.Core.StateChanged += (_, _) => Dispatch(UpdateTrayState);
        AppHost.Host.Core.StateChanged += OnCoreStateForRestore;
        AppHost.Host.Core.StateChanged += OnCoreStateForAutoQuit;
        ViewModels.AppSignals.SwitchesChanged += (_, _) => Dispatch(UpdateTrayState);
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
            if (MicaController.IsSupported())
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            else if (DesktopAcrylicController.IsSupported())
                SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
        }
    }

    /// <summary>背景材质：0=Mica，1=亚克力(Mica Alt)，2=纯色。不支持的会回退。</summary>
    public void ApplyBackdropStyle(int style)
    {
        try
        {
            switch (style)
            {
                case 1 when MicaController.IsSupported():
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                    break;
                case 2:
                    SystemBackdrop = null;
                    break;
                default:
                    if (MicaController.IsSupported())
                        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    else if (DesktopAcrylicController.IsSupported())
                        SystemBackdrop = new DesktopAcrylicBackdrop();
                    break;
            }
        }
        catch
        {
        }

        _miniWindow?.ApplyBackdropStyle(style);
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

        // 窗口/托盘图标跟随主题（浅色用黑猫、深色用白猫）。
        UpdateTrayState();
    }

    private void CreateTrayIcon()
    {
        try
        {
            var initialFile = "TrayDefault.ico";
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", initialFile)),
                Text = "Momomi",
                Visible = true,
            };
            _lastTrayIconFile = initialFile;
            _notifyIcon.MouseDown += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    Dispatch(() => OnTrayLeftClick());
            };

            var menu = new Forms.ContextMenuStrip();
            menu.Opening += (_, _) =>
            {
                _trayMenuWasOpened = true;
                ApplyMenuTheme(menu);
                UpdateTrayState();
            };

            _coreStatusItem = new Forms.ToolStripMenuItem("内核：未知") { Enabled = false };
            menu.Items.Add(_coreStatusItem);

            menu.Items.Add(new Forms.ToolStripMenuItem("打开主界面", null, (_, _) => Dispatch(ShowAndActivate)));
            _miniPanelItem = new Forms.ToolStripMenuItem("显示迷你面板", null, (_, _) => Dispatch(ToggleMiniWindow));
            menu.Items.Add(_miniPanelItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add(new Forms.ToolStripMenuItem("启动内核", null, (_, _) => Dispatch(async () => await AppHost.Host.Core.StartAsync())));
            menu.Items.Add(new Forms.ToolStripMenuItem("停止内核", null, (_, _) => Dispatch(async () => await AppHost.Host.Core.StopAsync())));
            menu.Items.Add(new Forms.ToolStripMenuItem("重启内核", null, (_, _) => Dispatch(async () => await AppHost.Host.Core.RestartAsync())));

            menu.Items.Add(new Forms.ToolStripSeparator());

            _systemProxyItem = new Forms.ToolStripMenuItem("系统代理", null, (_, _) => Dispatch(ToggleSystemProxy))
            {
                CheckOnClick = false,
            };
            menu.Items.Add(_systemProxyItem);

            _tunItem = new Forms.ToolStripMenuItem("TUN 模式", null, (_, _) => Dispatch(ToggleTun))
            {
                CheckOnClick = false,
            };
            menu.Items.Add(_tunItem);

            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => Dispatch(ExitFromTray)));

            _notifyIcon.ContextMenuStrip = menu;
            UpdateTrayState();
        }
        catch
        {
            _notifyIcon = null;
        }
    }

    private static void Dispatch(Action action)
    {
        var queue = App.Main?.DispatcherQueue;
        if (queue is null) action();
        else queue.TryEnqueue(() => action());
    }

    private void Dispatch(Func<Task> action)
        => Dispatch(() => _ = action());

    private void UpdateTrayState()
    {
        if (_notifyIcon is null) return;
        try
        {
            var core = AppHost.Host.Core;
            if (_coreStatusItem is not null)
            {
                _coreStatusItem.Text = core.State switch
                {
                    CoreState.Running => $"内核：运行中 {core.Version}",
                    CoreState.Starting => "内核：启动中",
                    CoreState.Stopping => "内核：停止中",
                    CoreState.Error => "内核：异常",
                    _ => "内核：已停止",
                };
            }

            var proxyOn = AppHost.Host.SystemProxy.IsEnabled();
            if (_systemProxyItem is not null)
                _systemProxyItem.Checked = proxyOn;

            var tunOn = false;
            try
            {
                tunOn = AppHost.Host.Settings.GetBoolAsync("core.tun").GetAwaiter().GetResult();
            }
            catch
            {
            }
            if (_tunItem is not null)
                _tunItem.Checked = tunOn;

            _notifyIcon.Text = core.State == CoreState.Running
                ? $"Momomi · 运行中"
                : "Momomi · 已停止";

            UpdateTrayIcon(core.State, proxyOn, tunOn);
        }
        catch
        {
        }
    }

    private string? _lastTrayIconFile;

    /// <summary>
    /// 托盘图标随状态切换（磁贴样式）：
    /// 系统代理与 TUN 同时开启=红；仅系统代理开=绿；其余（含仅 TUN 开）=默认。
    /// </summary>
    private void UpdateTrayIcon(CoreState state, bool proxyOn, bool tunOn)
    {
        if (_notifyIcon is null) return;
        try
        {
            var running = state == CoreState.Running;
            string file;
            if (running && proxyOn && tunOn) file = "TrayRed.ico";
            else if (running && proxyOn) file = "TrayGreen.ico";
            else file = "TrayDefault.ico";

            if (file == _lastTrayIconFile) return;

            var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
            if (!File.Exists(path)) return;

            var newIcon = new Icon(path);
            var old = _notifyIcon.Icon;
            _notifyIcon.Icon = newIcon;
            old?.Dispose();
            _lastTrayIconFile = file;

            // Windows 托盘常缓存旧图标不立即重绘：清空再重设 Visible 强制刷新。
            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
        }
        catch
        {
        }
    }

    private void ToggleSystemProxy()
    {
        var host = AppHost.Host;
        try
        {
            if (host.SystemProxy.IsEnabled())
            {
                host.SystemProxy.Disable();
            }
            else
            {
                var port = host.Settings.GetIntAsync("core.mixedPort", 7897).GetAwaiter().GetResult();
                host.SystemProxy.Enable($"127.0.0.1:{port}", "localhost;127.*;10.*;172.16.*;192.168.*");
            }
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

    private void ApplyMenuTheme(Forms.ContextMenuStrip menu)
    {
        var dark = IsAppDark();
        menu.Renderer = dark
            ? new Forms.ToolStripProfessionalRenderer(new DarkColorTable())
            : new Forms.ToolStripProfessionalRenderer();
        var textColor = dark ? Color.White : Color.Black;
        menu.ForeColor = textColor;
        foreach (Forms.ToolStripItem item in menu.Items)
            item.ForeColor = textColor;
    }

    /// <summary>
    /// 判断当前实际生效的主题是否为深色。
    /// 运行时切换主题只能改根元素 RequestedTheme（Application.RequestedTheme 不可变），
    /// 因此优先读应用保存的主题设置，其次看根元素，最后回退系统主题。
    /// </summary>
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

    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x2B, 0x2B, 0x2B);
        private static readonly Color HoverBg = Color.FromArgb(0x41, 0x41, 0x41);
        private static readonly Color HoverBorder = Color.FromArgb(0x55, 0x55, 0x55);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ToolStripGradientBegin => Bg;
        public override Color ToolStripGradientMiddle => Bg;
        public override Color ToolStripGradientEnd => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => HoverBorder;
        public override Color MenuItemBorder => HoverBorder;
        public override Color MenuItemSelected => HoverBg;
        public override Color MenuItemSelectedGradientBegin => HoverBg;
        public override Color MenuItemSelectedGradientEnd => HoverBg;
        public override Color MenuItemPressedGradientBegin => HoverBg;
        public override Color MenuItemPressedGradientMiddle => HoverBg;
        public override Color MenuItemPressedGradientEnd => HoverBg;
        public override Color SeparatorDark => HoverBorder;
        public override Color SeparatorLight => Bg;
        public override Color ButtonSelectedHighlight => HoverBg;
        public override Color ButtonSelectedHighlightBorder => HoverBorder;
        public override Color ButtonSelectedBorder => HoverBorder;
        public override Color ToolStripBorder => HoverBorder;
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
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _miniWindow?.ForceClose();
        _miniWindow = null;
    }

    private void MinimizeToTray()
    {
        AppWindow.Hide();
    }

    /// <summary>隐藏主窗口到托盘（用于静默启动）。</summary>
    public void HideToTray() => MinimizeToTray();

    private void OnTrayLeftClick()
    {
        if (_trayMenuWasOpened)
        {
            _trayMenuWasOpened = false;
            return;
        }
        ToggleMiniWindow();
    }

    private void ToggleMiniWindow()
    {
        _miniWindow ??= new MiniWindow();
        _miniWindow.ToggleVisible();
    }

    public void ShowMiniPanel()
    {
        _miniWindow ??= new MiniWindow();
        _miniWindow.Show();
    }

    public void ApplyMiniPanelTheme(string theme) => _miniWindow?.ApplyTheme(theme);

    public void ShowAndActivate()
    {
        AppWindow.Show();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void ExitFromTray()
    {
        _forceExit = true;
        _miniWindow?.ForceClose();
        _miniWindow = null;
        _notifyIcon?.Dispose();
        _notifyIcon = null;

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
