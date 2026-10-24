using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Momomi.Core.Services;
using WinRT.Interop;

namespace Momomi.App.Mini;

public sealed partial class MiniWindow : Window
{
    private const int DesignWidth = 380;
    private const int DesignHeight = 540;
    private const int MarginRight = 12;
    private const int MarginBottom = 12;
    private const int ShowDurationMs = 260;
    private const int HideDurationMs = 220;

    private enum VisState
    {
        Hidden,
        Showing,
        Shown,
        Hiding,
    }

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly WindowSlider _slider;
    private readonly DispatcherTimer _statsTimer;
    private bool _forceClose;
    private VisState _state = VisState.Hidden;
    private Windows.Graphics.RectInt32 _targetRect;

    // ---- 鼠标离开后自动收起 ----
    //
    // 只在「鼠标已经移出面板」时计时：鼠标停在面板上时不收起，否则用户正在选节点
    // 会被打断。移到面板外才开始倒计时，移回来立即取消。
    private DispatcherTimer? _autoHideTimer;
    private int _autoHideSeconds;
    private bool _pointerInside;

    public MiniWindow()
    {
        InitializeComponent();

        Title = "Momomi";
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _slider = new WindowSlider(_hwnd, DispatcherQueue);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        WindowChrome.SetToolWindow(_hwnd);
        WindowChrome.SetRoundCorner(_hwnd);
        WindowChrome.RemoveNonClientFrame(_hwnd);
        WindowChrome.SetTopmost(_hwnd);

        // 初始位置只用于创建窗口，真正呼出时会按托盘图标所在屏幕重算。
        _targetRect = DpiLayout.ComputeBottomRight(
            _appWindow, DesignWidth, DesignHeight, MarginRight, MarginBottom,
            DpiLayout.MonitorForTrayTaskbar());
        _appWindow.MoveAndResize(_targetRect);

        // 窗口创建后默认可见：立即移到屏幕外并隐藏，避免预热/首次构造时闪出。
        _appWindow.MoveAndResize(SlideMath.Hidden(_targetRect, SlideDirection.BottomUp));
        _appWindow.Hide();

        _appWindow.Closing += OnClosing;

        Panel.OpenFullRequested += (_, _) => OpenFullPanel();
        Panel.CollapseRequested += (_, _) => Hide();
        Panel.DragGestureStarted += (_, _) => OnDragStarted();
        Panel.DragGestureMoved += (_, _) => OnDragMoved();
        Panel.DragGestureEnded += (_, _) => OnDragEnded();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statsTimer.Tick += async (_, _) => await Panel.ViewModel.LoadAsync();

        // 鼠标进出面板：用于「离开后自动收起」的计时。
        // 用隧道事件在根元素上监听，子控件（ComboBox 弹出层等）不会漏掉。
        Panel.PointerEntered += (_, _) => OnPointerEntered();
        Panel.PointerExited += (_, _) => OnPointerExited();

        _ = InitializeAsync();
    }

    private void OnPointerEntered()
    {
        _pointerInside = true;
        _autoHideTimer?.Stop();
    }

    private void OnPointerExited()
    {
        _pointerInside = false;
        RestartAutoHideTimer();
    }

    /// <summary>
    /// 是否有弹出层正在显示（ComboBox 下拉、Flyout 等）。
    ///
    /// 鼠标移进 Popup 时 Panel 会收到 PointerExited，但用户其实还在操作面板。
    /// 不做这个判断的话，点开节点下拉框选节点时面板会被自动收起。
    /// </summary>
    private bool IsPopupOpen()
    {
        try
        {
            return Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Panel.XamlRoot).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 设置「鼠标离开后自动收起」的秒数。0 表示关闭。
    /// 由设置页在用户改动后调用，立即生效，不需要重启。
    /// </summary>
    public void SetAutoHideSeconds(int seconds)
    {
        _autoHideSeconds = Math.Max(0, seconds);
        _autoHideTimer?.Stop();
        RestartAutoHideTimer();
    }

    private void RestartAutoHideTimer()
    {
        if (_autoHideSeconds <= 0) return;
        if (_state is not VisState.Shown) return;

        // 鼠标还在面板里，不计时。
        if (_pointerInside) return;

        if (_autoHideTimer is null)
        {
            _autoHideTimer = new DispatcherTimer();
            _autoHideTimer.Tick += OnAutoHideTick;
        }

        _autoHideTimer.Stop();
        _autoHideTimer.Interval = TimeSpan.FromSeconds(_autoHideSeconds);
        _autoHideTimer.Start();
    }

    private void OnAutoHideTick(object? sender, object e)
    {
        _autoHideTimer?.Stop();

        // 倒计时期间鼠标又回来了：不收起。
        if (_pointerInside) return;
        if (_state is not VisState.Shown) return;

        // 用户正在下拉框里选节点：等他选完再算。
        if (IsPopupOpen())
        {
            RestartAutoHideTimer();
            return;
        }

        Hide();
    }

    private async Task InitializeAsync()
    {
        ApplyBackdropStyle(await ReadBackdropStyleAsync());
        SetAutoHideSeconds(await ReadAutoHideSecondsAsync());
        await Panel.InitializeAsync();
    }

    /// <summary>读取「鼠标离开后自动收起」的秒数，0 表示关闭。</summary>
    private static async Task<int> ReadAutoHideSecondsAsync()
    {
        try
        {
            var v = await global::Momomi.App.AppHost.Host.Settings
                .GetIntAsync("ui.miniAutoHideSeconds", 0);
            return v is < 0 or > 3600 ? 0 : v;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<int> ReadBackdropStyleAsync()
    {
        try
        {
            return await global::Momomi.App.AppHost.Host.Settings.GetIntAsync("ui.backdropStyle", 0);
        }
        catch
        {
            return 0;
        }
    }

    public void ApplyTheme(string theme)
    {
        Panel.RequestedTheme = theme switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>背景材质：0=亚克力(透出下方窗口)，1=Mica，2=纯色。与主窗口保持一致。</summary>
    public void ApplyBackdropStyle(int style)
    {
        try
        {
            switch (style)
            {
                case 1 when MicaController.IsSupported():
                    SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
                    Panel.SetSolidBackground(false);
                    break;
                case 2:
                    SetBackdrop(null);
                    Panel.SetSolidBackground(true);
                    break;
                default:
                    if (global::Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
                    {
                        SetBackdrop(new global::Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop());
                        Panel.SetSolidBackground(false);
                    }
                    else if (MicaController.IsSupported())
                    {
                        SetBackdrop(new MicaBackdrop { Kind = MicaKind.Base });
                        Panel.SetSolidBackground(false);
                    }
                    else
                    {
                        SetBackdrop(null);
                        Panel.SetSolidBackground(true);
                    }
                    break;
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 替换背景材质并释放旧实例，避免切换材质时泄漏控制器。
    ///
    /// 与主窗口同理：Window.SystemBackdrop 赋值时框架会内部订阅
    /// Content.ActualThemeChanged，Content 未就绪时会 E_INVALIDARG → RoFailFast
    /// （0xc000027b，托管侧接不到）。所以先确认 Panel 已加载再赋值。
    /// </summary>
    private void SetBackdrop(SystemBackdrop? next)
    {
        if (!Panel.IsLoaded)
        {
            // 面板还没 Loaded：记下目标材质，Loaded 后补上（MiniWindow.InitializeAsync
            // 是在构造后异步跑的，正常会先走到这里）。
            _pendingBackdrop = next;
            _hasPendingBackdrop = true;
            if (!_backdropHookInstalled)
            {
                _backdropHookInstalled = true;
                void OnPanelLoaded(object? sender, RoutedEventArgs e)
                {
                    Panel.Loaded -= OnPanelLoaded;
                    _backdropHookInstalled = false;
                    if (!_hasPendingBackdrop) return;
                    var pending = _pendingBackdrop;
                    _pendingBackdrop = null;
                    _hasPendingBackdrop = false;
                    ApplyBackdropNow(pending);
                }
                Panel.Loaded += OnPanelLoaded;
            }
            return;
        }

        ApplyBackdropNow(next);
    }

    private bool _backdropHookInstalled;
    private bool _hasPendingBackdrop;
    private SystemBackdrop? _pendingBackdrop;

    private void ApplyBackdropNow(SystemBackdrop? next)
    {
        var previous = SystemBackdrop;
        try
        {
            SystemBackdrop = next;
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"迷你面板设置背景材质失败：{ex}");
            return;
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
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose) return;
        args.Cancel = true;
        Hide();
    }

    public void ForceClose()
    {
        _forceClose = true;
        _statsTimer.Stop();
        // 定时器不停的话，窗口销毁后它仍会触发，调用 Hide() 操作已关闭的窗口。
        _autoHideTimer?.Stop();

        // 窗口即将销毁，必须摘掉活动标记：
        // 如果它是在显示状态下被销毁的（退出程序、渲染出错后重建），
        // 标记就会永远留着，导致工作集回收彻底失效。
        WorkingSetTrimmer.EndActivity(ActivitySources.MiniWindow);

        Panel.ViewModel.Dispose();
        _slider.Dispose();
        // 解挂子类化，避免窗口销毁后仍留着指向托管委托的原生入口。
        try { WindowChrome.RemoveSubclass(_hwnd); } catch { }
        Close();
    }

    public bool IsVisible => _state is VisState.Showing or VisState.Shown;

    public void ToggleVisible() => ToggleVisible(IntPtr.Zero);

    /// <summary>
    /// 切换显示。显示时按 <paramref name="monitor"/> 指定的屏幕定位（IntPtr.Zero 表示跟随窗口自身）。
    /// </summary>
    public void ToggleVisible(IntPtr monitor)
    {
        if (_state is VisState.Showing or VisState.Shown) Hide();
        else Show(monitor);
    }

    /// <summary>
    /// 重算迷你面板的右下角位置。
    /// </summary>
    /// <param name="monitor">
    /// 目标显示器。呼出时传托盘图标所在屏幕，收起时传 IntPtr.Zero（跟随窗口自身）。
    /// </param>
    private void UpdateTargetRect(IntPtr monitor)
    {
        _targetRect = DpiLayout.ComputeBottomRight(
            _appWindow, DesignWidth, DesignHeight, MarginRight, MarginBottom, monitor);
    }

    public void Show() => Show(IntPtr.Zero);

    /// <summary>
    /// 显示面板。<paramref name="monitor"/> 为目标显示器（托盘图标所在屏幕），
    /// IntPtr.Zero 表示跟随窗口当前所在屏幕。
    /// </summary>
    public void Show(IntPtr monitor)
    {
        bool wasHidden = _state is VisState.Hidden;
        _state = VisState.Showing;

        // 每次呼出都当作鼠标不在面板上：计时从「显示完成」开始，
        // 用户把鼠标移进来时 PointerEntered 会立刻取消。
        _pointerInside = false;
        _autoHideTimer?.Stop();

        // 马上要开始渲染动画，登记活动来源，避免这时被清工作集。
        WorkingSetTrimmer.BeginActivity(ActivitySources.MiniWindow);

        // 每次呼出都重新定位：托盘图标可能已经换到另一块屏幕（改缩放后同样需要）。
        UpdateTargetRect(monitor);
        var hiddenRect = SlideMath.Hidden(_targetRect, SlideDirection.BottomUp);

        Windows.Graphics.RectInt32 start = hiddenRect;
        if (!wasHidden)
        {
            var current = DpiLayout.GetCurrentRect(_hwnd);
            // 面板已经在别的位置显示着（例如按热键再呼出）：就地展开。
            // 但如果它当前在另一块屏幕上，就不要横跨两个屏幕滑过去——直接在新屏幕上重新滑出。
            if (current.Width > 0 && current.Height > 0
                && DpiLayout.IsWithinWorkArea(monitor, current))
            {
                start = current;
            }
        }
        else
        {
            _appWindow.MoveAndResize(start);
        }

        _appWindow.Show();
        WindowChrome.BringToForeground(_hwnd);
        // 必须最后插到任务栏下方：任务栏遮住窗口，滑动时才有"从任务栏后钻出"的观感。
        WindowChrome.PlaceBelowTaskbar(_hwnd);

        // 呼出：fast-out / slow-in，末尾缓缓减速，对齐 Fluent 展开曲线。
        _slider.Animate(start, _targetRect, 255, 255, ShowDurationMs, SlideEasing.EaseOut, OnShowCompleted);

        _statsTimer.Start();
    }

    private void OnShowCompleted()
    {
        if (_state != VisState.Showing) return;
        _state = VisState.Shown;
        // 滑出动画结束后再拉取模式/开关/订阅/代理：避免冷页换入与网络加载突发叠加在动画起步上造成卡顿。
        _ = Panel.RefreshOnShowAsync();

        // 动画期间鼠标可能在面板外，此时才开始计时；在面板内则等它离开。
        // 不直接读 IsPointerOver：MiniPanelView 是 UserControl，该属性在
        // WinUI 的 UserControl 上不可靠（构造后常为 false），交给 Pointer 事件驱动。
        RestartAutoHideTimer();
    }

    public void Hide()
    {
        if (_state is VisState.Hidden or VisState.Hiding) return;
        _state = VisState.Hiding;

        _statsTimer.Stop();
        _autoHideTimer?.Stop();

        // 收起跟随窗口自身所在屏幕：此时光标可能已经移走，动画不该跨屏跳。
        UpdateTargetRect(IntPtr.Zero);
        var hiddenRect = SlideMath.Hidden(_targetRect, SlideDirection.BottomUp);

        var current = DpiLayout.GetCurrentRect(_hwnd);
        if (current.Width <= 0 || current.Height <= 0) current = _targetRect;

        // 收起前把窗口插回任务栏下方：向下滑时被任务栏遮住，产生"从任务栏后藏入"的观感（与拖拽收起一致）。
        WindowChrome.PlaceBelowTaskbar(_hwnd);

        // 收起：反向曲线，开始缓缓起步，对齐 Fluent 退出动画。
        _slider.Animate(current, hiddenRect, 255, 255, HideDurationMs, SlideEasing.EaseIn, OnHideCompleted);
    }

    private void OnHideCompleted()
    {
        if (_state != VisState.Hiding) return;
        _appWindow.Hide();
        _appWindow.MoveAndResize(_targetRect);
        _state = VisState.Hidden;

        // 面板已收起、不再渲染，把工作集交还系统。
        // 这是最频繁开合的窗口，每次收起都会留下一些 D3D/字形缓存，
        // 不主动剥出去就会一直驻留——托盘常驻场景下这是内存的主要来源。
        WorkingSetTrimmer.EndActivity(ActivitySources.MiniWindow);

        // 延后一拍：现在还在 WindowSlider 的动画回调里，
        // 立刻清工作集会和它紧接着的收尾操作抢同一批页。
        _ = DispatcherQueue.TryEnqueue(() => WorkingSetTrimmer.TryTrim("迷你面板收起"));
    }

    private void OpenFullPanel()
    {
        Hide();
        global::Momomi.App.App.Main?.ShowAndActivate();
    }

    // ---- 下拉关闭手势：按住空白处往下拖，跟手移动；超阈值下滑收起并关闭，否则回弹 ----

    private const double DragDismissThresholdRatio = 0.28;

    private bool _dragging;
    private Windows.Graphics.RectInt32 _dragStartRect;
    private int _dragStartCursorY;

    private void OnDragStarted()
    {
        if (_state != VisState.Shown) return;
        _slider.Cancel();
        // 拖拽全程让任务栏盖住窗口：插到任务栏在 topmost 组内的正下方，避免盖在任务栏上方。
        WindowChrome.PlaceBelowTaskbar(_hwnd);
        _dragging = true;
        _dragStartRect = DpiLayout.GetCurrentRect(_hwnd);
        GetCursorPos(out var p);
        _dragStartCursorY = p.Y;
    }

    private void OnDragMoved()
    {
        if (!_dragging) return;
        GetCursorPos(out var p);
        int dy = p.Y - _dragStartCursorY;
        // 跟手：窗口随鼠标移动；钳制在起始位置之下，避免拖出屏幕顶部（可往回拖到原位取消）。
        int y = Math.Max(_dragStartRect.Y, _dragStartRect.Y + dy);
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            _dragStartRect.X, y, _dragStartRect.Width, _dragStartRect.Height));
    }

    private void OnDragEnded()
    {
        if (!_dragging) return;
        _dragging = false;
        GetCursorPos(out var p);
        int dy = p.Y - _dragStartCursorY;
        var current = DpiLayout.GetCurrentRect(_hwnd);

        if (dy >= _dragStartRect.Height * DragDismissThresholdRatio)
            DismissFromDrag(current);
        else
            SnapBackFromDrag(current);
    }

    /// <summary>拖拽收起（下滑到底后真正关闭），通知宿主置空引用。</summary>
    private void DismissFromDrag(Windows.Graphics.RectInt32 current)
    {
        _statsTimer.Stop();
        // 下滑前把窗口插到任务栏下层，与自然收起一致：被任务栏遮住、从底部藏入。
        WindowChrome.PlaceBelowTaskbar(_hwnd);
        var hidden = SlideMath.Hidden(current, SlideDirection.BottomUp);
        _slider.Animate(current, hidden, 255, 255, 240, SlideEasing.EaseIn, () =>
        {
            _state = VisState.Hidden;
            _appWindow.Hide();
            _appWindow.MoveAndResize(_targetRect);
            _forceClose = true;
            // 通知宿主（MainWindow）把 _miniWindow 置空，避免引用失效窗口。
            Dismissed?.Invoke(this, EventArgs.Empty);
            Panel.ViewModel.Dispose();
            _slider.Dispose();
            try { WindowChrome.RemoveSubclass(_hwnd); } catch { }
            Close();
        });
    }

    /// <summary>未过阈值：平滑弹回右下角原位（不关闭）。</summary>
    private void SnapBackFromDrag(Windows.Graphics.RectInt32 current)
    {
        _slider.Animate(current, _targetRect, 255, 255, 220, SlideEasing.EaseOut, null);
    }

    /// <summary>拖拽收起关闭时触发，宿主据此清理窗口引用。</summary>
    public event EventHandler? Dismissed;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
