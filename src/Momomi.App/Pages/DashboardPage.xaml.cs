using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = new DashboardViewModel(
            global::Momomi.App.AppHost.Host,
            DispatcherQueue);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void GoProfiles_Click(object sender, RoutedEventArgs e) =>
        global::Momomi.App.App.Main?.NavigateToTag("profiles");

    private void GoSettings_Click(object sender, RoutedEventArgs e) =>
        global::Momomi.App.App.Main?.NavigateToTag("settings");

    private void GoProxies_Click(object sender, RoutedEventArgs e) =>
        global::Momomi.App.App.Main?.NavigateToTag("proxies");

    private void GoNetwork_Click(object sender, RoutedEventArgs e) =>
        global::Momomi.App.App.Main?.NavigateToTag("network");

    private void GoTraffic_Click(object sender, RoutedEventArgs e) =>
        global::Momomi.App.App.Main?.NavigateToTag("traffic");

    private void GoLogs_Click(object sender, RoutedEventArgs e) =>
        global::Momomi.App.App.Main?.NavigateToTag("logs");

    private void GoConnections_Click(object sender, RoutedEventArgs e) =>
        global::Momomi.App.App.Main?.NavigateToTag("connections");
}