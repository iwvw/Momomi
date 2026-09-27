using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class ProxiesPage : Page
{
    public ProxiesViewModel ViewModel { get; }

    public ProxiesPage()
    {
        ViewModel = new ProxiesViewModel(
            global::Momomi.App.AppHost.Host.Core,
            DispatcherQueue);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        await ViewModel.LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void Node_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProxyItemViewModel node }) return;
        node.SelectRequested?.Invoke(node);
    }

    private void NodeTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProxyItemViewModel node }) return;
        node.TestRequested?.Invoke(node);
    }
}
