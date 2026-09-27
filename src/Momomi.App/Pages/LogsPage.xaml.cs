using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class LogsPage : Page
{
    public LogsViewModel ViewModel { get; }

    public LogsPage()
    {
        ViewModel = new LogsViewModel(
            global::Momomi.App.AppHost.Host.Core,
            DispatcherQueue);
        InitializeComponent();
        LevelBox.SelectedIndex = ViewModel.Level switch
        {
            "debug" => 0,
            "warning" => 2,
            "error" => 3,
            _ => 1,
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void Level_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LevelBox.SelectedValue is string level)
            ViewModel.Level = level;
    }
}
