using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
