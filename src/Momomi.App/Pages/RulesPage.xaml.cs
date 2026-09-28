using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class RulesPage : Page
{
    public RulesViewModel ViewModel { get; }

    public RulesPage()
    {
        ViewModel = new RulesViewModel(
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

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not RuleRowViewModel row) return;
        _ = ViewModel.ToggleRuleCommand.ExecuteAsync(row);
    }
}