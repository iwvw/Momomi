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
        return icon;
    }

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

    /// <summary>同步菜单与图标的运行状态。</summary>
    public void UpdateState(TrayState state)
    {
        if (_icon is null) return;

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
