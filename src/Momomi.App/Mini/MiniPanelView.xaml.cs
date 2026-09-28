using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Momomi.App.ViewModels;

namespace Momomi.App.Mini;

public sealed partial class MiniPanelView : UserControl
{
    public MiniPanelViewModel ViewModel { get; }

    public event EventHandler? OpenFullRequested;
    public event EventHandler? CollapseRequested;

    private bool _modeLoading;

    public MiniPanelView()
    {
        ViewModel = new MiniPanelViewModel(
            global::Momomi.App.AppHost.Host,
            DispatcherQueue);
        InitializeComponent();
        ViewModel.ModeSelector.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModeSelectorViewModel.SelectedIndex)
                or nameof(ModeSelectorViewModel.SystemProxyOn)
                or nameof(ModeSelectorViewModel.TunOn))
            {
                SyncModeBar();
            }
        };

        // 拖拽收起手势：整面板可拖，但落在交互控件上时让控件优先处理点击。
        RootGrid.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(OnRootPointerPressed),
            handledEventsToo: true);
        RootGrid.AddHandler(
            PointerMovedEvent,
            new PointerEventHandler(OnRootPointerMoved),
            handledEventsToo: true);
        RootGrid.AddHandler(
            PointerReleasedEvent,
            new PointerEventHandler(OnRootPointerReleased),
            handledEventsToo: true);
        RootGrid.AddHandler(
            PointerCanceledEvent,
            new PointerEventHandler(OnRootPointerCanceled),
            handledEventsToo: true);
    }

    /// <summary>拖拽手势开始（位移已超过阈值，进入拖拽）。</summary>
    public event EventHandler? DragGestureStarted;

    /// <summary>拖拽手势移动（鼠标位置变化即触发）。</summary>
    public event EventHandler? DragGestureMoved;

    /// <summary>拖拽手势结束（松开/取消），由宿主决定收起或回弹。</summary>
    public event EventHandler? DragGestureEnded;

    /// <summary>按下后位移超过该阈值才视为拖拽；阈值内的轻点交由控件正常处理（点击）。</summary>
    private const double DragThresholdDip = 8;

    private bool _dragArmed;
    private bool _dragActive;
    private uint? _dragPointerId;
    private Windows.Foundation.Point _pressPoint;

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragArmed || _dragActive) return;
        if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
        _dragArmed = true;
        _dragPointerId = e.Pointer.PointerId;
        _pressPoint = e.GetCurrentPoint(RootGrid).Position;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;
        if (_dragActive)
        {
            DragGestureMoved?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!_dragArmed) return;

        var pos = e.GetCurrentPoint(RootGrid).Position;
        double dx = pos.X - _pressPoint.X;
        double dy = pos.Y - _pressPoint.Y;
        if (Math.Abs(dx) <= DragThresholdDip && Math.Abs(dy) <= DragThresholdDip) return;

        // 位移超阈值：从"轻点"转为"拖拽"，接管指针，后续事件只发给本控件。
        _dragArmed = false;
        _dragActive = true;
        RootGrid.CapturePointer(e.Pointer);
        DragGestureStarted?.Invoke(this, EventArgs.Empty);
        DragGestureMoved?.Invoke(this, EventArgs.Empty);
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;
        if (_dragActive)
        {
            _dragActive = false;
            _dragArmed = false;
            _dragPointerId = null;
            RootGrid.ReleasePointerCapture(e.Pointer);
            DragGestureEnded?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            // 未进入拖拽即视为轻点：不接管，让控件（按钮等）正常收到 Click。
            _dragArmed = false;
            _dragPointerId = null;
        }
    }

    private void OnRootPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _dragPointerId) return;
        bool wasActive = _dragActive;
        _dragActive = false;
        _dragArmed = false;
        _dragPointerId = null;
        if (wasActive) DragGestureEnded?.Invoke(this, EventArgs.Empty);
    }

    public void SetSolidBackground(bool solid)
    {
        SolidBackdrop.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task InitializeAsync()
    {
        await ViewModel.LoadModeAsync();
        SyncModeBar();
    }

    /// <summary>面板每次显示时重新拉取模式与开关状态，保证与主窗口一致。</summary>
    public async Task RefreshOnShowAsync()
    {
        await ViewModel.RefreshOnShowAsync();
        SyncModeBar();
    }

    private void SyncModeBar()
    {
        _modeLoading = true;
        try
        {
            var index = ViewModel.ModeSelector.SelectedIndex;
            ModeRuleButton.IsChecked = index == 0;
            ModeGlobalButton.IsChecked = index == 1;
            ModeDirectButton.IsChecked = index == 2;

            var selector = ViewModel.ModeSelector;
            if (SystemProxySwitch.IsOn != selector.SystemProxyOn)
                SystemProxySwitch.IsOn = selector.SystemProxyOn;
            if (TunSwitch.IsOn != selector.TunOn)
                TunSwitch.IsOn = selector.TunOn;
        }
        finally
        {
            _modeLoading = false;
        }
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modeLoading) return;
        if (sender is not ToggleButton button) return;
        if (button.Tag is not string tag || !int.TryParse(tag, out var index)) return;

        if (ViewModel.ModeSelector.SelectedIndex == index)
        {
            SyncModeBar();
            return;
        }

        await ViewModel.SelectModeAsync(index);
        SyncModeBar();
    }

    private void SystemProxy_Toggled(object sender, RoutedEventArgs e)
    {
        if (_modeLoading) return;
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ViewModel.ModeSelector.SystemProxyOn) return;
        _ = ViewModel.SetSystemProxyAsync(toggle.IsOn);
    }

    private void Tun_Toggled(object sender, RoutedEventArgs e)
    {
        if (_modeLoading) return;
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn == ViewModel.ModeSelector.TunOn) return;
        _ = ViewModel.SetTunAsync(toggle.IsOn);
    }

    private void Node_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Tag is not MiniProxyGroupViewModel group) return;
        if (combo.SelectedItem is not MiniProxyNodeViewModel node) return;
        // 重排集合或程序化同步 Selected 时，ComboBox 会触发 SelectionChanged；
        // 只有用户真实改选（选择引用确实变化）才处理，否则会与 ProxiesChanged 形成无限重载循环。
        if (ReferenceEquals(group.Selected, node)) return;
        group.Selected = node;
        _ = ViewModel.SelectNodeAsync(group, node);
    }

    private void TestGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not MiniProxyGroupViewModel group) return;
        _ = ViewModel.TestGroupAsync(group);
    }

    private void OpenFull_Click(object sender, RoutedEventArgs e)
        => OpenFullRequested?.Invoke(this, EventArgs.Empty);

    private void Collapse_Click(object sender, RoutedEventArgs e)
        => CollapseRequested?.Invoke(this, EventArgs.Empty);
}
