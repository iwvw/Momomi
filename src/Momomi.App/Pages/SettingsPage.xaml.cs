using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private bool _syncing;
    private bool _attached;

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(global::Momomi.App.AppHost.Host);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Attach();
        await ReloadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Detach();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        // 其它界面（标题栏、迷你面板）改动开关或内核状态时，设置页同步刷新。
        AppSignals.SwitchesChanged += OnExternalChange;
        AppHost.Host.Core.StateChanged += OnCoreStateChanged;
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        AppSignals.SwitchesChanged -= OnExternalChange;
        AppHost.Host.Core.StateChanged -= OnCoreStateChanged;
    }

    // 事件可能从后台线程触发：必须调度回 UI 线程再 Reload，
    // 否则 LoadAsync 里的属性赋值会让 x:Bind 跨界更新控件抛 COMException。
    private void OnExternalChange(object? sender, EventArgs e) => DispatchReload();

    private void OnCoreStateChanged(object? sender, Momomi.Core.Services.CoreStateChanged e) => DispatchReload();

    private void DispatchReload()
    {
        if (DispatcherQueue.HasThreadAccess)
            _ = ReloadAsync();
        else
            DispatcherQueue.TryEnqueue(() => _ = ReloadAsync());
    }

    private async Task ReloadAsync()
    {
        await ViewModel.LoadAsync();

        _syncing = true;
        try
        {
            ThemeBox.SelectedIndex = ViewModel.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
            LogLevelBox.SelectedIndex = ViewModel.LogLevel switch
            {
                "silent" => 0,
                "error" => 1,
                "warning" => 2,
                "debug" => 4,
                _ => 3,
            };
            SystemProxyModeBox.SelectedIndex = ViewModel.SystemProxyMode switch
            {
                "pac" => 1,
                _ => 0,
            };
            TunStackBox.SelectedIndex = ViewModel.TunStack switch
            {
                "gvisor" => 1,
                "system" => 2,
                _ => 0,
            };
            var proxyIndex = ViewModel.GithubProxyIndex;
            GithubProxyBox.SelectedIndex = proxyIndex >= 0 && proxyIndex < SettingsViewModel.GithubProxyBuiltins.Length
                ? proxyIndex
                : SettingsViewModel.GithubProxyBuiltins.Length;
            GithubProxyCustomBox.Visibility = GithubProxyBox.SelectedIndex == SettingsViewModel.GithubProxyBuiltins.Length
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _syncing = false;
        }

        // 内核版本列表走网络（可能很慢），后台加载，不阻塞上面的下拉框初始化。
        if (!ViewModel.LoadKernelVersionsCommand.IsRunning)
            _ = ViewModel.LoadKernelVersionsCommand.ExecuteAsync(null);
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.Theme = ThemeBox.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "default",
        };
    }

    private void LogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.LogLevel = LogLevelBox.SelectedIndex switch
        {
            0 => "silent",
            1 => "error",
            2 => "warning",
            4 => "debug",
            _ => "info",
        };
    }

    private void SystemProxyMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.SystemProxyMode = SystemProxyModeBox.SelectedIndex switch
        {
            1 => "pac",
            _ => "manual",
        };
    }

    private void TunStack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.TunStack = TunStackBox.SelectedIndex switch
        {
            1 => "gvisor",
            2 => "system",
            _ => "mixed",
        };
    }

    private void GithubProxy_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        ViewModel.GithubProxyIndex = GithubProxyBox.SelectedIndex;
        GithubProxyCustomBox.Visibility = GithubProxyBox.SelectedIndex == SettingsViewModel.GithubProxyBuiltins.Length
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>文本框失焦时提交。</summary>
    private void TextField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.Tag is string field) CommitTextField(field, box.Text);
    }

    /// <summary>回车提交并把焦点移出输入框（触发失焦，给出明确的保存反馈）。</summary>
    private void TextField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        if (sender is not TextBox box) return;
        if (box.Tag is string field) CommitTextField(field, box.Text);
        e.Handled = true;
        // 把焦点交给根容器，结束编辑（WinUI Desktop 不能用无 SearchRoot 的 TryMoveFocus）。
        RootGrid.Focus(FocusState.Programmatic);
    }

    /// <summary>点击页面空白处时结束输入框编辑。</summary>
    private void Root_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox) return;
        // 点击源若在某个输入框内，保持其焦点，不要移走。
        if (IsWithinTextBox(e.OriginalSource as DependencyObject)) return;
        RootGrid.Focus(FocusState.Programmatic);
    }

    private static bool IsWithinTextBox(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBox) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>快捷键录入：在只读输入框内按下组合键即录制；Backspace 清空。</summary>
    private async void Hotkey_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string action) return;
        e.Handled = true;

        // 忽略单独的修饰键（等待主键）。
        if (e.Key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Menu
            or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.LeftWindows
            or Windows.System.VirtualKey.RightWindows)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Back)
        {
            await ClearHotkeyAsync(action, box);
            return;
        }

        var mods = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        var win = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows);

        var modifiers = new System.Text.StringBuilder();
        if ((mods & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down) modifiers.Append("Ctrl+");
        if ((alt & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down) modifiers.Append("Alt+");
        if ((shift & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down) modifiers.Append("Shift+");
        if ((win & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down) modifiers.Append("Win+");

        var text = modifiers + KeyName(e.Key);
        if (!Momomi.App.Services.HotkeyParser.TryParse(text, out _, out _))
        {
            ViewModel.StatusText = "快捷键无效：需至少一个修饰键 + 一个可用的主键";
            return;
        }

        await ApplyHotkeyAsync(action, text, box);
    }

    /// <summary>绑定快捷键；与应用内其它动作冲突时弹窗确认是否抢占。</summary>
    private async Task ApplyHotkeyAsync(string action, string text, TextBox? box)
    {
        var result = await ViewModel.TryBindHotkeyAsync(action, text);
        switch (result.Status)
        {
            case Momomi.App.Services.HotkeyApplyStatus.Ok:
                if (box is not null) box.Text = text;
                break;

            case Momomi.App.Services.HotkeyApplyStatus.InternalConflict when result.Conflict is not null:
                var conflictName = SettingsViewModel.HotkeyDisplayName(result.Conflict.Value);
                var dialog = new ContentDialog
                {
                    Title = "快捷键冲突",
                    Content = $"「{text}」已绑定给「{conflictName}」。\n是否移除对方的绑定并改绑到这里？",
                    PrimaryButtonText = "改绑",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    var ok = await ViewModel.ResolveHotkeyConflictAsync(action, text, result.Conflict.Value.ToString());
                    if (ok && box is not null) box.Text = text;
                }
                else
                {
                    // 取消：把输入框恢复为当前动作的实际绑定。
                    if (box is not null) box.Text = await GetCurrentHotkeyTextAsync(action);
                }
                break;

            case Momomi.App.Services.HotkeyApplyStatus.Occupied:
                ViewModel.StatusText = "快捷键注册失败：可能已被其他程序占用";
                if (box is not null) box.Text = await GetCurrentHotkeyTextAsync(action);
                break;

            case Momomi.App.Services.HotkeyApplyStatus.Invalid:
                ViewModel.StatusText = "快捷键无效：需至少一个修饰键 + 一个可用的主键";
                if (box is not null) box.Text = await GetCurrentHotkeyTextAsync(action);
                break;
        }
    }

    private async Task<string> GetCurrentHotkeyTextAsync(string action)
    {
        var host = global::Momomi.App.AppHost.Host;
        return await host.Settings.GetAsync($"hotkey.{action}") ?? "";
    }

    private async Task<bool> ClearHotkeyAsync(string action, TextBox box)
    {
        var result = await ViewModel.TryBindHotkeyAsync(action, "");
        if (result.Status is Momomi.App.Services.HotkeyApplyStatus.Cleared)
        {
            box.Text = "";
            return true;
        }
        return false;
    }

    /// <summary>把 VirtualKey 转成解析器认识的名称。</summary>
    private static string KeyName(Windows.System.VirtualKey key)
    {
        var v = (int)key;
        if (v is >= 0x41 and <= 0x5A) return ((char)v).ToString();   // A-Z
        if (v is >= 0x30 and <= 0x39) return ((char)v).ToString();   // 0-9
        if (v is >= 0x70 and <= 0x87) return $"F{v - 0x70 + 1}";      // F1-F24
        if (v is >= 0x60 and <= 0x69) return $"NumPad{v - 0x60}";     // 小键盘 0-9
        return key switch
        {
            Windows.System.VirtualKey.Space => "Space",
            Windows.System.VirtualKey.Tab => "Tab",
            Windows.System.VirtualKey.Enter => "Enter",
            Windows.System.VirtualKey.Escape => "Esc",
            Windows.System.VirtualKey.Back => "Backspace",
            Windows.System.VirtualKey.Delete => "Delete",
            Windows.System.VirtualKey.Insert => "Insert",
            Windows.System.VirtualKey.Home => "Home",
            Windows.System.VirtualKey.End => "End",
            Windows.System.VirtualKey.PageUp => "PageUp",
            Windows.System.VirtualKey.PageDown => "PageDown",
            Windows.System.VirtualKey.Up => "Up",
            Windows.System.VirtualKey.Down => "Down",
            Windows.System.VirtualKey.Left => "Left",
            Windows.System.VirtualKey.Right => "Right",
            Windows.System.VirtualKey.NumberKeyLock => "NumLock",
            _ => "",
        };
    }

    private void CommitTextField(string field, string text)
    {
        switch (field)
        {
            case "MixedPort": ViewModel.MixedPort = text; break;
            case "ControllerPort": ViewModel.ControllerPort = text; break;
            case "DelayTestUrl": ViewModel.DelayTestUrl = text; break;
            case "DelayTestTimeout": ViewModel.DelayTestTimeout = text; break;
            case "DelayTestConcurrency": ViewModel.DelayTestConcurrency = text; break;
            case "SubscriptionUserAgent": ViewModel.SubscriptionUserAgent = text; break;
            case "SubscriptionTimeout": ViewModel.SubscriptionTimeout = text; break;
            case "GithubProxyCustom": ViewModel.GithubProxyCustom = text; break;
            case "AutoQuitWithoutCoreDelay": ViewModel.AutoQuitWithoutCoreDelay = int.TryParse(text, out var d) ? d : ViewModel.AutoQuitWithoutCoreDelay; break;
            case "TunMtu": ViewModel.TunMtu = text; break;
            case "TunDnsHijack": ViewModel.TunDnsHijack = text; break;
            case "TunRouteExcludeAddress": ViewModel.TunRouteExcludeAddress = text; break;
            case "PauseSsids": ViewModel.PauseSsids = text; break;
            case "SsidProfileMap": ViewModel.SsidProfileMap = text; break;
        }
    }
}
