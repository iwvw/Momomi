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
        RangeBar.SelectedItem = RangeBar.Items[ViewModel.SelectedRangeIndex];
        await ViewModel.LoadAsync();
        _loading = false;
    }

    private void Range_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_loading) return;
        ViewModel.SelectedRangeIndex = sender.Items.IndexOf(sender.SelectedItem);
    }
}