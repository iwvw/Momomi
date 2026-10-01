using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, DesignWidth, DesignHeight, MarginRight, MarginBottom);
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

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        ApplyBackdropStyle(await ReadBackdropStyleAsync());
        await Panel.InitializeAsync();
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
                        SetBackdrop(new global::Momomi.App.Controls.AlwaysActiveAcrylicBackdrop());
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

    /// <summary>替换背景材质并释放旧实例，避免切换材质时泄漏控制器。</summary>
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
        Panel.ViewModel.Dispose();
        _slider.Dispose();
        Close();
    }

    public bool IsVisible => _state is VisState.Showing or VisState.Shown;

    public void ToggleVisible()
    {
        if (_state is VisState.Showing or VisState.Shown) Hide();
        else Show();
    }

    public void Show()
    {
        bool wasHidden = _state is VisState.Hidden;
        _state = VisState.Showing;

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, DesignWidth, DesignHeight, MarginRight, MarginBottom);
        var hiddenRect = SlideMath.Hidden(_targetRect, SlideDirection.BottomUp);

        Windows.Graphics.RectInt32 start = hiddenRect;
        if (!wasHidden)
        {
            var current = DpiLayout.GetCurrentRect(_hwnd);
            if (current.Width > 0 && current.Height > 0) start = current;
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
        _ = Panel.RefreshOnShowAsync();
    }

    private void OnShowCompleted()
    {
        if (_state != VisState.Showing) return;
        _state = VisState.Shown;
    }

    public void Hide()
    {
        if (_state is VisState.Hidden or VisState.Hiding) return;
        _state = VisState.Hiding;

        _statsTimer.Stop();

        _targetRect = DpiLayout.ComputeBottomRight(_appWindow, DesignWidth, DesignHeight, MarginRight, MarginBottom);
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
