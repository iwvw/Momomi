using System.Drawing;
using System.Drawing.Drawing2D;
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
    private Forms.ToolStripMenuItem? _startCoreItem;
    private Forms.ToolStripMenuItem? _stopCoreItem;
    private Forms.ToolStripMenuItem? _restartCoreItem;
    private Forms.ToolStripMenuItem? _modeRuleItem;
    private Forms.ToolStripMenuItem? _modeGlobalItem;
    private Forms.ToolStripMenuItem? _modeDirectItem;
    private Forms.ToolStripMenuItem? _systemProxyItem;
    private Forms.ToolStripMenuItem? _tunItem;
    private Forms.ToolStripMenuItem? _miniPanelItem;
    private MiniWindow? _miniWindow;
    private bool _forceExit;
    private bool _trayMenuWasOpened;
    private bool _modeBarLoading;
    private double _paneWidth = 200;
    private double _dragStartWidth;
    private Microsoft.UI.Xaml.DispatcherTimer? _sizeSaveTimer;

    private const double MinPaneWidth = 160;
    private const double MaxPaneWidth = 420;

    /// <summary>状态点标记：_coreStatusItem 用它标识，渲染器据此避免禁用灰化绘制。</summary>
    private const int StatusDotTag = -1;

    public ModeSelectorViewModel ModeSelector { get; }

    public Services.HotkeyManager Hotkeys { get; }

    public MainWindow()
    {
        ModeSelector = new ModeSelectorViewModel(AppHost.Host, DispatcherQueue);
        Hotkeys = new Services.HotkeyManager(AppHost.Host, this);

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

        _ = Hotkeys.LoadAsync();
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
                SystemBackdrop = new Controls.AlwaysActiveAcrylicBackdrop();
            else if (MicaController.IsSupported())
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        catch
        {
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
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    break;
                case 2:
                    SystemBackdrop = null;
                    break;
                default:
                    if (DesktopAcrylicController.IsSupported())
                        SystemBackdrop = new Controls.AlwaysActiveAcrylicBackdrop();
                    else if (MicaController.IsSupported())
                        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    else
                        SystemBackdrop = null;
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

            var menu = new Forms.ContextMenuStrip
            {
                ImageScalingSize = new Size(16, 16),
                ShowImageMargin = true,
            };
            menu.Opening += (_, _) =>
            {
                _trayMenuWasOpened = true;
                ApplyMenuTheme(menu);
                UpdateTrayState();
            };
            menu.Opened += (_, _) => TryRoundMenuWindow(menu);

            var (darkIcon, lightIcon) = GlyphColors();

            _coreStatusItem = new Forms.ToolStripMenuItem("内核：未知") { Enabled = false, Tag = StatusDotTag };
            menu.Items.Add(_coreStatusItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add(MakeIconItem("打开主界面", 0xE80F, darkIcon, () => Dispatch(ShowAndActivate)));
            _miniPanelItem = MakeIconItem("显示迷你面板", 0xE90B, darkIcon, () => Dispatch(ToggleMiniWindow));
            menu.Items.Add(_miniPanelItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            var coreMenu = MakeIconItem("内核", 0xE768, darkIcon, null);
            _startCoreItem = MakeIconItem("启动内核", 0xE768, darkIcon, () => Dispatch(async () => await AppHost.Host.Core.StartAsync()));
            _stopCoreItem = MakeIconItem("停止内核", 0xE71A, darkIcon, () => Dispatch(async () => await AppHost.Host.Core.StopAsync()));
            _restartCoreItem = MakeIconItem("重启内核", 0xE72C, darkIcon, () => Dispatch(async () => await AppHost.Host.Core.RestartAsync()));
            coreMenu.DropDownItems.AddRange([_startCoreItem, _stopCoreItem, _restartCoreItem]);
            menu.Items.Add(coreMenu);

            var modeMenu = MakeIconItem("代理模式", 0xE7C8, darkIcon, null);
            _modeRuleItem = MakeIconItem("规则模式", 0xE8A5, darkIcon, () => Dispatch(async () => await HotkeySelectModeAsync(0)));
            _modeGlobalItem = MakeIconItem("全局模式", 0xE774, darkIcon, () => Dispatch(async () => await HotkeySelectModeAsync(1)));
            _modeDirectItem = MakeIconItem("直连模式", 0xE711, darkIcon, () => Dispatch(async () => await HotkeySelectModeAsync(2)));
            foreach (var item in new[] { _modeRuleItem, _modeGlobalItem, _modeDirectItem })
                item.CheckOnClick = false;
            modeMenu.DropDownItems.AddRange([_modeRuleItem, _modeGlobalItem, _modeDirectItem]);
            menu.Items.Add(modeMenu);

            menu.Items.Add(new Forms.ToolStripSeparator());

            _systemProxyItem = MakeIconItem("系统代理", 0xE774, darkIcon, () => Dispatch(ToggleSystemProxy));
            _systemProxyItem.CheckOnClick = false;
            menu.Items.Add(_systemProxyItem);

            _tunItem = MakeIconItem("TUN 模式", 0xE968, darkIcon, () => Dispatch(ToggleTun));
            _tunItem.CheckOnClick = false;
            menu.Items.Add(_tunItem);

            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(MakeIconItem("退出", 0xE7E8, darkIcon, () => Dispatch(ExitFromTray)));

            _notifyIcon.ContextMenuStrip = menu;
            UpdateTrayState();
        }
        catch
        {
            _notifyIcon = null;
        }
    }

    /// <summary>创建带图标（按 glyph code 记录到 Tag，供主题切换时重建）的菜单项。</summary>
    private static Forms.ToolStripMenuItem MakeIconItem(string text, int code, Color color, Action? onClick)
        => new(text, IconGlyph(code, color), onClick is null ? null : (_, _) => onClick()) { Tag = code };

    /// <summary>按当前主题返回深色/浅色图标颜色。</summary>
    private static (Color Dark, Color Light) GlyphColors()
        => (Color.FromArgb(0xEC, 0xEC, 0xEC), Color.FromArgb(0x2B, 0x2B, 0x2B));

    /// <summary>用 Segoe Fluent Icons（回退 Segoe MDL2 Assets）把 glyph 渲染成 20x20 位图。</summary>
    private static Bitmap? IconGlyph(int code, Color color)
    {
        try
        {
            var glyph = Convert.ToChar(code);
            var family = "Segoe Fluent Icons";
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            if (!installed.Families.Any(f => f.Name == "Segoe Fluent Icons"))
                family = "Segoe MDL2 Assets";

            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font(family, 14f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            var rect = new RectangleF(0, 0, 16, 16);
            g.DrawString(glyph.ToString(), font, brush, rect, sf);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>渲染一个 16x16 的纯色圆点，用于表示内核运行状态。</summary>
    private static Bitmap? StatusDot(Color color)
    {
        try
        {
            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 8, 8);
            return bmp;
        }
        catch
        {
            return null;
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
            var dark = IsAppDark();
            var running = core.State == CoreState.Running;
            if (_coreStatusItem is not null)
            {
                var statusText = core.State switch
                {
                    CoreState.Running => $"内核：运行中 {core.Version}",
                    CoreState.Starting => "内核：启动中",
                    CoreState.Stopping => "内核：停止中",
                    CoreState.Error => "内核：异常",
                    _ => "内核：已停止",
                };
                _coreStatusItem.Text = statusText;
                var dotColor = core.State switch
                {
                    CoreState.Running => Color.FromArgb(0x3C, 0xB0, 0x4A),       // 绿：运行中
                    CoreState.Starting => Color.FromArgb(0xEA, 0xA5, 0x3B),      // 黄：启动中
                    CoreState.Stopping => Color.FromArgb(0xF0, 0x7C, 0x00),      // 橙：停止中
                    CoreState.Error => Color.FromArgb(0xE5, 0x48, 0x4D),         // 红：异常
                    _ => Color.FromArgb(0x9E, 0x9E, 0x9E),                        // 灰：已停止
                };
                var prev = _coreStatusItem.Image;
                _coreStatusItem.Image = StatusDot(dotColor);
                prev?.Dispose();
            }

            if (_startCoreItem is not null)
                _startCoreItem.Enabled = !running && core.State is not (CoreState.Starting or CoreState.Stopping);
            if (_stopCoreItem is not null)
                _stopCoreItem.Enabled = running || core.State is CoreState.Starting or CoreState.Stopping;
            if (_restartCoreItem is not null)
                _restartCoreItem.Enabled = running || core.State is CoreState.Starting or CoreState.Stopping;

            if (_modeRuleItem is not null)
                _modeRuleItem.Checked = ModeSelector.Mode == "rule";
            if (_modeGlobalItem is not null)
                _modeGlobalItem.Checked = ModeSelector.Mode == "global";
            if (_modeDirectItem is not null)
                _modeDirectItem.Checked = ModeSelector.Mode == "direct";

            if (_miniPanelItem is not null)
                _miniPanelItem.Text = _miniWindow?.IsVisible == true ? "隐藏迷你面板" : "显示迷你面板";

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

    private void ApplyMenuTheme(Forms.ContextMenuStrip menu)
    {
        var dark = IsAppDark();
        menu.Renderer = dark
            ? new FluentMenuRenderer(true, new DarkColorTable())
            : new FluentMenuRenderer(false, new Forms.ProfessionalColorTable());
        var textColor = dark ? Color.White : Color.Black;
        var iconColor = dark ? Color.FromArgb(0xEC, 0xEC, 0xEC) : Color.FromArgb(0x2B, 0x2B, 0x2B);
        ApplyItemsTheme(menu.Items, textColor, iconColor);
    }

    /// <summary>递归设置菜单项前景色、垂直间距并重建图标颜色；Tag 为 int 时按 glyph code 重绘。</summary>
    private static void ApplyItemsTheme(Forms.ToolStripItemCollection items, Color textColor, Color iconColor)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            item.ForeColor = textColor;
            if (item is not Forms.ToolStripMenuItem mi) continue;
            mi.Margin = new Forms.Padding(0, 2, 0, 2);
            if (mi.Tag is int code && code > 0)
            {
                var old = mi.Image;
                mi.Image = IconGlyph(code, iconColor);
                old?.Dispose();
            }
            if (mi.HasDropDownItems)
                ApplyItemsTheme(mi.DropDownItems, textColor, iconColor);
        }
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

    /// <summary>
    /// 自定义菜单渲染器：展开箭头颜色跟随深浅主题，菜单项 hover 背景改为圆角。
    /// 系统 ToolStripProfessionalRenderer 的箭头使用固定系统色，深色主题下不清晰。
    /// </summary>
    private sealed class FluentMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private readonly bool _dark;
        private readonly Color _hoverBg;
        private readonly Color _pressedBg;
        private readonly Color _arrowColor;

        public FluentMenuRenderer(bool dark, Forms.ProfessionalColorTable table)
            : base(table)
        {
            _dark = dark;
            _hoverBg = dark ? Color.FromArgb(0x41, 0x41, 0x41) : Color.FromArgb(0xE5, 0xE5, 0xE5);
            _pressedBg = dark ? Color.FromArgb(0x35, 0x35, 0x35) : Color.FromArgb(0xCC, 0xCC, 0xCC);
            _arrowColor = dark ? Color.FromArgb(0xEC, 0xEC, 0xEC) : Color.FromArgb(0x2B, 0x2B, 0x2B);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == true ? _arrowColor : Color.FromArgb(0x80, _arrowColor);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemImage(Forms.ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item?.Tag is int tag && tag == StatusDotTag && e.Image is not null)
            {
                e.Graphics.DrawImage(e.Image, e.ImageRectangle);
                return;
            }
            base.OnRenderItemImage(e);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;

            var bg = e.Item.Pressed ? _pressedBg : _hoverBg;
            e.Graphics.FillRectangle(new SolidBrush(bg), new Rectangle(Point.Empty, e.Item.Size));
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>给原生菜单窗口设置 DWM 圆角（Win11 生效，Win10 忽略）。</summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>主菜单打开后设置圆角，并为子菜单递归设置。仅执行一次防重复。</summary>
    private void TryRoundMenuWindow(Forms.ContextMenuStrip menu)
    {
        try
        {
            RoundWindow(menu.Handle);
            HookDropDownRounding(menu.Items);
        }
        catch
        {
        }
    }

    private static void HookDropDownRounding(Forms.ToolStripItemCollection items)
    {
        foreach (Forms.ToolStripItem item in items)
        {
            if (item is not Forms.ToolStripMenuItem mi || !mi.HasDropDownItems) continue;
            mi.DropDownOpening += (_, _) =>
            {
                try
                {
                    if (mi.DropDown is not null) RoundWindow(mi.DropDown.Handle);
                    HookDropDownRounding(mi.DropDown?.Items ?? mi.DropDownItems);
                }
                catch
                {
                }
            };
        }
    }

    private static void RoundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
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

    public void ToggleMiniWindow()
    {
        EnsureMiniWindow();
        _miniWindow!.ToggleVisible();
    }

    public void ShowMiniPanel()
    {
        EnsureMiniWindow();
        _miniWindow!.Show();
    }

    /// <summary>惰性创建迷你窗口并订阅关闭通知；拖拽收起关闭后会置空引用，下次调用重建。</summary>
    private void EnsureMiniWindow()
    {
        if (_miniWindow is not null) return;
        var mini = new MiniWindow();
        mini.Dismissed += (_, _) => _miniWindow = null;
        _miniWindow = mini;
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
