using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Controls;
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
        // 标题栏内容行保持 48px，但系统按钮（最小化/最大化/关闭）用标准高度（约 32px）顶部对齐，
        // 形成"半高"观感；Tall 会让按钮占满整行高度。
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 600;
        }

        // 默认窗口尺寸（用户调整后持久化，重启恢复；无记录时用较小的默认值）。
        RestoreWindowSize();
        HookUpdatePill();
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
            // 兜底：内容就绪之前被推迟的背景材质，在这里补一次。
            // （正常会被根元素 Loaded 先触发，这条只是防止 Content 分支没覆盖到。）
            RetryPendingBackdrop();

            // 用户回来了，刷新活跃时间戳，避免这时被回收工作集造成卡顿。
            WorkingSetTrimmer.Touch(ActivitySources.MainWindow);
        };

        // 任何指针活动都说明界面在动，刷新活跃时间戳。
        // 只挂一次在根元素上：XAML 的指针事件会从命中的子元素冒泡上来。
        if (Content is UIElement root)
        {
            root.AddHandler(
                UIElement.PointerMovedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler(
                    (_, _) => WorkingSetTrimmer.Touch(ActivitySources.MainWindow)),
                handledEventsToo: true);
        }

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

    // ---- 顶栏「可更新」胶囊 ----

    /// <summary>订阅更新状态广播，并按需显示顶栏胶囊。</summary>
    private void HookUpdatePill()
    {
        ViewModels.AppSignals.AppUpdateAvailableChanged += (_, notice) =>
            DispatcherQueue.TryEnqueue(() => ApplyUpdateNotice(notice));

        // 首次打开窗口时补一次当前状态（检查可能早于本窗口创建完成）。
        ApplyUpdateNotice(ViewModels.AppSignals.AppUpdateAvailable);
    }

    /// <summary>
    /// 后台检查到新版本时调用（由 UpdateCoordinator 触发）。
    /// 顶栏胶囊是常驻入口，托盘通知只是附带提示——用户可能正开在别的窗口上，
    /// 不给通知就完全不知道有新版本。
    /// </summary>
    public void NotifyUpdateAvailable(AppUpdateInfo info, bool notify)
    {
        if (!info.HasUpdate || string.IsNullOrEmpty(info.LatestVersion)) return;

        ViewModels.AppSignals.RaiseAppUpdateNotice(new ViewModels.AppUpdateNotice(
            info.LatestVersion, info.Notes, info.ReleaseUrl));

        if (!notify) return;
        NotifyTray(
            "Momomi 有新版本",
            $"v{info.LatestVersion} 已发布，点击顶栏胶囊即可更新。");
    }

    /// <summary>自动安装开始前的提示。用户可以借此知道程序为什么突然重启。</summary>
    public void NotifyAutoUpdateStarting(AppUpdateInfo info)
    {
        if (string.IsNullOrEmpty(info.LatestVersion)) return;

        _updateInFlight = true;
        try
        {
            UpdatePillText.Text = $"正在更新到 v{info.LatestVersion}";
            UpdatePill.Visibility = Visibility.Visible;
        }
        catch
        {
        }

        NotifyTray(
            "Momomi 正在更新",
            $"正在安装 v{info.LatestVersion}，完成后会自动重启。");
    }

    /// <summary>
    /// 自动安装失败：把胶囊从「正在更新」恢复成可点击状态。
    ///
    /// 不恢复的话 _updateInFlight 会一直为 true，ApplyUpdateNotice 之后
    /// 再也不会更新文案，胶囊会永远停在「正在更新到 vX」，用户点它也没反应。
    /// </summary>
    public void NotifyAutoUpdateFailed(AppUpdateInfo info)
    {
        _updateInFlight = false;
        RestoreUpdatePillText();

        try
        {
            UpdatePill.Visibility = string.IsNullOrEmpty(info.LatestVersion)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch
        {
        }
    }

    /// <summary>设置项读出「不显示通知」；读不到时按显示处理。</summary>
    private static bool GetSuppressUpdateNotify()
    {
        try
        {
            return !AppHost.Host.Settings
                .GetBoolAsync("ui.updateSuppressNotifications")
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return true;
        }
    }

    /// <summary>弹一条托盘气泡。托盘未创建（隐藏到托盘前）时静默跳过。</summary>
    private void NotifyTray(string title, string message)
    {
        try
        {
            _tray?.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"托盘更新通知失败：{ex}");
        }
    }

    private void ApplyUpdateNotice(ViewModels.AppUpdateNotice? notice)
    {
        try
        {
            if (notice is null)
            {
                UpdatePill.Visibility = Visibility.Collapsed;
                return;
            }

            // 下载进行中不要覆盖进度文案（此时广播可能因为复查再次触发）。
            if (!_updateInFlight) UpdatePillText.Text = $"v{notice.Version} 可更新";
            UpdatePill.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"更新胶囊显示失败：{ex}");
        }
    }

    /// <summary>下载中/重启中：防止重复点击。不用 IsEnabled——禁用态会让模板改掉胶囊配色。</summary>
    private bool _updateInFlight;

    private async void UpdatePill_Click(object sender, RoutedEventArgs e)
    {
        var notice = ViewModels.AppSignals.AppUpdateAvailable;
        if (notice is null) return;

        try
        {
            // 有更新说明就展示更新说明（截断，避免超长 release notes 撑爆对话框）。
            var notes = SummarizeNotes(notice.Notes);
            var content = $"当前版本 v{Momomi.Core.Services.AppUpdateService.GetCurrentVersion()}，"
                          + $"最新版本 v{notice.Version}。\n\n"
                          + (notes is null ? "" : notes + "\n\n")
                          + "更新会下载安装包并重启 Momomi，期间代理会短暂中断。是否现在更新？";

            var dialog = new ContentDialog
            {
                Title = "发现新版本",
                Content = new ScrollViewer
                {
                    MaxHeight = 360,
                    Content = new TextBlock
                    {
                        Text = content,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
                PrimaryButtonText = "立即更新",
                SecondaryButtonText = "跳过此版本",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            var result = await Pages.DialogGuard.ShowAsync(dialog);

            if (result == ContentDialogResult.Secondary)
            {
                await (global::Momomi.App.App.Updates?.SkipVersionAsync(notice.Version)
                       ?? Task.CompletedTask);
                // 收起胶囊：用户已明确表示不要再提示这个版本。
                ViewModels.AppSignals.RaiseAppUpdateNotice(null);
                return;
            }

            if (result != ContentDialogResult.Primary) return;

            await RunAppUpdateAsync();
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"一键更新失败：{ex}");
        }
    }

    /// <summary>
    /// 把 release notes 压缩成适合对话框显示的摘要。
    /// 原文是完整的 Markdown（含下载表格、安装说明），整段塞进对话框又长又乱，
    /// 这里只保留开头的变更条目，超出部分用省略号收尾。
    /// </summary>
    private static string? SummarizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;

        var lines = notes.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var chars = 0;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            // 遇到产物下载/安装说明就停：那部分对「要不要更新」这个决定没有帮助。
            if (line.StartsWith("## 产物下载", StringComparison.Ordinal)
                || line.StartsWith("### ", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal))
                break;

            if (line.Length == 0 && kept.Count == 0) continue;

            kept.Add(line);
            chars += line.Length;

            // 600 字足够说清这一版改了什么，再长用户也不会读。
            if (chars > 600)
            {
                kept.Add("…");
                break;
            }
        }

        var text = string.Join("\n", kept).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// 一键更新：下载 → 生成脚本 → 停内核并退出，由脚本接管安装与重启。
    /// 与设置页的实现同源，这里补一个下载进度提示（顶栏点击时用户会盯着看）。
    /// </summary>
    private async Task RunAppUpdateAsync()
    {
        if (_updateInFlight) return;
        _updateInFlight = true;

        var host = AppHost.Host;
        UpdatePillText.Text = "准备更新…";

        try
        {
            var progress = new Progress<double>(p =>
                UpdatePillText.Text = $"下载 {p * 100:0}%");

            var info = await host.AppUpdate.PrepareUpdateAsync(progress);
            if (info.Error is not null)
            {
                RestoreUpdatePillText();
                _updateInFlight = false;
                await ShowUpdateErrorAsync(info.Error);
                return;
            }

            UpdatePillText.Text = "正在重启…";
            ExitForUpdate();
        }
        catch (Exception ex)
        {
            UpdatePillText.Text = "更新失败";
            _updateInFlight = false;
            global::Momomi.App.App.WriteLog($"一键更新失败：{ex}");
            await ShowUpdateErrorAsync(ex.Message);
        }
    }

    /// <summary>把胶囊文案恢复成「vX.Y.Z 可更新」。</summary>
    private void RestoreUpdatePillText()
    {
        var notice = ViewModels.AppSignals.AppUpdateAvailable;
        if (notice is not null)
            UpdatePillText.Text = $"v{notice.Version} 可更新";
    }

    private async Task ShowUpdateErrorAsync(string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "更新失败",
                Content = message,
                CloseButtonText = "知道了",
                XamlRoot = Content.XamlRoot,
            };
            await Pages.DialogGuard.ShowAsync(dialog);
        }
        catch
        {
        }
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

    /// <summary>
    /// 构造函数期的默认背景材质。
    ///
    /// 已合并到 <see cref="ApplyBackdropStyle"/>：之前这里直接赋值一次、
    /// `InitializeBackdropStyleAsync` 又按设置赋值一次，同一个窗口会连续 attach
    /// 两次 backdrop target。现在统一走 ApplyBackdropStyle（默认样式即 0=亚克力）。
    /// </summary>
    public void ApplyBackdrop() => ApplyBackdropStyle(_appliedBackdropStyle < 0 ? 0 : _appliedBackdropStyle);

    /// <summary>当前已应用的背景材质样式（-1 表示还没设过）。</summary>
    private int _appliedBackdropStyle = -1;

    /// <summary>该样式是否已经真正赋值成功（false 表示因内容未就绪被推迟）。</summary>
    private bool _appliedBackdropStyleApplied;

    /// <summary>
    /// 替换系统背景材质。<b>返回是否真的完成了赋值</b>（false 表示被推迟）。
    ///
    /// 这里必须防住一个 WinUI 的硬崩溃：给 Window.SystemBackdrop 赋值时，框架内部会去
    /// 订阅 Content.ActualThemeChanged；如果此刻 Content 还不是 FrameworkElement
    /// （构造函数里、窗口 Activate 之前就是这种状态），内部抛 E_INVALIDARG 并直接
    /// RoFailFast 结束进程——异常码 0xc000027b，且是 stowed exception，
    /// 托管侧的 UnhandledException 完全接不到，日志里一个字都不会留下。
    ///
    /// 本机 10-19（0.8.0）与 10-22（0.8.2）两次崩溃的 WER 报告都是
    /// combase.dll / 偏移 0x5cf34，与 microsoft-ui-xaml#8364 描述的调用链一致。
    ///
    /// 因此：内容未就绪时推迟，等根元素 Loaded 后按当前样式重放。
    /// 旧实例在赋值成功之后再释放（顺序对齐官方 Gallery 示例）。
    /// </summary>
    private bool SetBackdrop(SystemBackdrop? next)
    {
        // 内容未就绪：推迟。赋值必须发生在 Content 已是 FrameworkElement 且已加载之后。
        if (Content is not FrameworkElement root)
        {
            // 连根元素都还没有：内容加载后会由 Activated 兜底重试（见构造函数里的订阅）。
            _backdropNeedsRetry = true;
            return false;
        }

        if (!root.IsLoaded)
        {
            _backdropNeedsRetry = true;
            if (!_backdropHookInstalled)
            {
                _backdropHookInstalled = true;
                root.Loaded += OnBackdropRootLoaded;
            }
            return false;
        }

        _backdropNeedsRetry = false;
        return ApplyBackdropNow(next);
    }

    private bool _backdropHookInstalled;

    /// <summary>是否还有未落实的背景材质（内容未就绪时被推迟）。</summary>
    private bool _backdropNeedsRetry;

    /// <summary>根元素首次加载完成：把构造函数期推迟的材质补上。</summary>
    private void OnBackdropRootLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement root)
            root.Loaded -= OnBackdropRootLoaded;
        _backdropHookInstalled = false;

        RetryPendingBackdrop();
    }

    /// <summary>
    /// 按「当前样式」重放被推迟的材质，而不是重放某个被记下来的实例：
    /// 期间设置页可能已经改过样式，重放旧实例会覆盖用户设置。
    /// </summary>
    private void RetryPendingBackdrop()
    {
        if (!_backdropNeedsRetry) return;
        if (_appliedBackdropStyle < 0) return;
        ApplyBackdropStyle(_appliedBackdropStyle);
    }

    /// <summary>真正执行赋值。调用点保证 Content 已是可用且已加载的 FrameworkElement。</summary>
    private bool ApplyBackdropNow(SystemBackdrop? next)
    {
        var previous = SystemBackdrop;
        try
        {
            SystemBackdrop = next;
        }
        catch (Exception ex)
        {
            // 兜底：即便判断失误，也不能让异常冒到 UI 线程。
            global::Momomi.App.App.WriteLog($"设置背景材质失败：{ex}");
            return false;
        }

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

        return true;
    }

    /// <summary>背景材质：0=亚克力(透出下方窗口)，1=Mica，2=纯色。不支持的会回退。</summary>
    public void ApplyBackdropStyle(int style)
    {
        // 同一样式不重复创建 controller：设置页与启动流程都会调用，
        // 重复赋值会让框架反复 attach/detach backdrop target。
        // 但上次若是「推迟」状态（内容还没就绪），必须重试，否则背景永远不生效。
        if (style == _appliedBackdropStyle && _appliedBackdropStyleApplied) return;
        _appliedBackdropStyle = style;

        SystemBackdrop? target = null;
        try
        {
            switch (style)
            {
                case 1 when MicaController.IsSupported():
                    target = new MicaBackdrop { Kind = MicaKind.Base };
                    break;
                case 2:
                    target = null;
                    break;
                default:
                    if (DesktopAcrylicController.IsSupported())
                        target = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                    else if (MicaController.IsSupported())
                        target = new MicaBackdrop { Kind = MicaKind.Base };
                    else
                        target = null;
                    break;
            }
        }
        catch
        {
        }

        // 只有真正完成赋值才算「已应用」；被推迟时保持未应用，等 Loaded 重试。
        _appliedBackdropStyleApplied = SetBackdrop(target);
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
                ApplyCaptionButtonTheme(root.ActualTheme);
                _tray?.ApplyTheme(root.ActualTheme);
                UpdateTrayState();
            };
        }
    }

    /// <summary>
    /// 标题栏系统按钮（最小化/最大化/关闭）由系统绘制，不随根元素 RequestedTheme 自动变化；
    /// 且运行时改色后系统不会自行重绘，需在主题变化时重新下发一次。
    /// </summary>
    private void ApplyCaptionButtonTheme(ElementTheme theme)
    {
        try
        {
            var dark = theme == ElementTheme.Dark;
            var titleBar = AppWindow.TitleBar;
            var fg = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                          : Windows.UI.Color.FromArgb(255, 0, 0, 0);
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedForegroundColor = fg;
            titleBar.ForegroundColor = fg;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(24, 255, 255, 255)
                : Windows.UI.Color.FromArgb(24, 0, 0, 0);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Mica 背景与标题栏按钮不受根元素 RequestedTheme 影响，需要单独跟随主题。
    /// </summary>
    public void ApplyThemeToChrome(string theme)
    {
        var dark = theme == "dark" || (theme == "default" && IsSystemDark());
        ApplyCaptionButtonTheme(dark ? ElementTheme.Dark : ElementTheme.Light);

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

        // 释放主界面页面的可视化树，然后把工作集交还系统。
        //
        // 这里必须调 EmptyWorkingSet：ReleasePageContent 后面的 GC.Collect
        // 只回收托管对象，而常驻内存的大头是 WinUI/D3D 的原生页
        // （交换链、着色器缓存、字形纹理），GC 完全碰不到它们。
        // 不主动剥出去，任务管理器里那几十上百兆会一直挂着。
        WorkingSetTrimmer.EndActivity(ActivitySources.MainWindow);
        ReleasePageContent();

        // 稍等一拍再修剪：Hide() 之后 XAML 还要跑完收起动画、释放交换链，
        // 立刻清工作集会和那批释放操作抢同一批页。
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1200).ConfigureAwait(true);

            // 页面树已置空、窗口已隐藏，此时压缩托管堆不会造成可见停顿，
            // 还能把 ReleasePageContent 掉下的页面一起还回去。
            WorkingSetTrimmer.CompactAndTrim("主窗口隐藏");
        });
    }

    /// <summary>隐藏主窗口到托盘（用于静默启动）。</summary>
    public void HideToTray() => MinimizeToTray();

    /// <summary>启动时直接最小化到托盘（供 App 依据设置调用）。</summary>
    public void StartMinimizedToTray() => MinimizeToTray();

    private void OnTrayLeftClick()
    {
        // H.NotifyIcon 的 LeftClickCommand 只在真实左键单击时触发（右键菜单不触发），无需再区分。
        //
        // 光标此刻正停在托盘图标上，所以在事件线程上立刻取显示器，作为面板的落点。
        // 不能等排到 UI 线程再取：那期间用户可能已经把鼠标移开了。
        var monitor = Mini.DpiLayout.MonitorForCursor();
        Dispatch(() => ToggleMiniWindow(monitor));
    }

    /// <summary>切换迷你面板。默认按托盘图标（光标）所在屏幕定位。</summary>
    public void ToggleMiniWindow()
        => ToggleMiniWindow(Mini.DpiLayout.MonitorForForegroundWindow());

    public void ToggleMiniWindow(IntPtr monitor)
    {
        EnsureMiniWindow();
        if (_miniWindow is null)
        {
            // 不在 UI 线程时创建是排队执行的，这次点击直接丢弃，避免操作未初始化的窗口。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_miniWindow is null) return;
                _miniWindow.ToggleVisible(monitor);
                UpdateTrayState();
            });
            return;
        }

        _miniWindow.ToggleVisible(monitor);
        UpdateTrayState();
    }

    public void ShowMiniPanel()
        => ShowMiniPanel(Mini.DpiLayout.MonitorForTrayTaskbar());

    public void ShowMiniPanel(IntPtr monitor)
    {
        EnsureMiniWindow();
        if (_miniWindow is null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_miniWindow is null) return;
                _miniWindow.Show(monitor);
                UpdateTrayState();
            });
            return;
        }

        _miniWindow.Show(monitor);
        UpdateTrayState();
    }

    /// <summary>
    /// 惰性创建迷你窗口并订阅关闭通知；拖拽收起关闭后会置空引用，下次调用重建。
    ///
    /// 必须在 UI 线程上执行：迷你面板、窗口显示与托盘点击可能分别来自后台线程的定时器、
    /// 内核事件与全局热键，而 WinUI 只允许在创建该窗口的线程上构建可视化树。
    /// </summary>
    private void EnsureMiniWindow()
    {
        if (_miniWindow is not null) return;
        if (DispatcherQueue.HasThreadAccess)
            CreateMiniWindow();
        else
            DispatcherQueue.TryEnqueue(CreateMiniWindow);
    }

    private void CreateMiniWindow()
    {
        if (_miniWindow is not null) return;
        var mini = new MiniWindow();
        _miniWindow = mini;
        // 窗口可能在主题切换之后才创建（预热、托盘点击），继承当前主题。
        var theme = global::Momomi.App.App.CurrentTheme;
        if (!string.Equals(theme, "default", StringComparison.OrdinalIgnoreCase))
        {
            try { mini.ApplyTheme(theme); } catch { }
        }
        // 首个迷你窗口预热完成后再后台创建下一个：MiniWindow 的构造会解析整份 XAML，
        // 如果在界面线程上同步回绕创建，用户点一次托盘图标就会卡一下。
        mini.Dismissed += (_, _) =>
        {
            _miniWindow = null;
            UpdateTrayState();
            DispatcherQueue.TryEnqueue(WarmupNextMiniWindow);
        };
    }

    private bool _warmingMiniWindow;

    /// <summary>在后台构建下一个迷你窗口，保证隐藏后再次呼出时不再付出构造开销。</summary>
    private void WarmupNextMiniWindow()
    {
        if (_miniWindow is not null || _warmingMiniWindow) return;
        _warmingMiniWindow = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _warmingMiniWindow = false;
            try
            {
                if (_miniWindow is null && !_forceExit) CreateMiniWindow();
            }
            catch (Exception ex)
            {
                global::Momomi.App.App.WriteLog($"预热迷你窗口失败：{ex}");
            }
        });
    }

    public void ApplyMiniPanelTheme(string theme) => _miniWindow?.ApplyTheme(theme);

    /// <summary>
    /// 把「迷你面板鼠标离开后自动收起」的秒数转给迷你窗口（0 = 关闭）。
    /// 设置页改动后立即调用，不需要重启。
    /// </summary>
    public void ApplyMiniAutoHide(int seconds)
    {
        if (DispatcherQueue.HasThreadAccess)
            _miniWindow?.SetAutoHideSeconds(seconds);
        else
            DispatcherQueue.TryEnqueue(() => _miniWindow?.SetAutoHideSeconds(seconds));
    }

    public void ShowAndActivate()
    {
        EnsureInitialPageLoaded();

        // 窗口要开始渲染了，登记活动来源，
        // 否则空闲看门狗可能在用户刚点开的瞬间清工作集，造成首帧卡顿。
        WorkingSetTrimmer.BeginActivity(ActivitySources.MainWindow);

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
        NavigateKeepingNoBackStack(_lastPageType);
    }

    /// <summary>隐藏到托盘时释放当前页面内容（保留页面类型），降低常驻内存。</summary>
    private void ReleasePageContent()
    {
        try
        {
            if (NavFrame.Content is not null)
            {
                _lastPageType = NavFrame.Content.GetType();

                // Frame.Content = null 不触发导航事件，必须显式解绑，
                // 否则页面的 ViewModel 会一直订阅 Core/AppSignals 的事件（详见 IPageLifecycle）。
                if (NavFrame.Content is IPageLifecycle lifecycle)
                {
                    try { lifecycle.DetachFromHost(); }
                    catch (Exception ex) { global::Momomi.App.App.WriteLog($"页面解绑失败：{ex}"); }
                }
            }

            NavFrame.Content = null;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"释放页面内容失败：{ex}");
        }
    }

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
            ModeSegmented.SelectedIndex = ModeSelector.SelectedIndex;
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

    private async void ModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_modeBarLoading) return;
        if (sender is not Segmented segmented) return;
        var index = segmented.SelectedIndex;
        if (index < 0) return;

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
            NavigateKeepingNoBackStack(typeof(SettingsPage));
            return;
        }
        if (args.SelectedItem is not NavigationViewItem item) return;
        switch (item.Tag)
        {
            case "dashboard": NavigateKeepingNoBackStack(typeof(DashboardPage)); break;
            case "proxies": NavigateKeepingNoBackStack(typeof(ProxiesPage)); break;
            case "connections": NavigateKeepingNoBackStack(typeof(ConnectionsPage)); break;
            case "rules": NavigateKeepingNoBackStack(typeof(RulesPage)); break;
            case "profiles": NavigateKeepingNoBackStack(typeof(ProfilesPage)); break;
            case "overrides": NavigateKeepingNoBackStack(typeof(OverridesPage)); break;
            case "traffic": NavigateKeepingNoBackStack(typeof(TrafficPage)); break;
            case "network": NavigateKeepingNoBackStack(typeof(NetworkPage)); break;
            case "logs": NavigateKeepingNoBackStack(typeof(LogsPage)); break;
        }
    }

    /// <summary>
    /// 导航并清空后退栈。
    ///
    /// 界面上没有后退按钮（IsBackButtonVisible=Collapsed），但 Frame 默认会把每一个
    /// 访问过的 Page 都放进 BackStack 强引用着——连接页/日志页这类重页面来回切几十次，
    /// 就是几十 MB 常驻，且永远不会被回收。
    /// </summary>
    private void NavigateKeepingNoBackStack(Type pageType)
    {
        try
        {
            NavFrame.Navigate(pageType);
            // Navigate 会把当前页压入 BackStack，这里立刻清掉，只保留正在显示的页。
            NavFrame.BackStack.Clear();
            NavFrame.ForwardStack.Clear();
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"导航到 {pageType.Name} 失败：{ex}");
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
