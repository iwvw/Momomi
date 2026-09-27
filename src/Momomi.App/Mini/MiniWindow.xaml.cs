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

        _appWindow.Closing += OnClosing;

        Panel.OpenFullRequested += (_, _) => OpenFullPanel();
        Panel.CollapseRequested += (_, _) => Hide();

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

    /// <summary>背景材质：0=Mica，1=亚克力(Mica Alt)，2=纯色。与主窗口保持一致。</summary>
    public void ApplyBackdropStyle(int style)
    {
        try
        {
            switch (style)
            {
                case 1 when MicaController.IsSupported():
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                    Panel.SetSolidBackground(false);
                    break;
                case 2:
                    SystemBackdrop = null;
                    Panel.SetSolidBackground(true);
                    break;
                default:
                    if (MicaController.IsSupported())
                    {
                        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                        Panel.SetSolidBackground(false);
                    }
                    else if (DesktopAcrylicController.IsSupported())
                    {
                        SystemBackdrop = new DesktopAcrylicBackdrop();
                        Panel.SetSolidBackground(false);
                    }
                    else
                    {
                        SystemBackdrop = null;
                        Panel.SetSolidBackground(true);
                    }
                    break;
            }
        }
        catch
        {
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
        _slider.Dispose();
        Close();
    }

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

        _slider.Animate(start, _targetRect, 255, 255, ShowDurationMs, easeOut: true, OnShowCompleted);

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

        _slider.Animate(current, hiddenRect, 255, 255, HideDurationMs, easeOut: true, OnHideCompleted);
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
}
