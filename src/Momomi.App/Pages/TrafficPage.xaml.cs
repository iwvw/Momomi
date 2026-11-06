using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class TrafficPage : Page
{
    public TrafficViewModel ViewModel { get; }
    private bool _loading;

    public TrafficPage()
    {
        ViewModel = new TrafficViewModel(
            global::Momomi.App.AppHost.Host,
            DispatcherQueue);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loading = true;
        RangeBar.SelectedIndex = ViewModel.SelectedRangeIndex;
        await ViewModel.LoadAsync();
        _loading = false;
    }

    private void Range_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CommunityToolkit.WinUI.Controls.Segmented segmented) return;
        if (segmented.SelectedIndex < 0) return;
        ViewModel.SelectedRangeIndex = segmented.SelectedIndex;
    }
}