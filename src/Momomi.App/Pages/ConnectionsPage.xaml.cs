using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class ConnectionsPage : Page
{
    public ConnectionsViewModel ViewModel { get; }
    private bool _syncingSort;

    public ConnectionsPage()
    {
        ViewModel = new ConnectionsViewModel(
            global::Momomi.App.AppHost.Host.Core,
            DispatcherQueue);
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        ViewModel.RenderNow();
        SyncSortBox();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void SyncSortBox()
    {
        _syncingSort = true;
        try
        {
            SortBox.SelectedIndex = ViewModel.SortBy switch
            {
                "upload" => 1,
                "download" => 2,
                "process" => 3,
                "host" => 4,
                _ => 0,
            };
            RealtimeButton.IsChecked = !ViewModel.IsPaused;
        }
        finally
        {
            _syncingSort = false;
        }
    }

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSort) return;
        ViewModel.SortBy = SortBox.SelectedIndex switch
        {
            1 => "upload",
            2 => "download",
            3 => "process",
            4 => "host",
            _ => "duration",
        };
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
        => ViewModel.SortDescending = !ViewModel.SortDescending;

    private void Realtime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        ViewModel.IsPaused = button.IsChecked != true;
    }
}

