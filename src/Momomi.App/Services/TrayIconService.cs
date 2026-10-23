using System.Runtime.InteropServices;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Momomi.App.Services;

/// <summary>
/// 托盘图标与右键菜单控制器。基于 H.NotifyIcon.WinUI 的 TaskbarIcon，
/// 右键菜单走 ContextMenuMode.SecondWindow（独立 WinUI 窗口里的原生 MenuFlyout，Fluent 观感）。
/// 菜单项在 TrayMenu.xaml 声明，这里按名称取引用、挂事件、按状态同步。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _icon;
    private ElementTheme _theme = ElementTheme.Default;

    // 菜单项引用（在 TrayMenu.xaml 中命名）。
    private MenuFlyoutItem? _statusItem;
    private MenuFlyoutItem? _openItem;
    private MenuFlyoutItem? _miniItem;
    private MenuFlyoutItem? _startCoreItem;
    private MenuFlyoutItem? _stopCoreItem;
    private MenuFlyoutItem? _restartCoreItem;
    private ToggleMenuFlyoutItem? _modeRuleItem;
    private ToggleMenuFlyoutItem? _modeGlobalItem;
    private ToggleMenuFlyoutItem? _modeDirectItem;
    private ToggleMenuFlyoutItem? _systemProxyItem;
    private ToggleMenuFlyoutItem? _tunItem;
    private MenuFlyoutItem? _exitItem;

    /// <summary>左键单击（按需求呼出主界面或迷你面板由宿主决定）。</summary>
    public event Action? LeftClicked;
    public event Action? OpenRequested;
    public event Action? ToggleMiniRequested;
    public event Action? StartCoreRequested;
    public event Action? StopCoreRequested;
    public event Action? RestartCoreRequested;
    public event Action<int>? ModeRequested;
    public event Action? ToggleSystemProxyRequested;
    public event Action? ToggleTunRequested;
    public event Action? ExitRequested;

    private string _lastIconFile = "TrayDefault.ico";

    // ---- 任务栏重建看门狗 ----
    //
    // explorer 重启后托盘图标会消失。H.NotifyIcon 自身靠 TaskbarCreated 广播来重建，
    // 但那条路径有两个不确定因素：
    //   1) 主程序是 requireAdministrator（High 完整性），explorer 是中完整性，
    //      UIPI 对低→高的自定义注册消息是否放行没有保证（本机无法构造 Medium 进程验证，
    //      因为内置 Administrator + ConsentPromptBehaviorAdmin=0 下所有进程都是 High）；
    //   2) 即便消息送达，也要依赖 H.NotifyIcon 内部状态机正确重建。
    // 所以这里再加一道与消息无关的兜底：启动时按 Shell_TrayWnd 的宿主 PID 记下任务栏身份，
    // 之后定时比对，发现 explorer/任务栏换了就主动重建图标。
    // 只在进程真的换了才重建，平时零开销、也不会造成图标闪烁。
    private Timer? _taskbarWatchdog;
    private uint _trayOwnerPid;
    private Microsoft.UI.Dispatching.DispatcherQueue? _ownerDispatcherQueue;
    private TrayState? _lastState;

    public void Show(bool visible)
    {
        if (_icon is null)
        {
            if (!visible) return;
            _icon = CreateIcon();
        }
        _icon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _theme = theme;
        if (_icon is not null)
        {
            _icon.RequestedTheme = theme;
            // 显式给菜单窗口指定深浅色：System 模式依赖 TaskbarIcon.ActualTheme，
            // 而 TaskbarIcon 不在可视树里，ActualTheme 不跟随主题，故按应用主题直接映射。
            _icon.ContextMenuThemeMode = ToMenuThemeMode(theme);
        }
    }

    private static PopupMenuThemeMode ToMenuThemeMode(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => PopupMenuThemeMode.Dark,
        ElementTheme.Light => PopupMenuThemeMode.Light,
        _ => PopupMenuThemeMode.System,
    };

    private TaskbarIcon CreateIcon()
    {
        var icon = (TaskbarIcon)Application.Current.Resources["MomomiTrayIcon"];
        // 记住创建线程的 DispatcherQueue：看门狗在线程池回调，重建图标必须回到 UI 线程。
        _ownerDispatcherQueue = icon.DispatcherQueue;
        icon.RequestedTheme = _theme;
        icon.ContextMenuMode = ContextMenuMode.SecondWindow;
        // 菜单主题按应用主题显式映射（TaskbarIcon 不在可视树，ActualTheme 不可靠）。
        icon.ContextMenuThemeMode = ToMenuThemeMode(_theme);
        icon.LeftClickCommand = new SimpleCommand(() => LeftClicked?.Invoke());
        icon.NoLeftClickDelay = true;

        if (icon.ContextFlyout is MenuFlyout menu)
        {
            _statusItem = FindItem<MenuFlyoutItem>(menu, "TrayStatusItem");
            _openItem = FindItem<MenuFlyoutItem>(menu, "TrayOpenItem");
            _miniItem = FindItem<MenuFlyoutItem>(menu, "TrayMiniItem");
            _startCoreItem = FindItem<MenuFlyoutItem>(menu, "TrayStartCoreItem");
            _stopCoreItem = FindItem<MenuFlyoutItem>(menu, "TrayStopCoreItem");
            _restartCoreItem = FindItem<MenuFlyoutItem>(menu, "TrayRestartCoreItem");
            _modeRuleItem = FindItem<ToggleMenuFlyoutItem>(menu, "TrayModeRuleItem");
            _modeGlobalItem = FindItem<ToggleMenuFlyoutItem>(menu, "TrayModeGlobalItem");
            _modeDirectItem = FindItem<ToggleMenuFlyoutItem>(menu, "TrayModeDirectItem");
            _systemProxyItem = FindItem<ToggleMenuFlyoutItem>(menu, "TraySystemProxyItem");
            _tunItem = FindItem<ToggleMenuFlyoutItem>(menu, "TrayTunItem");
            _exitItem = FindItem<MenuFlyoutItem>(menu, "TrayExitItem");

            if (_openItem is not null) _openItem.Click += (_, _) => OpenRequested?.Invoke();
            if (_miniItem is not null) _miniItem.Click += (_, _) => ToggleMiniRequested?.Invoke();
            if (_startCoreItem is not null) _startCoreItem.Click += (_, _) => StartCoreRequested?.Invoke();
            if (_stopCoreItem is not null) _stopCoreItem.Click += (_, _) => StopCoreRequested?.Invoke();
            if (_restartCoreItem is not null) _restartCoreItem.Click += (_, _) => RestartCoreRequested?.Invoke();
            if (_modeRuleItem is not null) _modeRuleItem.Click += (_, _) => ModeRequested?.Invoke(0);
            if (_modeGlobalItem is not null) _modeGlobalItem.Click += (_, _) => ModeRequested?.Invoke(1);
            if (_modeDirectItem is not null) _modeDirectItem.Click += (_, _) => ModeRequested?.Invoke(2);
            if (_systemProxyItem is not null) _systemProxyItem.Click += (_, _) => ToggleSystemProxyRequested?.Invoke();
            if (_tunItem is not null) _tunItem.Click += (_, _) => ToggleTunRequested?.Invoke();
            if (_exitItem is not null) _exitItem.Click += (_, _) => ExitRequested?.Invoke();
        }

        icon.ForceCreate(enablesEfficiencyMode: false);
        AllowTaskbarCreatedMessage(icon);
        StartTaskbarWatchdog(icon);
        return icon;
    }

    /// <summary>
    /// 定时检查任务栏（Shell_TrayWnd）的宿主进程是否还活着、是否变了。
    /// explorer 重启后句柄和 PID 都会变，此时重建图标——不依赖 TaskbarCreated 能否送达。
    /// </summary>
    private void StartTaskbarWatchdog(TaskbarIcon icon)
    {
        _trayOwnerPid = GetShellTrayOwnerPid();

        _taskbarWatchdog?.Dispose();
        _taskbarWatchdog = new Timer(_ =>
        {
            try
            {
                var pid = GetShellTrayOwnerPid();
                // 0 = 任务栏暂时不可用（explorer 正在重启），不算变化，等下一轮。
                if (pid == 0) return;
                if (pid == _trayOwnerPid) return;

                _trayOwnerPid = pid;

                // Timer 在线程池：WinUI 对象只能在创建它的线程上操作。
                var queue = _ownerDispatcherQueue;
                if (queue is null) return;
                queue.TryEnqueue(() => Recreate(icon));
            }
            catch
            {
            }
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    /// <summary>重建托盘图标（等价于 H.NotifyIcon 收到 TaskbarCreated 时的处理）。</summary>
    private void Recreate(TaskbarIcon icon)
    {
        try
        {
            if (!ReferenceEquals(icon, _icon)) return;
            if (icon.TrayIcon.IsCreated) _ = icon.TrayIcon.TryRemove();
            icon.ForceCreate(enablesEfficiencyMode: false);
            ApplyStateAfterRecreate();
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"重建托盘图标失败：{ex}");
        }
    }

    /// <summary>重建后把图标、ToolTip、菜单状态按当前快照再刷一遍。</summary>
    private void ApplyStateAfterRecreate()
    {
        // 新图标的 IconSource 是空的；清掉缓存标记才能让 UpdateState → UpdateIcon 真正写一次。
        _lastIconFile = "";
        _icon!.Visibility = Visibility.Visible;
        if (_lastState is not null) UpdateState(_lastState);
    }

    /// <summary>取 Shell_TrayWnd 的宿主进程 PID；任务栏不存在（explorer 重启中）返回 0。</summary>
    private static uint GetShellTrayOwnerPid()
    {
        try
        {
            var hwnd = FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero) return 0;
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 放行 explorer 广播的 TaskbarCreated，让 H.NotifyIcon 自身的重建逻辑也能生效。
    ///
    /// 主程序是 requireAdministrator（见 app.manifest），进程处于 High 完整性级别，
    /// explorer 是中完整性。UIPI 默认拦截"低完整性 → 高完整性"的窗口消息，
    /// TaskbarCreated 是 RegisterWindowMessage 注册的自定义消息、不在放行白名单里，
    /// 因此不能指望它一定能送达（实测本机无法构造 Medium 进程验证，见看门狗处的说明）。
    ///
    /// H.NotifyIcon 2.5.0-beta.3 内部没有调用 ChangeWindowMessageFilterEx，
    /// 这里显式放行，与下面的 PID 看门狗互为兜底：任一条生效图标都能恢复。
    /// </summary>
    private static void AllowTaskbarCreatedMessage(TaskbarIcon icon)
    {
        try
        {
            var hwnd = icon.TrayIcon.MessageWindow.Handle;
            if (hwnd == IntPtr.Zero) return;

            var message = RegisterWindowMessage("TaskbarCreated");
            if (message == 0) return;

            if (!ChangeWindowMessageFilterEx(hwnd, message, MSGFLT_ALLOW, IntPtr.Zero))
            {
                // 老系统（Win7 之前）没有 Ex 版本；降级为全局放行，仅影响本进程。
                _ = ChangeWindowMessageFilter(message, MSGFLT_ALLOW);
            }
        }
        catch
        {
            // 放行失败只是回到旧行为（explorer 重启后图标消失），不能影响托盘创建。
        }
    }

    private const uint MSGFLT_ALLOW = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>按 x:Name 在菜单里查找项（含子菜单递归）。</summary>
    private static T? FindItem<T>(MenuFlyout menu, string name) where T : MenuFlyoutItemBase
    {
        foreach (var item in menu.Items)
        {
            if (item is T t && (item as FrameworkElement)?.Name == name) return t;
            if (item is MenuFlyoutSubItem sub)
            {
                var found = FindItem<T>(sub, name);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static T? FindItem<T>(MenuFlyoutSubItem menu, string name) where T : MenuFlyoutItemBase
    {
        foreach (var item in menu.Items)
        {
            if (item is T t && (item as FrameworkElement)?.Name == name) return t;
            if (item is MenuFlyoutSubItem sub)
            {
                var found = FindItem<T>(sub, name);
                if (found is not null) return found;
            }
        }
        return null;
    }

    /// <summary>
    /// 弹一条托盘气泡通知。
    ///
    /// 用 realtime=false + respectQuietTime=true：用户开着「专注助手」时不打扰。
    /// 托盘还没创建（启动早期）时直接跳过——没有图标就没有宿主窗口，硬调会抛。
    /// </summary>
    public void ShowNotification(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(
                title,
                message,
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: false,
                sound: true,
                respectQuietTime: true,
                realtime: false,
                timeout: null);
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"托盘通知失败：{ex}");
        }
    }

    /// <summary>同步菜单与图标的运行状态。</summary>
    public void UpdateState(TrayState state)
    {
        if (_icon is null) return;

        // 记住最新快照：任务栏重建后要按这份状态把图标/ToolTip/菜单再刷一遍。
        _lastState = state;

        if (_statusItem is not null)
        {
            _statusItem.Text = state.StatusText;
            if (_statusItem.Icon is FontIcon fi)
            {
                fi.Glyph = state.StatusGlyph;
                fi.Foreground = state.StatusBrush;
            }
        }

        if (_startCoreItem is not null) _startCoreItem.IsEnabled = state.CanStart;
        if (_stopCoreItem is not null) _stopCoreItem.IsEnabled = state.CanStop;
        if (_restartCoreItem is not null) _restartCoreItem.IsEnabled = state.CanStop;

        if (_modeRuleItem is not null) _modeRuleItem.IsChecked = state.Mode == "rule";
        if (_modeGlobalItem is not null) _modeGlobalItem.IsChecked = state.Mode == "global";
        if (_modeDirectItem is not null) _modeDirectItem.IsChecked = state.Mode == "direct";

        if (_miniItem is not null) _miniItem.Text = state.MiniVisible ? "隐藏迷你面板" : "显示迷你面板";
        if (_systemProxyItem is not null) _systemProxyItem.IsChecked = state.SystemProxyOn;
        if (_tunItem is not null) _tunItem.IsChecked = state.TunOn;

        _icon.ToolTipText = state.Tooltip;

        UpdateIcon(state.IconFile);
    }

    /// <summary>切换托盘图标（磁贴样式：默认/绿/红）。</summary>
    private void UpdateIcon(string file)
    {
        if (_icon is null || file == _lastIconFile) return;
        try
        {
            _icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri($"ms-appx:///Assets/{file}"));
            _lastIconFile = file;
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _taskbarWatchdog?.Dispose();
        _taskbarWatchdog = null;
        if (_icon is null) return;
        _icon.Visibility = Visibility.Collapsed;
        _icon.Dispose();
        _icon = null;
    }

    private sealed class SimpleCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}

/// <summary>托盘状态快照，由宿主组装后交给 TrayIconService 同步。</summary>
internal sealed record TrayState(
    string StatusText,
    string StatusGlyph,
    Microsoft.UI.Xaml.Media.Brush StatusBrush,
    string Tooltip,
    string IconFile,
    bool CanStart,
    bool CanStop,
    string Mode,
    bool MiniVisible,
    bool SystemProxyOn,
    bool TunOn);
